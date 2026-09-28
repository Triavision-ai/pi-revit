import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';
const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const version = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const packaged = new Map(JSON.parse(await readFile(new URL('../../skills/pi-revit/contracts.generated.json', import.meta.url), 'utf8')).tools.map(t => [t.name, t]));
const packagedDescriptor = name => { const p = packaged.get(name); return { name, description: name, tier: p.tier, write: p.write, effects: p.effects, requiresDocument: p.requires_document, parameters: structuredClone(p.parameters) }; };
const descriptors = [
  { name: 'get_elements', description: 'Query elements', tier: 'core' },
  { name: 'get_linked_models', description: 'List linked models', tier: 'advanced', promptSnippet: 'Discover links', promptGuidelines: ['Use get_linked_models to inspect links.'] },
  { name: 'get_schedules', description: 'Read schedules', tier: 'advanced' },
];

async function fixture(action, online = true, catalogDescriptors = descriptors) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-catalog-'));
  const originals = { fetch: globalThis.fetch, appData: process.env.APPDATA, setInterval: globalThis.setInterval, clearInterval: globalThis.clearInterval };
  process.env.APPDATA = directory;
  await mkdir(path.join(directory, 'RevitBridge'));
  await writeFile(path.join(directory, 'RevitBridge', 'bridge.json'), JSON.stringify({ baseUrl: 'http://catalog.invalid', token: 'test' }));
  const timers = new Set();
  globalThis.setInterval = callback => { const timer = { callback, unref() {} }; timers.add(timer); return timer; };
  globalThis.clearInterval = timer => timers.delete(timer);
  let available = online;
  let fetches = 0;
  let discoveryGate;
  globalThis.fetch = async url => {
    const parsed = new URL(url);
    assert.equal(parsed.origin, 'http://catalog.invalid');
    fetches++;
    if (!available) throw new Error('offline fixture');
    if (parsed.pathname === '/tools' && discoveryGate) await discoveryGate;
    return new Response(JSON.stringify(parsed.pathname === '/tools' ? { tools: catalogDescriptors } : { ok: true, addinVersion: version }));
  };
  const registered = new Map();
  const handlers = new Map();
  let active = ['read', 'another_extension'];
  const pi = {
    registerTool(tool) { registered.set(tool.name, tool); active = [...new Set([...active, tool.name])]; },
    on(event, handler) { handlers.set(event, handler); },
    getActiveTools() { return [...active]; },
    setActiveTools(names) { active = [...names]; },
    sendMessage() {},
  };
  const event = name => handlers.get(name)?.({}, { ui: { notify() {} } });
  try {
    await connector(pi);
    await action({ pi, registered, timers, event, online: () => { available = true; }, offline: () => { available = false; }, fetches: () => fetches,
      holdDiscovery: () => { let release; discoveryGate = new Promise(resolve => { release = resolve; }); return () => release(); } });
  } finally {
    await event('session_shutdown');
    globalThis.fetch = originals.fetch;
    globalThis.setInterval = originals.setInterval;
    globalThis.clearInterval = originals.clearInterval;
    if (originals.appData === undefined) delete process.env.APPDATA;
    else process.env.APPDATA = originals.appData;
    await rm(directory, { recursive: true, force: true });
  }
}

test('catalogue starts specialist tools inactive and preserves other extensions', async () => fixture(async f => {
  assert.equal(f.timers.size, 0, 'no background timer in factory');
  await f.event('session_start');
  assert.deepEqual(f.pi.getActiveTools().sort(), ['another_extension', 'find_revit_tools', 'get_elements', 'get_revit_operation', 'manage_revit_instances', 'manage_revit_scripts', 'ping', 'read', 'read_revit_result'].sort());
  assert.equal(f.registered.get('get_linked_models').promptSnippet, 'Discover links');
  assert.ok(f.registered.get('get_linked_models').promptGuidelines.includes('Use get_linked_models to inspect links.'));
  const find = args => f.registered.get('find_revit_tools').execute('catalog-test', args);
  const list = await find({});
  assert.equal(list.details.total_count, 9, 'three bridge tools plus six native utilities');
  assert.equal(f.pi.getActiveTools().includes('get_schedules'), false);
  await find({ query: 'linked models' });
  assert.ok(f.pi.getActiveTools().includes('get_linked_models'));
  await find({ names: ['get_schedules'] });
  assert.ok(f.pi.getActiveTools().includes('get_linked_models'));
  assert.ok(f.pi.getActiveTools().includes('get_schedules'));
  assert.ok(f.pi.getActiveTools().includes('another_extension'));
  const before = f.pi.getActiveTools();
  await find({ query: 'no-such-match' });
  assert.deepEqual(f.pi.getActiveTools(), before);
  await assert.rejects(find({ names: ['unknown'] }), /Unknown/);
  assert.deepEqual(f.pi.getActiveTools(), before);
}));

