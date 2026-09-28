import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { access, readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import {
  CONTRACT_END, CONTRACT_START, createLoader, evaluateCorpus, extractBridgeDescriptors, manifestPath, manualRoot, registerExtension,
  resolvePiDependencies, revitApiXmlPaths, root, skillRoot, snapshotPath,
} from './lib/platform.mjs';
import { buildArtifacts } from './generate-contracts.mjs';

// Offline platform gates: no model execution, real bridge connection or package
// installation. Source metadata, generated artifacts, schemas, guidance structure and
// prompt size are checked; Revit bodies and agent behavior need their own tests.
// Budgets change deliberately, never by accident: raise them only with a reviewed reason
// in the change log. The startup total covers the platform section plus the snippets and
// guidelines of tools active at session start; it must not grow with the tool count.
const PROMPT_BUDGET = { startupTotal: 7000, perToolGuidelines: 1200 };
const problems = [];
const warnings = [];
const fail = message => problems.push(message);

const piRequire = resolvePiDependencies();
const loader = createLoader(piRequire);
const { Check, Errors } = await import(pathToFileURL(piRequire.resolve('typebox/value')).href);
assert.equal(Check({ type: 'integer', minimum: 1 }, 0), false, 'schema validator must enforce numeric constraints');
assert.equal(Check({ type: 'object', required: ['id'], properties: { id: { type: 'integer' } } }, {}), false,
  'schema validator must enforce required properties');

// 1. Generated artifacts are current: code is the single owner of contracts.
const { files: generated } = await buildArtifacts();
for (const [file, content] of generated)
  if (!existsSync(file) || await readFile(file, 'utf8') !== content) fail(`${path.relative(root, file)}: generated artifact is stale; run npm run generate:contracts`);

const packageInfo = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
const snapshot = JSON.parse(await readFile(snapshotPath, 'utf8'));
const descriptors = await extractBridgeDescriptors();
const bridgeNames = new Set(descriptors.map(tool => tool.name));
assert.ok(descriptors.length > 0, 'real registry must expose bridge descriptors');
assert.equal(bridgeNames.size, descriptors.length, 'bridge tool names must be unique');

// 2. Documentation manifest v2 structure.
assert.equal(manifest.schema_version, 2, 'unrecognized documentation manifest schema');
assert.ok(typeof manifest.documentation_revision === 'string' && manifest.documentation_revision.trim(), 'documentation revision is required');
const groupIds = new Set(manifest.groups.map(group => group.id));
const manifestNames = new Set(manifest.tools.map(tool => tool.name));
assert.equal(manifestNames.size, manifest.tools.length, 'manifest tool names must be unique');
for (const tool of manifest.tools) {
  assert.match(tool.name, /^[a-z][a-z0-9_]*$/, 'tool names must be safe filenames');
  assert.ok(['bridge', 'native'].includes(tool.source), `${tool.name}: source must be bridge or native`);
  assert.ok(typeof tool.summary === 'string' && tool.summary.trim(), `${tool.name}: summary is required`);
  if (!groupIds.has(tool.group)) fail(`${tool.name}: unknown manifest group ${tool.group}`);
}
assert.deepEqual(manifest.tools.filter(tool => tool.source === 'bridge').map(tool => tool.name).sort(), [...bridgeNames].sort(),
  'manifest bridge inventory must equal the real C# registry');
const guidanceNames = new Set();
for (const guide of manifest.guidance) {
  if (guidanceNames.has(guide.name)) fail(`guidance ${guide.name}: duplicate name`);
  guidanceNames.add(guide.name);
  if (!['workflow', 'reference', 'skill'].includes(guide.kind)) fail(`guidance ${guide.name}: kind must be workflow, reference or skill`);
  if (!existsSync(path.join(skillRoot, guide.path))) fail(`guidance ${guide.name}: missing ${guide.path}`);
  if (!guide.summary?.trim() || !(guide.keywords?.length >= 3)) fail(`guidance ${guide.name}: needs a summary and at least 3 keywords`);
}

// 3. Real registration against the extracted bridge catalogue.
const fixture = await registerExtension(loader, descriptors, packageInfo.version);
const { publicBridgeSchema } = await loader.import(path.join(root, 'extensions', 'pi-revit', 'tool-schema.ts'));
const { ALTERNATIVE_KINDS, VERIFICATION_KINDS, apiLookupQuery } = await loader.import(path.join(root, 'extensions', 'pi-revit', 'contracts.ts'));
const registered = fixture.registered;
let promptReport;
try {
  assert.ok(fixture.routes.includes('/tools'), 'connector must register real extracted bridge schemas');
  assert.deepEqual([...registered.keys()].sort(), [...manifestNames].sort(), 'manifest must cover every registered public tool');
  assert.deepEqual(manifest.tools.filter(tool => tool.source === 'native').map(tool => tool.name).sort(),
    [...registered.keys()].filter(name => !bridgeNames.has(name)).sort(), 'native inventory must match actual extension registrations');
  for (const descriptor of descriptors)
    assert.deepEqual(JSON.parse(JSON.stringify(registered.get(descriptor.name).parameters)), publicBridgeSchema(descriptor.parameters),
      `${descriptor.name}: registered parameters must include the actual extension schema overlay`);

  // 8 (measured here while registered). Prompt budget: the startup contribution must not grow with tool count.
  await fixture.events.get('session_start')?.({}, { ui: { notify() {} } });
  const options = { sections: {}, selectedTools: fixture.activeTools() };
  await fixture.events.get('before_agent_start')?.({ systemPromptOptions: options }, {});
  const section = options.sections.pi_revit ?? '';
  const perTool = fixture.activeTools().map(name => ({ name, snippet: registered.get(name)?.promptSnippet ?? '', guidelines: (registered.get(name)?.promptGuidelines ?? []).join('\n') }));
  const toolChars = perTool.reduce((sum, tool) => sum + tool.snippet.length + tool.guidelines.length, 0);
  promptReport = { sectionChars: section.length, toolChars, activeTools: perTool.length, total: section.length + toolChars,
    largestGuidelines: perTool.map(t => [t.name, t.guidelines.length]).sort((a, b) => b[1] - a[1])[0] };
  if (!section.includes('PI-Revit protocol')) fail('platform section was not injected by before_agent_start');
  if (/For \w+, read .*\.md when detailed usage/.test(perTool.map(t => t.guidelines).join('\n'))) fail('per-tool manual pointer lines must not return; the platform section holds one pointer');
  for (const tool of perTool) if (tool.guidelines.length > PROMPT_BUDGET.perToolGuidelines) fail(`${tool.name}: ${tool.guidelines.length} guideline characters exceed the per-tool budget of ${PROMPT_BUDGET.perToolGuidelines}`);
  if (promptReport.total > PROMPT_BUDGET.startupTotal) fail(`PI-Revit startup prompt contribution ${promptReport.total} characters exceeds the budget of ${PROMPT_BUDGET.startupTotal}`);
} finally { await fixture.restore(); }

// 4. Contract v2 validity for every tool, including limit references.
const publicNames = new Set([...registered.keys(), 'read']);
const apiXml = await Promise.all(revitApiXmlPaths().map(file => readFile(file, 'utf8')));
if (!apiXml.length) warnings.push('RevitAPI.xml not found; API references in declared limits were not verified (set REVIT_API_PATH).');
const apiMemberExists = member => apiXml.some(xml => new RegExp(`name="[TMPFE]:Autodesk\\.Revit\\.(?:[A-Za-z0-9]+\\.)*${member.replace(/\./g, '\\.')}(?:["(])`).test(xml));
for (const tool of snapshot.tools) {
  if (!(tool.keywords?.length >= 3)) fail(`${tool.name}: declare at least 3 discovery keywords`);
  if ((tool.write || tool.effects?.length) && !VERIFICATION_KINDS.includes(tool.verification))
    fail(`${tool.name}: a tool that can write or has effects must declare verification (${VERIFICATION_KINDS.join(', ')})`);
  if (tool.verification !== null && !VERIFICATION_KINDS.includes(tool.verification)) fail(`${tool.name}: unknown verification ${tool.verification}`);
  if (!tool.limits.length) fail(`${tool.name}: declare at least one limit with its alternative`);
  for (const limit of tool.limits) {
    if (!limit.what?.trim() || !ALTERNATIVE_KINDS.includes(limit.alternative?.kind) || !limit.alternative.ref?.trim())
      { fail(`${tool.name}: every limit needs what, a known alternative kind and a reference`); continue; }
    const ref = limit.alternative.ref;
    if (limit.alternative.kind === 'tool') {
      const named = [...ref.matchAll(/\b[a-z][a-z0-9]*(?:_[a-z0-9]+)+\b|\bread\b/g)].map(m => m[0]).filter(name => !['host_bounds', 'include_instance_count', 'count_only', 'next_offset'].includes(name));
      if (!named.length) fail(`${tool.name}: tool alternative "${ref}" names no public tool`);
      for (const name of named) if (!publicNames.has(name)) fail(`${tool.name}: tool alternative names unknown tool ${name}`);
    }
    // Every name in the generated one-call lookup must resolve, so the lookup never sends the agent searching.
    if (limit.alternative.kind === 'api' && apiXml.length)
      for (const member of (apiLookupQuery(ref) ?? '').split('; ').filter(Boolean))
        if (!apiMemberExists(member)) fail(`${tool.name}: API alternative ${member} was not found in the installed RevitAPI.xml`);
  }
  // Required inputs must not be described as optional anywhere in their schema text.
  for (const name of tool.parameters?.required ?? [])
    if (/\boptional\b/i.test(tool.parameters.properties?.[name]?.description ?? '')) fail(`${tool.name}.${name}: a required input's description says "optional"`);
}

// 5. Manuals: one per tool, generated block present, examples valid, limitation language declared.
const manualFiles = (await readdir(manualRoot)).filter(file => file.endsWith('.md')).sort();
assert.deepEqual(manualFiles, [...manifestNames].map(name => `${name}.md`).sort(), 'one manual per public tool; no orphan manuals');
const snapshotByName = new Map(snapshot.tools.map(tool => [tool.name, tool]));
let exampleCount = 0;
for (const tool of manifest.tools) {
  const file = path.join(manualRoot, `${tool.name}.md`);
  const markdown = await readFile(file, 'utf8');
  if (!markdown.includes(CONTRACT_START) || !markdown.includes(CONTRACT_END)) fail(`${tool.name}: manual lacks its generated contract block`);
  const handWritten = markdown.slice(0, markdown.indexOf(CONTRACT_START)) + markdown.slice(markdown.indexOf(CONTRACT_END) + CONTRACT_END.length);
  if (/outside this tool|\bunsupported\b|not supported/i.test(handWritten) && !snapshotByName.get(tool.name)?.limits.length)
    fail(`${tool.name}: the manual states a limitation but the tool declares no limits with alternatives`);
  const examples = [...markdown.matchAll(/^```json[ \t]*\r?\n([\s\S]*?)^```[ \t]*\r?$/gm)];
  if (examples.length === 0) fail(`${tool.name}: missing JSON input example`);
  const schema = registered.get(tool.name).parameters;
  for (const match of examples) {
    const label = `${path.relative(root, file)}:${markdown.slice(0, match.index).split('\n').length + 1}`;
    try {
      const input = JSON.parse(match[1]);
      assert.ok(input !== null && typeof input === 'object' && !Array.isArray(input), 'example must be an input argument object');
      if (!Check(schema, input)) throw new Error(JSON.stringify([...Errors(schema, input)]));
      assertDocumentedKeys(schema, input, label);
      // Negative controls exercise the actual composed schemas rather than a copied test schema.
      for (const key of schema.required ?? []) {
        const missingRequired = { ...input };
        delete missingRequired[key];
        assert.equal(Check(schema, missingRequired), false, `${tool.name}: required ${key} was not enforced`);
      }
      if (schema.properties?._operation_id) assert.equal(Check(schema, { ...input, _operation_id: 42 }), false, `${tool.name}: retry ID must remain a string`);
      exampleCount++;
    } catch (error) { fail(`${label}: ${error.message}`); }
  }
}

// 6. Invariant register: every rule has an owner and an enforcement, and guidance tags resolve.
const register = JSON.parse(await readFile(path.join(root, 'docs', 'invariants.json'), 'utf8'));
const invariants = new Map(register.invariants.map(item => [item.id, item]));
const guidanceFiles = [...await markdownUnder(path.join(root, 'skills')), ...await markdownUnder(path.join(root, 'docs')),
  ...['AGENTS.md', 'README.md', 'workspace/AGENTS.md'].map(file => path.join(root, file))];
const tagged = new Set();
const testSources = (await Promise.all((await filesUnder(path.join(root, 'tests'))).filter(file => /\.(mjs|cjs|cs)$/.test(file)).map(file => readFile(file, 'utf8')))).join('\n')
  + await readFile(new URL(import.meta.url), 'utf8');
for (const file of guidanceFiles) {
  const text = await readFile(file, 'utf8');
  for (const match of text.matchAll(/<!-- inv:([a-z0-9-]+) -->/g)) {
    tagged.add(match[1]);
    if (!invariants.has(match[1])) fail(`${path.relative(root, file)}: tag inv:${match[1]} is not in docs/invariants.json`);
  }
}
const coreRules = ['skills/pi-revit/SKILL.md', 'skills/pi-revit/references/execution-rules.md', 'skills/pi-revit/references/operation-recovery.md',
  'skills/pi-revit/references/visual-verification.md', 'workspace/AGENTS.md'];
for (const relative of coreRules)
  for (const [index, line] of (await readFile(path.join(root, relative), 'utf8')).split(/\r?\n/).entries())
    if (/\b(never|must not)\b/i.test(line) && !/<!-- inv:[a-z0-9-]+ -->/.test(line)) fail(`${relative}:${index + 1}: a never/must-not rule needs an <!-- inv:<id> --> tag and a register entry`);
for (const item of register.invariants) {
  if (!['safety', 'correctness', 'guidance'].includes(item.severity)) fail(`inv:${item.id}: unknown severity`);
  if (!tagged.has(item.id)) fail(`inv:${item.id}: no guidance sentence carries this tag`);
  if (item.enforcement === 'code') {
    if (!item.test || !testSources.includes(item.test)) fail(`inv:${item.id}: enforcement=code needs an existing test titled "${item.test}"`);
    for (const location of item.location ?? []) if (!existsSync(path.join(root, location))) fail(`inv:${item.id}: missing location ${location}`);
  } else if (item.enforcement === 'advisory') {
    if (!item.reason) fail(`inv:${item.id}: advisory rules must state why code cannot enforce them`);
    if (item.severity === 'safety' && !item.eval_scenario) fail(`inv:${item.id}: an advisory safety rule must name the agent-eval scenario that measures it`);
  } else fail(`inv:${item.id}: enforcement must be code or advisory`);
}
const scenarioFile = path.join(root, 'tests', 'agent-eval', 'scenarios.json');
const scenarioIds = existsSync(scenarioFile) ? new Set(JSON.parse(await readFile(scenarioFile, 'utf8')).scenarios.map(s => s.id)) : new Set();
for (const item of register.invariants) if (item.eval_scenario && !scenarioIds.has(item.eval_scenario)) fail(`inv:${item.id}: eval scenario ${item.eval_scenario} is not defined in tests/agent-eval/scenarios.json`);

// Architecture gates over every bridge source file, present and future. Shared primitives
// own cross-cutting policy; a new tool cannot reintroduce a private variant.
for (const file of (await readdir(path.join(root, 'src', 'Revit', 'Tools'))).filter(name => name.endsWith('.cs'))) {
  const source = (await readFile(path.join(root, 'src', 'Revit', 'Tools', file), 'utf8')).replace(/\/\/.*$/gm, '').replace(/"(?:[^"\\]|\\.)*"/g, '""');
  // inv:no-tool-saves-model
  if (/\.\s*(Save|SaveAs|Close|SynchronizeWithCentral)\s*\(/.test(source)) fail(`architecture: no bridge tool saves, closes or synchronizes a document (${file})`);
  // inv:parameter-ambiguity: LookupParameter returns an arbitrary one of several same-named parameters.
  if (file !== 'ParameterResolver.cs' && /\b(LookupParameter|GetParameters)\s*\(/.test(source))
    fail(`architecture: ${file} resolves parameters by name directly; use ParameterResolver (one ambiguity policy for every tool)`);
  // inv:special-objects-flagged: sheet-owned schedule instances include the titleblock's revision schedule.
  if (file !== 'ElementTraits.cs' && /\bScheduleSheetInstance\b/.test(source) && !/\bElementTraits\./.test(source))
    fail(`architecture: ${file} handles ScheduleSheetInstance without ElementTraits classification`);
  // inv:derived-state-reported: an object made from an existing one carries its hidden content, overrides and values.
  if (file !== 'InheritedState.cs' && /\.\s*Duplicate\s*\(|\bCopyElements?\s*\(|\bMirrorElements\s*\(|\.\s*ChangeTypeId\s*\(/.test(source) && !/\bInheritedState\./.test(source))
    fail(`architecture: a tool that duplicates, copies or retypes reports inherited state; ${file} creates from an existing object without InheritedState`);
  // inv:existing-objects-not-reused: names and sheet numbers go through the one collision check.
  if (file !== 'ElementNames.cs' && /\.\s*(Name|SheetNumber)\s*=(?!=)|\.\s*(NewType|RenameCurrentType)\s*\(/.test(source))
    fail(`architecture: names are assigned through ElementNames; ${file} assigns a Name, SheetNumber or family type name directly`);
  // inv:document-kind-declared: every tool states where it works, so the dispatcher can refuse the rest.
  if (/:\s*ITool\b/.test(source) && !/\bDocumentKinds\s*=>/.test(source))
    fail(`architecture: every bridge tool declares the document kinds it supports; ${file} has no DocumentKinds`);
}
// inv:model-changes-reported: the dispatcher, not each tool, attaches model_changes to every model-changing call.
{
  const server = await readFile(path.join(root, 'src', 'Revit', 'BridgeServer.cs'), 'utf8');
  if (!/ModelChangeRecorder\.For\(\s*tool\b/.test(server) || !/\.Attach\(/.test(server))
    fail('architecture: the bridge dispatcher records model changes for every tool call (ModelChangeRecorder.For(tool, ...) and Attach)');
}

// 7. Discovery corpus: English task phrasings (the model translates other languages).
// Every public tool needs coverage; overall top-N recall must stay above the threshold.
const corpus = JSON.parse(await readFile(path.join(root, 'tests', 'discovery', 'corpus.json'), 'utf8'));
const corpusResults = await evaluateCorpus(loader, corpus, packageInfo.version);
const recall = corpusResults.filter(r => r.pass).length / corpusResults.length;
if (recall < corpus.min_recall) fail(`discovery corpus recall ${recall.toFixed(3)} is below ${corpus.min_recall}; run node tests/discovery/run-corpus.mjs for the misses`);
for (const tool of manifest.tools) {
  const count = corpus.entries.filter(entry => entry.expect === tool.name).length;
  if (count < corpus.min_per_tool) fail(`${tool.name}: the discovery corpus needs at least ${corpus.min_per_tool} phrasings (has ${count})`);
}
for (const guide of manifest.guidance.filter(g => g.kind === 'workflow'))
  if (!corpus.entries.some(entry => entry.expect_guidance === guide.name)) fail(`workflow ${guide.name}: the discovery corpus needs at least one phrasing`);
if (!corpus.entries.some(entry => entry.expect_fallback)) fail('the discovery corpus needs at least one query that must return the capability route');

// 9. Local links in all guidance and contributor documents.
let localLinkCount = 0;
for (const file of [...guidanceFiles, path.join(root, 'tests/tool-documentation/README.md')]) {
  if (!existsSync(file)) continue;
  const markdown = await readFile(file, 'utf8');
  const prose = markdown.replace(/^```[^\n]*\r?\n[\s\S]*?^```[ \t]*\r?$/gm, '');
  for (const match of prose.matchAll(/\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/g)) {
    const destination = match[1] ?? match[2];
    if (/^(?:[a-z][a-z0-9+.-]*:|#)/i.test(destination)) continue;
    const relative = decodeURIComponent(destination.split('#')[0]);
    if (!relative) continue;
    try { await access(path.resolve(path.dirname(file), relative)); localLinkCount++; }
    catch { fail(`${path.relative(root, file)}: broken local link ${destination}`); }
  }
}

for (const warning of warnings) console.warn(`WARNING: ${warning}`);
assert.equal(problems.length, 0, `Platform validation failed:\n${problems.join('\n')}`);
console.log(`PASS: ${descriptors.length} bridge + ${registered.size - descriptors.length} native contracts; ${manualFiles.length} manuals with generated contracts; ${exampleCount} valid input examples; ${register.invariants.length} invariants; discovery recall ${corpusResults.filter(r => r.pass).length}/${corpusResults.length}; ${localLinkCount} local links.`);
console.log(`Prompt: platform section ${promptReport.sectionChars} + ${promptReport.activeTools} active tools ${promptReport.toolChars} = ${promptReport.total} characters (budget ${PROMPT_BUDGET.startupTotal}).`);
console.log('Offline structure verification only: conditional runtime rules, Revit behavior, and agent effectiveness require their own tests.');

function assertDocumentedKeys(schema, value, location) {
  if (!schema || typeof schema !== 'object' || value === null) return;
  if (Array.isArray(value)) {
    if (schema.items) value.forEach((item, index) => assertDocumentedKeys(schema.items, item, `${location}[${index}]`));
  } else if (typeof value === 'object' && schema.properties) {
    for (const [key, child] of Object.entries(value)) {
      // Some schemas permit additional JSON properties, but manual argument examples must use advertised keys.
      assert.ok(Object.hasOwn(schema.properties, key) || schema.additionalProperties === true || schema.patternProperties,
        `${location}: undocumented argument ${key}`);
      if (schema.properties[key]) assertDocumentedKeys(schema.properties[key], child, `${location}.${key}`);
    }
  }
}

async function filesUnder(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules'].includes(entry.name)) continue;
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...await filesUnder(file));
    else if (entry.isFile()) files.push(file);
  }
  return files;
}

async function markdownUnder(directory) {
  return (await filesUnder(directory)).filter(file => file.endsWith('.md'));
}
