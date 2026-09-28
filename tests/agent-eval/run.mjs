// Run behavioral scenarios in fresh, ephemeral Pi sessions against a disposable Revit fixture.
//
// Usage:
//   node tests/agent-eval/run.mjs --fixture <fixture.json> --out <directory outside the repo>
//        [--scenarios id,id] [--repeat N] [--model provider/id | --models a,b] [--allow-requires] [--no-reset]
//
// fixture.json: { "title", "document_id", "file", "disposable": true, "model"?, "values": { "sheet_number": "...", ... } }
// The runner refuses to start a scenario unless the bridge's active document is exactly the
// fixture (document ID and file path). Pi settings are never changed: the model is passed per
// invocation, sessions are ephemeral, and the guard blocks saving and exports.
//
// Clean runs (inv: comparable evaluation). The first call records a baseline of the freshly
// opened fixture in <out>/baseline.json. Before every run the harness resets the fixture to it:
// it deletes every element created after the baseline and restores pinned state, then compares
// a fingerprint (element range and counts, pinned elements, every view's signature). A run whose
// start state differs is never started. Resetting needs "disposable": true in fixture.json and
// never saves. After each run the harness records what the run created and changed, and runs the
// scenario's ground-truth check. With several models, runs interleave (A1 B1 A2 B2 ...).
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync, createWriteStream } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, arg, i, all) => arg.startsWith('--')
  ? [...pairs, [arg.slice(2), all[i + 1]?.startsWith('--') || all[i + 1] === undefined ? 'true' : all[i + 1]]] : pairs, []));
if (!args.fixture || !args.out) throw new Error('usage: run.mjs --fixture <fixture.json> --out <directory> [--scenarios a,b] [--repeat N] [--model m | --models a,b]');
const out = path.resolve(args.out);
if (!path.relative(root, out).startsWith('..')) throw new Error('--out must be outside the repository so traces never enter source control');
const fixture = JSON.parse(readFileSync(args.fixture, 'utf8'));
const models = (args.models ?? args.model ?? fixture.model ?? '').split(',').map(m => m.trim()).filter(Boolean);
if (!models.length) throw new Error('Set --model, --models or fixture.model (provider/id); the configured default is never changed.');
const reset = args['no-reset'] !== 'true';
if (reset && fixture.disposable !== true) throw new Error('Resetting deletes objects created during the round; set "disposable": true in fixture.json only for a disposable copy, or pass --no-reset.');
const catalog = JSON.parse(readFileSync(path.join(here, 'scenarios.json'), 'utf8')).scenarios;
const selected = (args.scenarios ? args.scenarios.split(',') : catalog.map(s => s.id)).map(id => catalog.find(s => s.id === id) ?? (() => { throw new Error(`unknown scenario ${id}`); })());
const contracts = new Map(JSON.parse(readFileSync(path.join(root, 'skills', 'pi-revit', 'contracts.generated.json'), 'utf8')).tools.map(t => [t.name, t]));
const manifest = JSON.parse(readFileSync(path.join(root, 'skills', 'pi-revit', 'tool-manifest.json'), 'utf8'));
const piPath = process.env.PI_CODING_AGENT_PATH ?? path.join(path.dirname(process.execPath), 'node_modules', '@earendil-works', 'pi-coding-agent');
const cli = path.join(piPath, 'dist', 'bundle', 'cli.js');
if (!existsSync(cli)) throw new Error(`Pi CLI not found at ${cli}; set PI_CODING_AGENT_PATH`);

async function bridge(tool, body, timeoutMs = 120000) {
  const info = JSON.parse(readFileSync(path.join(process.env.APPDATA, 'RevitBridge', 'bridge.json'), 'utf8'));
  const response = await fetch(`${info.baseUrl}/tools/${tool}/execute?token=${encodeURIComponent(info.token)}&timeout_ms=${timeoutMs}`,
    { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body), signal: AbortSignal.timeout(timeoutMs + 5000) });
  const value = await response.json();
  if (!response.ok || value.success === false || value.error) throw new Error(`${tool}: ${value.message ?? JSON.stringify(value).slice(0, 600)}`);
  return value.details?.payload;
}

