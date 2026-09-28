import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

// Platform behavior that must hold for every present and future tool: capability route,
// language-neutral discovery, one platform prompt section, shared-rule hoisting and
// loaded-status reporting. Fixture bridges are in-memory; nothing contacts Revit.
const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const { createDiscoveryIndex, normalizeText, stem } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/discovery.ts', import.meta.url)));
const { hoistSharedGuidelines } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/platform-prompt.ts', import.meta.url)));
const version = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const packaged = new Map(JSON.parse(await readFile(new URL('../../skills/pi-revit/contracts.generated.json', import.meta.url), 'utf8')).tools.map(t => [t.name, t]));
const packagedDescriptor = (name, extra = {}) => { const p = packaged.get(name); return { name, description: name, tier: p.tier, write: p.write, effects: p.effects, requiresDocument: p.requires_document, parameters: structuredClone(p.parameters), ...extra }; };

async function fixture(action, descriptors = []) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-platform-'));
  const originals = { fetch: globalThis.fetch, appData: process.env.APPDATA, setInterval: globalThis.setInterval };
  process.env.APPDATA = directory;
  await mkdir(path.join(directory, 'RevitBridge'));
  await writeFile(path.join(directory, 'RevitBridge', 'bridge.json'), JSON.stringify({ baseUrl: 'http://platform.invalid', token: 'test' }));
  globalThis.setInterval = () => ({ unref() {} });
  globalThis.fetch = async url => {
    const parsed = new URL(url);
    assert.equal(parsed.origin, 'http://platform.invalid');
    return new Response(JSON.stringify(parsed.pathname === '/tools' ? { tools: descriptors } : { ok: true, addinVersion: version }));
  };
  const registered = new Map(), handlers = new Map();
  let active = ['read'];
  const pi = {
    registerTool(tool) { registered.set(tool.name, tool); active = [...new Set([...active, tool.name])]; },
    on(event, handler) { handlers.set(event, handler); },
    getActiveTools: () => [...active], setActiveTools(names) { active = [...names]; }, sendMessage() {},
  };
  try {
    await connector(pi);
    await action({ registered, handlers, find: args => registered.get('find_revit_tools').execute('t', args) });
  } finally {
    await handlers.get('session_shutdown')?.({}, {});
    globalThis.fetch = originals.fetch; globalThis.setInterval = originals.setInterval;
    if (originals.appData === undefined) delete process.env.APPDATA; else process.env.APPDATA = originals.appData;
    await rm(directory, { recursive: true, force: true });
  }
}

test('unmatched query returns the capability route', async () => fixture(async f => {
  const result = await f.find({ scope: 'documentation', query: 'calibrate flux capacitor' });
  assert.equal(result.details.match, 'none');
  assert.equal(result.details.total_count, 0);
  const route = result.details.fallback.route.join(' ');
  assert.match(route, /search_api_docs/);
  assert.match(route, /execute_csharp/);
  assert.match(route, /does not mean the operation is impossible/);
}));

test('a request just outside a tool finds the tool together with its declared alternative', async () => fixture(async f => {
  const result = await f.find({ scope: 'documentation', query: 'perspective 3D view' });
  const views = result.details.tools.find(tool => tool.name === 'manage_views');
  assert.ok(views, 'the limit text makes the nearby tool discoverable');
  const limit = views.limits.find(item => /perspective/i.test(item.what));
  assert.equal(limit.alternative.kind, 'api');
  assert.match(limit.alternative.ref, /View3D\.CreatePerspective/);
}));