test('documentation scope works offline without discovery or activation', async () => fixture(async f => {
  const before = f.pi.getActiveTools(), fetches = f.fetches();
  const result = await f.registered.get('find_revit_tools').execute('docs', { scope: 'documentation', names: ['manage_schedules', 'manage_revit_scripts'] });
  assert.equal(f.fetches(), fetches);
  assert.deepEqual(f.pi.getActiveTools(), before);
  assert.equal(result.details.total_count, 2);
  assert.deepEqual(JSON.parse(result.content[0].text), result.details, 'manual pointers must be model visible');
  const bridge = result.details.tools.find(t => t.name === 'manage_schedules');
  const native = result.details.tools.find(t => t.name === 'manage_revit_scripts');
  assert.equal(bridge.registered, false);
  assert.equal(bridge.advertised_by_selected_bridge, null);
  assert.equal(bridge.documentation.compatibility, 'unknown');
  assert.equal(native.registered, true);
  assert.equal(native.documentation.compatibility, 'package_local');
  assert.equal(native.documentation.status, 'available');
  assert.ok((await readFile(native.documentation.path, 'utf8')).includes('manage_revit_scripts'));
  await assert.rejects(f.registered.get('find_revit_tools').execute('bad', { scope: 'documentation', activate: true }), /does not activate/);
}, false));

test('manual compatibility compares the selected bridge contract per tool', async () => fixture(async f => {
  const result = await f.registered.get('find_revit_tools').execute('docs', { names: ['get_linked_models'], activate: false });
  const tool = result.details.tools[0];
  assert.equal(tool.documentation.observed_bridge_version, version);
  assert.equal(tool.documentation.compatibility, 'contract_changed', 'the fixture schema differs from the packaged contract');
  assert.ok(tool.documentation.live_contract_hash);
  assert.notEqual(tool.documentation.live_contract_hash, tool.documentation.packaged_contract_hash);
  assert.equal(tool.advertised_by_selected_bridge, true);
  assert.equal(tool.active, true, 'factory registration precedes session-start hiding');
  assert.equal(tool.documentation.status, 'available');
}));

test('an exact packaged contract reports contract_match with declared limits and verification', async () => fixture(async f => {
  const result = await f.registered.get('find_revit_tools').execute('docs', { names: ['manage_views'], activate: false });
  const tool = result.details.tools[0];
  assert.equal(tool.documentation.compatibility, 'contract_match');
  assert.equal(tool.verification, 'capture');
  assert.ok(tool.limits.some(limit => /perspective/i.test(limit.what) && limit.alternative.kind === 'api'), 'limits come from the packaged contract when the bridge omits them');
}, true, [packagedDescriptor('manage_views')]));

test('packaged query terms survive live enrichment in both scopes without altering availability or activation', async () => {
  // Use the actual bridge descriptions that exposed the singular/plural miss.
  const bridgeDescriptions = await Promise.all(['GetModelHealth', 'SetParameters'].map(async name => {
    const source = await readFile(new URL(`../../src/Revit/Tools/${name}.cs`, import.meta.url), 'utf8');
    const description = source.match(/public string Description => "([^"]+)";/)?.[1];
    assert.ok(description, `Missing source description for ${name}`);
    return description;
  }));
  await fixture(async f => {
    const find = args => f.registered.get('find_revit_tools').execute('summary-match', args);
    const beforeFetches = f.fetches(), beforeActive = f.pi.getActiveTools();
    const before = await find({ scope: 'documentation', query: 'warnings' });
    assert.ok(before.details.tools.some(tool => tool.name === 'get_model_health'));
    assert.equal(f.fetches(), beforeFetches);
    assert.deepEqual(f.pi.getActiveTools(), beforeActive);
    const unavailable = await find({ query: 'warnings', activate: false });
    assert.equal(unavailable.details.total_count, 0, 'a summary must not advertise an unavailable bridge tool');

    f.online();
    await f.event('session_start');
    await [...f.timers][0].callback();
    const active = f.pi.getActiveTools(), fetches = f.fetches();
    for (const scope of ['documentation', 'available']) {
      const result = await find({ scope, query: 'warnings', activate: false });
      const health = result.details.tools.find(tool => tool.name === 'get_model_health');
      assert.ok(health, `${scope} must retain packaged summary vocabulary`);
      assert.equal(health.description, bridgeDescriptions[0], 'live description remains authoritative');
      assert.equal(health.advertised_by_selected_bridge, true);
      assert.deepEqual(health.effects, []);
      const combined = await find({ scope, query: 'warnings worksharing', activate: false });
      assert.deepEqual(combined.details.tools.map(tool => tool.name), ['get_model_health'], 'all words may span summary and live description');
    }
    assert.equal(f.fetches(), fetches, 'matching a known catalogue requires no additional connection');
    assert.deepEqual(f.pi.getActiveTools(), active);
    await find({ query: 'warnings', limit: 1 });
    assert.ok(f.pi.getActiveTools().includes('get_model_health'));
    assert.equal(f.pi.getActiveTools().includes('set_parameters'), false, 'activation applies only to returned page');
    assert.ok(f.pi.getActiveTools().includes('another_extension'));
  }, false, [
    { name: 'get_model_health', description: bridgeDescriptions[0], tier: 'advanced', effects: [] },
    { name: 'set_parameters', description: bridgeDescriptions[1], tier: 'advanced', effects: ['model'] },
  ]);
});