async function activeDocument() {
  return (await bridge('get_model_overview', {}, 30000))?.project ?? null;
}

async function assertFixtureActive() {
  const project = await activeDocument().catch(error => { throw new Error(`Cannot confirm the active document: ${error.message}`); });
  if (project?.documentId !== fixture.document_id || project?.filePath !== fixture.file)
    throw new Error(`Refusing to run: the active document is "${project?.title}", not the fixture "${fixture.title}".`);
}

/**
 * Run a harness script (tests/agent-eval/harness) on the fixture through execute_csharp. The
 * script writes its complete result to out_file (execute_csharp caps returned lists at 100 items).
 */
async function harness(name, inputs = {}) {
  await assertFixtureActive();
  const code = readFileSync(path.join(here, 'harness', 'common.cs'), 'utf8') + '\n' + readFileSync(path.join(here, 'harness', `${name}.cs`), 'utf8');
  const outFile = path.join(out, '.harness', `${name}.json`);
  mkdirSync(path.dirname(outFile), { recursive: true });
  writeFileSync(outFile, '');
  const payload = await bridge('execute_csharp', { code, inputs: { ...inputs, out_file: outFile }, expected_document_id: fixture.document_id }, 300000);
  if (payload.returnValue !== 'written') throw new Error(`harness ${name} returned ${JSON.stringify(payload.returnValue).slice(0, 300)}`);
  return JSON.parse(readFileSync(outFile, 'utf8'));
}

function fill(text, values) {
  return text.replace(/\{\{(\w+)\}\}/g, (_m, key) => {
    if (values[key] === undefined) throw new Error(`value ${key} is required by this scenario (fixture.values, setup output or run values)`);
    return String(values[key]);
  });
}

/** Fill a check's inputs: "{{key}}" alone keeps the value's type (numbers, lists); otherwise text is interpolated. */
function fillInputs(template, values) {
  return Object.fromEntries(Object.entries(template ?? {}).map(([key, value]) => {
    const whole = typeof value === 'string' && value.match(/^\{\{(\w+)\}\}$/);
    return [key, whole ? values[whole[1]] : typeof value === 'string' ? fill(value, values) : value];
  }));
}

function compareFingerprint(base, now) {
  const differences = [];
  for (const key of ['max_id', 'element_count', 'type_count']) if (base[key] !== now[key]) differences.push(`${key}: ${base[key]} -> ${now[key]}`);
  const pinnedBase = new Set(base.pinned), pinnedNow = new Set(now.pinned);
  const pinnedAdded = now.pinned.filter(id => !pinnedBase.has(id)), pinnedRemoved = base.pinned.filter(id => !pinnedNow.has(id));
  if (pinnedAdded.length || pinnedRemoved.length) differences.push(`pinned: +${pinnedAdded.length} -${pinnedRemoved.length}`);
  const viewsChanged = Object.keys(base.views).filter(id => now.views[id] !== base.views[id]);
  const viewsAdded = Object.keys(now.views).filter(id => !(id in base.views));
  if (viewsChanged.length) differences.push(`baseline views changed or deleted: ${viewsChanged.length}`);
  if (viewsAdded.length) differences.push(`views added: ${viewsAdded.length}`);
  // A family's types and parameters are not elements; compare them by name and values.
  const family = { types_added: [], types_removed: [], types_changed: [], parameters_changed: false, current_type_changed: false };
  if (base.family) {
    const before = base.family.types, after = now.family?.types ?? {};
    family.types_added = Object.keys(after).filter(name => !(name in before));
    family.types_removed = Object.keys(before).filter(name => !(name in after));
    family.types_changed = Object.keys(before).filter(name => name in after && after[name] !== before[name]);
    family.parameters_changed = JSON.stringify(base.family.parameters) !== JSON.stringify(now.family?.parameters);
    family.current_type_changed = base.family.current_type !== now.family?.current_type;
    if (family.types_added.length || family.types_removed.length || family.types_changed.length) differences.push(`family types: +${family.types_added.length} -${family.types_removed.length} changed ${family.types_changed.length}`);
    if (family.parameters_changed) differences.push('family parameters changed');
    if (family.current_type_changed) differences.push('current family type changed');
  }
  return { differences, pinned_added: pinnedAdded, pinned_removed: pinnedRemoved, views_changed: viewsChanged.map(id => ({ id: Number(id), before: base.views[id], after: now.views[id] ?? null })), views_added: viewsAdded.map(Number), family };
}