test('languages need no rules: discovery is English and the model is told to translate', async () => fixture(async f => {
  const tool = f.registered.get('find_revit_tools');
  assert.match(tool.description, /translate a request written in another language into English/);
  assert.match(tool.parameters.properties.query.description, /translate from the user's language first/);
  await f.handlers.get('session_start')({}, { ui: { notify() {} } });
  const options = { sections: {} };
  await f.handlers.get('before_agent_start')({ systemPromptOptions: options }, {});
  assert.match(options.sections.pi_revit, /Reply in the user's language\. Search tools and API documentation with English terms/);
  const english = await f.find({ scope: 'documentation', query: 'rename sheets' });
  assert.equal(english.details.tools[0].name, 'manage_sheets');
}));

test('guidance search returns workflows and skills with readable paths', async () => fixture(async f => {
  const result = await f.find({ scope: 'documentation', query: 'room tags schedules sheets' });
  const workflow = result.details.guidance.find(guide => guide.name === 'room-documentation');
  assert.equal(workflow?.kind, 'workflow');
  assert.match(await readFile(workflow.path, 'utf8'), /Room/);
  const skill = (await f.find({ scope: 'documentation', query: 'inspect edit document export models' })).details.guidance.find(g => g.kind === 'skill');
  assert.equal(skill?.name, 'pi-revit', 'skills beside this package are discovered automatically');
}));

test('the platform section is injected once and per-tool manual pointers are gone', async () => fixture(async f => {
  await f.handlers.get('session_start')({}, { ui: { notify() {} } });
  const options = { sections: {} };
  await f.handlers.get('before_agent_start')({ systemPromptOptions: options }, {});
  const section = options.sections.pi_revit;
  assert.match(section, /PI-Revit protocol/);
  assert.match(section, /Say an operation is not possible only after that check/);
  assert.match(section, /Offer further improvements as suggestions/);
  assert.match(section, /references[\\/]tools[\\/]<tool_name>\.md/);
  assert.equal((section.match(/Rules shared by several Revit tools/g) ?? []).length, 1);
  assert.match(section, /use the exact document identity/, 'a rule repeated across tools is hoisted once');
  for (const name of ['get_elements', 'set_parameters']) {
    const guidelines = f.registered.get(name).promptGuidelines;
    assert.ok(!guidelines.some(rule => /use the exact document identity/.test(rule)), `${name} keeps only tool-specific rules`);
    assert.ok(!guidelines.some(rule => /For \w+, read .*\.md/.test(rule)));
  }
  assert.ok(f.registered.get('set_parameters').promptGuidelines.includes('set_parameters: prefer atomic batches.'));
}, [
  packagedDescriptor('get_elements', { promptGuidelines: ['get_elements: use the exact document identity.'] }),
  packagedDescriptor('set_parameters', { promptGuidelines: ['set_parameters: use the exact document identity.', 'set_parameters: prefer atomic batches.'] }),
]));

test('ping reports the loaded package, guidance revision and per-tool contract agreement', async () => fixture(async f => {
  const result = await f.registered.get('ping').execute('p', {});
  const loaded = result.details.pi_revit;
  assert.equal(loaded.extension_package, version);
  assert.ok(loaded.documentation_revision);
  assert.equal(loaded.contracts.matching, 1);
  assert.deepEqual(loaded.contracts.changed, ['get_schedules']);
  assert.deepEqual(loaded.contracts.undocumented, ['future_tool']);
  assert.match(result.content[0].text, /PI-Revit loaded:/);
}, [packagedDescriptor('get_elements'), { name: 'get_schedules', tier: 'advanced', parameters: { type: 'object', properties: {} } },
  { name: 'future_tool', tier: 'core', parameters: { type: 'object', properties: {} } }]));

test('shared-rule hoisting is generic across tool names and bridge versions', () => {
  const { shared, perTool } = hoistSharedGuidelines([
    { name: 'a_tool', promptGuidelines: ['a_tool: bind the call.', 'a_tool: unique.'] },
    { name: 'b_tool', promptGuidelines: ['b_tool: bind the call.'] },
    { name: 'c_tool', promptGuidelines: null },
  ]);
  assert.deepEqual(shared, ['bind the call.']);
  assert.deepEqual(perTool.get('a_tool'), ['a_tool: unique.']);
  assert.deepEqual(perTool.get('b_tool'), []);
  assert.deepEqual(perTool.get('c_tool'), []);
});

test('completion monitor steers repeated re-verification after edits, for any tool, without blocking', async () => {
  const { createCompletionMonitor } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/completion-monitor.ts', import.meta.url)));
  const monitor = createCompletionMonitor();
  const capture = { view_id: 42, expected_document_id: 'doc', _operation_id: 'ignored' };
  const edit = () => assert.equal(monitor.afterCall('any_future_edit_tool', { code: 'x' }, { write: true }), null);
  assert.equal(monitor.afterCall('capture_view', capture, { effects: ['files'] }), null, 'first check');
  edit();
  assert.equal(monitor.afterCall('capture_view', { ...capture, _operation_id: 'other' }, { effects: ['files'] }), null, 'a before/after pair is normal');
  edit();
  const note = monitor.afterCall('capture_view', capture, { effects: ['files'] });
  assert.match(note, /completion check/);
  assert.match(note, /offer any further improvements as suggestions/);
  assert.match(note, /any_future_edit_tool ×2/);
  assert.equal(monitor.afterCall('capture_view', capture, { effects: ['files'] }), null, 'no edit since the last check: no repeat');
  for (let i = 0; i < 5; i++) assert.equal(monitor.afterCall('get_elements', { category: 'Walls' }, { write: false, effects: [] }), null, 'plain rereads never trigger');
  monitor.reset();
  assert.deepEqual(monitor.ledger(), {});
});

test('discovery normalizes English vocabulary and ranks complete before partial matches', () => {
  assert.equal(normalizeText('Café C#'), 'cafe csharp');
  assert.equal(stem('schedules'), 'schedule');
  assert.equal(stem('entries'), 'entry');
  assert.equal(stem('class'), 'class');
  const index = createDiscoveryIndex();
  const items = [
    { name: 'ping', keywords: ['connection'] },
    { name: 'tag_elements', keywords: ['door tag'], summary: 'Create host element tags.' },
    { name: 'element_relationships', keywords: ['host', 'hosted'] },
    { name: 'element_query', keywords: ['list'], inputs: ['level', 'category'] },
  ];
  const names = query => index.search(items, query, d => d).matches.map(m => m.item.name);
  assert.deepEqual(names('is it connected'), ['ping'], 'word forms match: connected / connection');
  assert.deepEqual(names('what hosts this door'), ['tag_elements', 'element_relationships'], 'a partial match still follows a complete one');
  assert.ok(names('list walls on level 1').includes('element_query'), 'input names are vocabulary and numbers are arguments');
  assert.deepEqual(names('brew coffee'), []);
});