test('native utilities remain discoverable when the bridge is unavailable', async () => fixture(async f => {
  const result = await f.registered.get('find_revit_tools').execute('native', { names: ['get_revit_operation'] });
  assert.equal(result.details.tools[0].source, 'native');
  assert.equal(result.details.tools[0].advertised_by_selected_bridge, null);
  await assert.rejects(f.registered.get('find_revit_tools').execute('missing', { names: ['manage_schedules'] }), /scope=documentation/);
}, false));

test('documentation lookup after the selected bridge closes preserves snapshot and activation without networking', async () => fixture(async f => {
  await f.event('session_start');
  const find = args => f.registered.get('find_revit_tools').execute('docs', args);
  const before = await find({ scope: 'documentation', names: ['get_linked_models'] });
  const active = f.pi.getActiveTools(), fetches = f.fetches();
  f.offline();
  const after = await find({ scope: 'documentation', names: ['get_linked_models'] });
  assert.equal(f.fetches(), fetches);
  assert.deepEqual(f.pi.getActiveTools(), active);
  assert.equal(after.details.bridge_catalog_observed_at, before.details.bridge_catalog_observed_at);
  assert.equal(after.details.tools[0].advertised_by_selected_bridge, true, 'advertisement is explicitly a retained snapshot');
  assert.equal(after.details.tools[0].documentation.observed_bridge_version, version);
  assert.match(after.details.instructions, /not a fresh health check/);
}));

test('a valid empty bridge catalogue is known and does not trigger repeated discovery', async () => fixture(async f => {
  const fetches = f.fetches();
  const listed = await f.registered.get('find_revit_tools').execute('empty', {});
  assert.equal(listed.details.bridge_catalog_known, true);
  assert.ok(listed.details.bridge_catalog_observed_at);
  assert.equal(listed.details.total_count, 6);
  assert.ok(listed.details.tools.every(tool => tool.source === 'native'));
  assert.equal(f.fetches(), fetches);
  const documented = await f.registered.get('find_revit_tools').execute('absent', { scope: 'documentation', names: ['get_elements'] });
  assert.equal(documented.details.tools[0].registered, false);
  assert.equal(documented.details.tools[0].advertised_by_selected_bridge, false);
}, true, []));

test('offline discovery starts only in session and hides late specialist tools', async () => fixture(async f => {
  assert.equal(f.timers.size, 0);
  await f.event('session_start');
  assert.equal(f.timers.size, 1);
  f.online();
  await [...f.timers][0].callback();
  assert.equal(f.timers.size, 0);
  assert.ok(f.registered.has('get_schedules'));
  assert.equal(f.pi.getActiveTools().includes('get_schedules'), false);
  assert.ok(f.pi.getActiveTools().includes('another_extension'));
}, false));

test('loader can recover offline discovery and activate only its matches', async () => fixture(async f => {
  await f.event('session_start');
  f.online();
  const result = await f.registered.get('find_revit_tools').execute('load-first', { query: 'schedules' });
  assert.equal(result.details.returned_count, 1);
  assert.ok(f.pi.getActiveTools().includes('get_schedules'));
  assert.equal(f.pi.getActiveTools().includes('get_linked_models'), false);
  assert.ok(f.pi.getActiveTools().includes('another_extension'));
}, false));

test('discovery response arriving after shutdown cannot register tools', async () => fixture(async f => {
  await f.event('session_start');
  f.online();
  const release = f.holdDiscovery();
  const pending = [...f.timers][0].callback();
  await f.event('session_shutdown');
  release();
  await pending;
  assert.equal(f.registered.has('get_schedules'), false);
  assert.equal(f.timers.size, 0);
}, false));

test('shutdown stops retry and rejects a stale timer callback without networking', async () => fixture(async f => {
  await f.event('session_start');
  const timer = [...f.timers][0];
  await f.event('session_shutdown');
  const before = f.fetches();
  f.online();
  await timer.callback();
  assert.equal(f.fetches(), before);
  assert.equal(f.timers.size, 0);
  assert.equal(f.registered.has('get_schedules'), false);
}, false));