async function loadBaseline() {
  const file = path.join(out, 'baseline.json');
  if (existsSync(file)) {
    const saved = JSON.parse(readFileSync(file, 'utf8'));
    if (saved.document_id === fixture.document_id) return saved;
    throw new Error(`${file} belongs to document ${saved.document_id}; use a new --out directory for a reopened fixture.`);
  }
  const value = await harness('fingerprint');
  if (value.is_modified && args['allow-modified-baseline'] !== 'true')
    throw new Error('The fixture already has unsaved changes. Reopen the disposable copy (discarding changes) before taking a baseline, or pass --allow-modified-baseline.');
  const baseline = { document_id: fixture.document_id, recorded_at: new Date().toISOString(), ...value };
  writeFileSync(file, JSON.stringify(baseline, null, 2));
  return baseline;
}

async function resetFixture(baseline, dir) {
  const before = await harness('active-view');
  // A view the agent opened may be active; Revit cannot delete the active view.
  if (before.active_view_id > baseline.max_id && baseline.active_view_id)
    await bridge('open_view', { view_id: baseline.active_view_id, expected_document_id: fixture.document_id }, 60000);
  const result = await harness('reset', { max_id: baseline.max_id, pinned: baseline.pinned,
    ...(baseline.family ? { family_types: Object.keys(baseline.family.types), family_parameters: baseline.family.parameter_names, family_current: baseline.family.current_type } : {}) });
  const after = await harness('fingerprint');
  const comparison = compareFingerprint(baseline, after);
  const record = { reset: result, start_state: comparison.differences.length ? 'differs' : 'baseline', differences: comparison.differences };
  writeFileSync(path.join(dir, 'reset.json'), JSON.stringify({ ...record, comparison }, null, 2));
  if (comparison.differences.length)
    throw new Error(`The fixture could not be reset to the baseline (${comparison.differences.join('; ')}); see ${dir}\\reset.json. Reopen the disposable copy and start a new round.`);
  return record;
}

function runPi(dir, scenario, prompt, model) {
  copyFileSync(path.join(root, 'workspace', 'AGENTS.md'), path.join(dir, 'AGENTS.md'));
  const piArgs = [cli, '--offline', '--model', model, '--no-extensions', '-e', path.join(root, 'extensions', 'pi-revit', 'index.ts'), '-e', path.join(here, 'guard.ts'),
    '--no-skills', '--skill', path.join(root, 'skills', 'pi-revit'), '--no-prompt-templates', '--no-themes', '--no-session',
    '--tools', ['read', 'grep', 'find', 'ls', ...manifest.tools.map(t => t.name)].join(','), '--mode', 'json', '--print', prompt];
  const hashes = Object.fromEntries(['extensions/pi-revit/index.ts', 'extensions/pi-revit/tool-catalog.ts', 'extensions/pi-revit/platform-prompt.ts',
    'extensions/pi-revit/scope-monitor.ts', 'skills/pi-revit/SKILL.md', 'skills/pi-revit/contracts.generated.json']
    .map(rel => [rel, createHash('sha256').update(readFileSync(path.join(root, rel))).digest('hex')]));
  const started = new Date();
  writeFileSync(path.join(dir, 'invocation.json'), JSON.stringify({ scenario: scenario.id, policy: scenario.policy, started_at: started.toISOString(), prompt, model,
    args: piArgs.slice(0, -1).concat(['<prompt>']), source_hashes: hashes, fixture: { title: fixture.title, document_id: fixture.document_id, file: fixture.file } }, null, 2));
  const env = { ...process.env, PI_OFFLINE: '1', PI_EVAL_AUDIT: path.join(dir, 'audit.jsonl'), PI_EVAL_POLICY: scenario.policy, PI_EVAL_DOCUMENT_ID: fixture.document_id };
  return new Promise(resolve => {
    const child = spawn(process.execPath, piArgs, { cwd: dir, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    child.stdout.pipe(createWriteStream(path.join(dir, 'trace.jsonl'), { flags: 'wx' }));
    child.stderr.pipe(createWriteStream(path.join(dir, 'stderr.txt'), { flags: 'wx' }));
    let timedOut = false;
    const timer = setTimeout(() => { timedOut = true; child.kill(); }, scenario.timeout_s * 1000);
    child.on('close', (code, signal) => { clearTimeout(timer); resolve({ elapsed_ms: Date.now() - started.getTime(), exit_code: code, signal, timed_out: timedOut }); });
  });
}

// Writing systems (Unicode scripts, not languages) used to tell whether a reply matches a request
// written in a non-Latin script. A Latin-script request cannot be judged this way.
const SCRIPTS = ['Han', 'Hiragana', 'Katakana', 'Hangul', 'Arabic', 'Hebrew', 'Cyrillic', 'Greek', 'Thai', 'Devanagari', 'Bengali', 'Tamil', 'Georgian', 'Armenian', 'Ethiopic', 'Khmer', 'Lao', 'Myanmar'];
const scriptCounts = text => Object.fromEntries(SCRIPTS.map(s => [s, (text.match(new RegExp(`\\p{Script=${s}}`, 'gu')) ?? []).length]));

function summarize(dir, scenario, run, model, groundTruth, prompt, values) {
  const lines = file => existsSync(file) ? readFileSync(file, 'utf8').split(/\r?\n/).filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : [];
  const events = lines(path.join(dir, 'trace.jsonl')), audit = lines(path.join(dir, 'audit.jsonl'));
  const assistants = events.filter(e => e.type === 'message_end' && e.message?.role === 'assistant').map(e => e.message);
  const usage = { input: 0, output: 0, cacheRead: 0, reasoning: 0 };
  for (const m of assistants) for (const k of Object.keys(usage)) usage[k] += m.usage?.[k] ?? 0;
  const calls = audit.filter(e => e.event === 'tool_call'), results = audit.filter(e => e.event === 'tool_result');
  const final = assistants.at(-1)?.content?.filter(c => c.type === 'text').map(c => c.text).join('\n') ?? '';
  const allowed = calls.filter(c => c.allowed);
  const impossible = /\b(not possible|impossible|cannot be done|can't be done|isn't possible|is not supported)\b/i.test(final);
  const apiQueries = allowed.filter(c => c.tool === 'search_api_docs').map(c => String(c.input?.query ?? ''));
  const checks = {};
  for (const check of scenario.checks) {
    if (check === 'final_answer') checks[check] = final.trim() && !run.timed_out ? 'pass' : 'fail';
    else if (check === 'no_model_changes') {
      const writes = allowed.filter(c => contracts.get(c.tool)?.write && c.tool !== 'execute_csharp' && !(c.tool === 'manage_sheet_placements' && c.input?.action === 'list'));
      checks[check] = writes.length ? 'fail' : allowed.some(c => c.tool === 'execute_csharp') ? 'review: custom code ran; confirm it only read' : 'pass';
    } else if (check === 'capability_checked')
      checks[check] = allowed.some(c => c.tool === 'find_revit_tools' || c.tool === 'search_api_docs' || (c.tool === 'read' && /references[\\/]tools[\\/]/.test(String(c.input?.path)))) ? 'pass' : 'fail';
    else if (check === 'api_checked_before_impossible') checks[check] = !impossible || apiQueries.length ? 'pass' : 'fail';
    else if (check === 'english_search') checks[check] = allowed.filter(c => c.tool === 'find_revit_tools' && c.input?.query).every(c => /^[\x20-\x7e]*$/.test(c.input.query)) ? 'pass' : 'fail';
    else if (check === 'reports_collision')
      checks[check] = /already (exists|used|taken|in use)|existing view|name (is )?(already )?taken|collision|same name/i.test(final) ? 'pass' : 'fail';
    else if (check === 'reply_script') {
      const asked = scriptCounts(prompt), answered = scriptCounts(final);
      const used = SCRIPTS.filter(s => asked[s] >= 3);
      // A bare list or table of names carries no language; only sentences can be judged.
      const prose = final.replace(/^\s*([-*|]|\d+\.).*$/gm, ' ').replace(/`[^`]*`/g, ' ').replace(/[^\p{L}]+/gu, '');
      checks[check] = !used.length ? 'review: a Latin-script request cannot be judged by script'
        : used.some(s => answered[s] >= 15) ? 'pass'
        : prose.length < 10 ? 'review: bare list, no sentence to judge' : 'fail';
    } else if (check.startsWith('answer_mentions:')) {
      // Language-neutral: the answer names a value, such as the colliding name the setup created.
      const key = check.slice('answer_mentions:'.length);
      const expected = String(values[key] ?? key).normalize('NFKC').toLowerCase();
      checks[check] = final.normalize('NFKC').toLowerCase().includes(expected) ? 'pass' : 'fail';
    }
    else checks[check] = 'review';
  }
  // Independent ground truth from the fixture, not from the agent's report.
  if (groundTruth?.changes) {
    const touched = groundTruth.changes.created.count + groundTruth.changes.fingerprint.views_changed.length
      + groundTruth.changes.fingerprint.pinned_added.length + groundTruth.changes.fingerprint.pinned_removed.length;
    const familyTouched = groundTruth.changes.fingerprint.family ? groundTruth.changes.fingerprint.family.types_added.length + groundTruth.changes.fingerprint.family.types_removed.length
      + groundTruth.changes.fingerprint.family.types_changed.length + (groundTruth.changes.fingerprint.family.parameters_changed ? 1 : 0) : 0;
    if (scenario.policy !== 'write' || scenario.expect_no_changes) checks.fixture_unchanged = touched + familyTouched === 0 ? 'pass' : 'fail';
    else checks.baseline_views_unchanged = groundTruth.changes.fingerprint.views_changed.length === 0 ? 'pass' : 'fail';
  }
  if (groundTruth?.post_check) checks.ground_truth = groundTruth.post_check.pass ? 'pass' : 'fail';
  const summary = { scenario: scenario.id, policy: scenario.policy, protects: scenario.protects, model, run,
    models_observed: [...new Set(assistants.map(m => `${m.provider}/${m.model}`))], requests: assistants.length, usage,
    tool_calls: calls.length, blocked: calls.filter(c => !c.allowed).map(c => ({ tool: c.tool, reason: c.reason })),
    errors: results.filter(r => r.is_error).map(r => r.tool), completion_checks: results.filter(r => r.completion_check).length,
    scope_notes: results.filter(r => r.scope_note).length, inherited_state_results: results.filter(r => r.inherited_state).length,
    api_lookups: { calls: apiQueries.length, members: apiQueries.reduce((sum, q) => sum + q.split(';').filter(p => p.trim()).length, 0) },
    files_read: allowed.filter(c => c.tool === 'read').map(c => c.input?.path),
    searches: allowed.filter(c => c.tool === 'find_revit_tools').map(c => c.input), api_searches: apiQueries,
    sequence: calls.map(c => `${c.tool}${c.allowed ? '' : '[BLOCKED]'}`), checks, start_state: groundTruth?.start_state ?? 'not reset',
    ground_truth: groundTruth?.post_check ?? scenario.ground_truth ?? null, created_objects: groundTruth?.changes?.created ?? null, final_answer: final };
  writeFileSync(path.join(dir, 'summary.json'), JSON.stringify(summary, null, 2));
  writeFileSync(path.join(dir, 'answer.md'), final + '\n');
  return summary;
}

mkdirSync(out, { recursive: true });
await assertFixtureActive();
const baseline = reset ? await loadBaseline() : null;
const repeat = Number(args.repeat ?? 1);
const table = [];
for (const scenario of selected) {
  if (scenario.requires && args['allow-requires'] !== 'true') { console.log(`SKIP ${scenario.id}: requires ${scenario.requires}`); continue; }
  // Scenarios name the fixture they need (default: the project fixture); others are skipped.
  if ((scenario.fixture ?? 'default') !== (fixture.id ?? 'default')) { console.log(`SKIP ${scenario.id}: needs fixture ${scenario.fixture ?? 'default'}, not ${fixture.id ?? 'default'}`); continue; }
  for (let n = 1; n <= repeat; n++) for (const model of models) {
    await assertFixtureActive();
    const tag = new Date().toISOString().replace(/[-:.TZ]/g, '').slice(2, 14);
    const dir = path.join(out, scenario.id, model.replace(/[^A-Za-z0-9.-]+/g, '_'), `${tag}-${n}`);
    mkdirSync(dir, { recursive: true });
    const truth = {};
    if (baseline) truth.start_state = (await resetFixture(baseline, dir)).start_state;
    const values = { ...(fixture.values ?? {}), run_tag: tag, ...(baseline ? { max_id: baseline.max_id, pinned: baseline.pinned, baseline_types: baseline.family?.types ?? {} } : {}) };
    if (scenario.setup) {
      if (!baseline) throw new Error(`${scenario.id} needs a setup step, which requires the reset baseline`);
      const setup = await harness(scenario.setup.script, fillInputs(scenario.setup.inputs, values));
      writeFileSync(path.join(dir, 'setup.json'), JSON.stringify(setup, null, 2));
      Object.assign(values, setup);
    }
    const prompt = fill(scenario.prompt, values);
    const run = await runPi(dir, scenario, prompt, model);
    writeFileSync(path.join(dir, 'run.json'), JSON.stringify(run, null, 2));
    if (baseline) {
      try {
        const created = await harness('created', { max_id: baseline.max_id });
        truth.changes = { created, fingerprint: compareFingerprint(baseline, await harness('fingerprint')) };
        if (scenario.post_check) truth.post_check = await harness(scenario.post_check.script, fillInputs(scenario.post_check.inputs, values));
        // Reference data for answers a script cannot score, such as an inspection's facts.
        if (scenario.reference) truth.reference = await harness(scenario.reference.script, fillInputs(scenario.reference.inputs, values));
      } catch (error) { truth.error = error.message; }
      writeFileSync(path.join(dir, 'ground-truth.json'), JSON.stringify(truth, null, 2));
    }
    const summary = summarize(dir, scenario, run, model, truth, prompt, values);
    table.push(summary);
    console.log(`${scenario.id} #${n} ${model}: ${(run.elapsed_ms / 1000).toFixed(1)} s, ${summary.tool_calls} calls, api ${summary.api_lookups.calls}/${summary.api_lookups.members}, `
      + `completion ${summary.completion_checks}, scope ${summary.scope_notes}, ` + Object.entries(summary.checks).map(([k, v]) => `${k}=${v}`).join(' ') + ` -> ${dir}`);
  }
}
writeFileSync(path.join(out, `summary-${Date.now()}.json`), JSON.stringify(table.map(({ final_answer, ...rest }) => rest), null, 2));
