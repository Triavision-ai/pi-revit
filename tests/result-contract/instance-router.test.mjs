import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { createInstanceRouter } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/instance-router.ts', import.meta.url)));
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const generation = () => randomUUID().replaceAll('-', '');
const noFallback = /session is unavailable.*outcome is unknown.*no action was sent to another instance/;

async function fixture(action) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-instances-'));
  const previous = { appData: process.env.APPDATA, fetch: globalThis.fetch };
  const bridgeDirectory = path.join(directory, 'RevitBridge');
  const instancesDirectory = path.join(bridgeDirectory, 'instances');
  const handlers = [];
  const a = { baseUrl: 'http://instance-a.invalid', token: 'private-fixture-a', pid: process.pid, bridgeId: generation(), revitVersion: '2025', supportsOperationTracking: true };
  const b = { baseUrl: 'http://instance-b.invalid', token: 'private-fixture-b', pid: process.pid, bridgeId: generation(), revitVersion: '2026', supportsOperationTracking: true };
  const records = new Map([[a.bridgeId, a], [b.bridgeId, b]]);
  const probes = [];
  const unavailable = new Set();
  const wrongGeneration = new Set();
  const writeRecord = (info, filename = `${info.bridgeId}.json`) => writeFile(path.join(instancesDirectory, filename), JSON.stringify(info));
  const legacy = info => writeFile(path.join(bridgeDirectory, 'bridge.json'), JSON.stringify(info));
  const readLegacy = async () => JSON.parse(await readFile(path.join(bridgeDirectory, 'bridge.json'), 'utf8'));
  const probe = async info => {
    probes.push(info.bridgeId);
    if (!records.has(info.bridgeId) || unavailable.has(info.bridgeId)) throw new Error('Fixture bridge unreachable');
    return { bridgeId: wrongGeneration.has(info.bridgeId) ? generation() : info.bridgeId, addinVersion: 'fixture-version' };
  };
  try {
    process.env.APPDATA = directory;
    await mkdir(instancesDirectory, { recursive: true });
    await writeRecord(a);
    await writeRecord(b);
    await legacy(a);
    await action({
      a, b, probes, records, unavailable, wrongGeneration, writeRecord, legacy, readLegacy, probe,
      router: () => createInstanceRouter(readLegacy, probe),
      writeRaw: (name, text) => writeFile(path.join(instancesDirectory, name), text),
      remove: id => rm(path.join(instancesDirectory, `${id}.json`)),
      async connect(beforeReady) {
        const registered = new Map();
        const events = new Map();
        handlers.push(events);
        let active = ['external_fixture'];
        registered.active = () => [...active];
        registered.event = name => events.get(name)?.({}, { ui: { notify() {} } });
        const ready = connector({
          registerTool(tool) { registered.set(tool.name, tool); active = [...new Set([...active, tool.name])]; },
          on(name, callback) { events.set(name, callback); },
          getActiveTools() { return [...active]; },
          setActiveTools(names) { active = [...names]; },
          sendMessage() {},
        });
        beforeReady?.(registered);
        await ready;
        return registered;
      },
    });
  } finally {
    try { for (const events of handlers) await events.get('session_shutdown')?.({}, {}); }
    finally {
      globalThis.fetch = previous.fetch;
      if (previous.appData === undefined) delete process.env.APPDATA;
      else process.env.APPDATA = previous.appData;
      assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
      assert.ok(path.basename(directory).startsWith('pi-revit-instances-'));
      await rm(directory, { recursive: true, force: true });
    }
  }
}

test('two reachable bridge files require explicit selection despite a legacy pointer', async () => fixture(async f => {
  const router = f.router();
  await assert.rejects(router.resolve(), /Several Revit instances.*list and select/);
  assert.deepEqual(new Set(f.probes), new Set([f.a.bridgeId, f.b.bridgeId]));
  const list = await router.list();
  assert.equal(list.length, 2, 'legacy record and instance file must be deduplicated');
  assert.ok(list.every(info => !info.selected));
}));

test('explicit selection persists when the legacy pointer changes', async () => fixture(async f => {
  const router = f.router();
  await router.select(f.a.bridgeId);
  await f.legacy(f.b);
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId);
  const list = await router.list();
  assert.equal(list.find(info => info.bridge_id === f.a.bridgeId).selected, true);
  assert.equal(list.find(info => info.bridge_id === f.b.bridgeId).selected, false);
}));

test('first sole instance is automatically pinned across pointer changes and replacement', async () => fixture(async f => {
  await f.remove(f.b.bridgeId);
  const router = f.router();
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId);
  assert.equal((await router.list())[0].selected, true, 'implicit sole-instance choice must become a session selection');
  await f.writeRecord(f.b);
  await f.legacy(f.b);
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId, 'another instance or changed legacy pointer must not replace the pinned session');
  await f.remove(f.a.bridgeId);
  await assert.rejects(router.resolve(), noFallback);
  await router.select(f.b.bridgeId);
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId, 'explicit reselection must recover from a closed session');
}));

test('sole reachable candidate among stale records is also pinned', async () => fixture(async f => {
  const router = f.router();
  f.unavailable.add(f.a.bridgeId);
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId);
  f.unavailable.clear();
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId, 'another bridge returning online must not change the implicit choice');
}));

test('stale initial legacy record does not pin the router before a fresh live bridge starts', async () => fixture(async f => {
  await f.remove(f.a.bridgeId);
  await f.remove(f.b.bridgeId);
  f.unavailable.add(f.a.bridgeId);
  const router = f.router();
  await assert.rejects(router.resolve(), /unreachable/);
  await f.legacy(f.b);
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId, 'failed initial probe must not bind the stale generation');
  assert.equal((await router.list())[0].selected, true);
}));

test('sole candidate with a mismatched ping generation does not become selected', async () => fixture(async f => {
  await f.remove(f.a.bridgeId);
  await f.remove(f.b.bridgeId);
  f.wrongGeneration.add(f.a.bridgeId);
  const router = f.router();
  await assert.rejects(router.resolve(), /different bridge generation/);
  await f.legacy(f.b);
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId);
}));

test('selected bridge removal never falls back to a remaining instance', async () => fixture(async f => {
  const router = f.router();
  await router.select(f.a.bridgeId);
  await f.legacy(f.b);
  await f.remove(f.a.bridgeId);
  await assert.rejects(router.resolve(), noFallback);
  assert.equal((await router.list()).length, 1);
}));

test('receipt IDs override selection and route to their original bridge', async () => fixture(async f => {
  const router = f.router();
  await router.select(f.b.bridgeId);
  assert.equal((await router.resolve(`${f.a.bridgeId}:${randomUUID()}`)).bridgeId, f.a.bridgeId);
  assert.equal((await router.resolve()).bridgeId, f.b.bridgeId, 'receipt lookup must not change normal selection');
  await f.legacy(f.b);
  await f.remove(f.a.bridgeId);
  await assert.rejects(router.resolve(`${f.a.bridgeId}:${randomUUID()}`), noFallback);
}));

test('list and selection results expose useful metadata without tokens or endpoint URLs', async () => fixture(async f => {
  const router = f.router();
  const list = await router.list();
  const selected = await router.select(f.a.bridgeId);
  for (const result of [list, selected]) {
    const text = JSON.stringify(result);
    for (const secret of [f.a.token, f.b.token, f.a.baseUrl, f.b.baseUrl]) assert.equal(text.includes(secret), false);
  }
  const a = list.find(info => info.bridge_id === f.a.bridgeId);
  assert.equal(a.pid, process.pid);
  assert.equal(a.revit_version, '2025');
  assert.equal(a.addin_version, 'fixture-version');
  assert.equal(a.supports_operation_tracking, true);
}));

test('malformed, stale and mismatched discovery entries are ignored before probing', async () => fixture(async f => {
  const malformedId = generation(), staleId = generation(), mismatchId = generation(), noTokenId = generation(), nonIntegerPidId = generation();
  await f.writeRaw(`${malformedId}.json`, '{not-json');
  await f.writeRecord({ ...f.a, bridgeId: staleId, pid: Number.MAX_SAFE_INTEGER });
  await f.writeRecord({ ...f.a, bridgeId: generation() }, `${mismatchId}.json`);
  await f.writeRecord({ ...f.a, bridgeId: noTokenId, token: '' });
  await f.writeRecord({ ...f.a, bridgeId: nonIntegerPidId, pid: '123' });
  await f.writeRaw('not-a-generation.json', JSON.stringify(f.a));
  assert.equal((await f.router().list()).length, 2);
  assert.deepEqual(new Set(f.probes), new Set([f.a.bridgeId, f.b.bridgeId]), 'invalid discovery records must not reach probe');
}));

test('unreachable probes and bridge-generation mismatch are excluded from live candidates', async () => fixture(async f => {
  const router = f.router();
  f.unavailable.add(f.b.bridgeId);
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId);
  f.unavailable.clear();
  f.wrongGeneration.add(f.b.bridgeId);
  assert.deepEqual((await router.list()).map(info => info.bridge_id), [f.a.bridgeId]);
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId);
}));

test('invalid or unreachable selection leaves the prior choice intact', async () => fixture(async f => {
  const router = f.router();
  await router.select(f.a.bridgeId);
  await assert.rejects(router.select('invalid'), /exact session ID/);
  f.unavailable.add(f.b.bridgeId);
  await assert.rejects(router.select(f.b.bridgeId), /selection was unchanged/);
  assert.equal((await router.resolve()).bridgeId, f.a.bridgeId);
}));

test('independent router instances keep their selections separate', async () => fixture(async f => {
  const first = f.router(), second = f.router();
  await first.select(f.a.bridgeId);
  await assert.rejects(second.resolve(), /Several Revit instances/);
  await second.select(f.b.bridgeId);
  assert.equal((await first.resolve()).bridgeId, f.a.bridgeId);
  assert.equal((await second.resolve()).bridgeId, f.b.bridgeId);
}));

test('legacy bridge alongside a modern bridge gets an opaque selectable identity', async () => fixture(async f => {
  await f.remove(f.b.bridgeId);
  const legacy = { baseUrl: 'http://legacy.invalid', token: 'legacy-private-credential', pid: process.pid, revitVersion: '2024' };
  f.records.set(undefined, legacy);
  await f.legacy(legacy);
  const router = f.router();
  await assert.rejects(router.resolve(), /Several Revit instances/);
  const list = await router.list();
  assert.equal(list.length, 2);
  const entry = list.find(info => info.revit_version === '2024');
  assert.match(entry.bridge_id, /^[0-9a-f]{32}$/);
  assert.equal(entry.supports_operation_tracking, false);
  assert.equal(JSON.stringify(list).includes(legacy.token), false);
  assert.equal(JSON.stringify(list).includes(legacy.baseUrl), false);
  assert.equal((await router.list()).find(info => info.revit_version === '2024').bridge_id, entry.bridge_id, 'opaque identity must remain stable for one legacy session');
  const selected = await router.select(entry.bridge_id);
  assert.equal(selected.bridge_id, entry.bridge_id);
  assert.deepEqual(await router.resolve(), legacy);
  assert.equal((await router.list()).find(info => info.bridge_id === entry.bridge_id).selected, true);
  await f.legacy({ ...legacy, token: 'replacement-session-credential' });
  await assert.rejects(router.resolve(), noFallback, 'changed per-start credentials must not retain the old legacy selection');
}));

function catalogFetch(f, catalogs, beforeTools) {
  globalThis.fetch = async (url, init) => {
    const parsed = new URL(url);
    const info = [f.a, f.b].find(candidate => candidate.baseUrl === parsed.origin);
    assert.ok(info, 'fixture must never contact a real bridge');
    assert.equal(init.method, 'GET', 'catalogue workflow must not dispatch model edits');
    assert.equal(parsed.searchParams.get('token'), info.token);
    if (parsed.pathname === '/ping') return new Response(JSON.stringify(await f.probe(info)));
    assert.equal(parsed.pathname, '/tools');
    await beforeTools?.(info);
    return new Response(JSON.stringify({ tools: catalogs.get(info.bridgeId) }));
  };
}

function catalogsFor(f) {
  const tool = (name, properties, tier = 'core') => ({ name, description: name, tier, parameters: { type: 'object', properties } });
  return new Map([
    [f.a.bridgeId, [tool('shared_tool', { old_argument: { type: 'string' } }), tool('only_a', {}), tool('specialist_a', {}, 'advanced')]],
    [f.b.bridgeId, [tool('shared_tool', { new_argument: { type: 'integer' } }), tool('only_b', {}), tool('specialist_b', {}, 'advanced')]],
  ]);
}

test('switching connector target refreshes changed schemas and removes old tools from the active catalogue', async () => fixture(async f => {
  catalogFetch(f, catalogsFor(f));
  const tools = await f.connect();
  const select = id => tools.get('manage_revit_instances').execute('select', { action: 'select', bridge_id: id });
  const selectedA = await select(f.a.bridgeId);
  assert.equal(selectedA.details.tool_catalog_ready, true);
  await tools.event('session_start');
  assert.equal(tools.get('shared_tool').parameters.properties.old_argument.type, 'string');
  assert.ok(tools.active().includes('only_a'));
  assert.equal(tools.active().includes('specialist_a'), false);
  await tools.get('find_revit_tools').execute('activate-a', { names: ['specialist_a'] });
  assert.ok(tools.active().includes('specialist_a'));
  const selectedB = await select(f.b.bridgeId);
  assert.equal(selectedB.details.tool_catalog_ready, true);
  assert.equal(tools.get('shared_tool').parameters.properties.new_argument.type, 'integer');
  assert.equal(Object.hasOwn(tools.get('shared_tool').parameters.properties, 'old_argument'), false);
  assert.equal(tools.active().includes('only_a'), false);
  assert.equal(tools.active().includes('specialist_a'), false);
  assert.ok(tools.active().includes('only_b'));
  assert.equal(tools.active().includes('specialist_b'), false, 'new specialist remains opt-in');
  assert.ok(tools.active().includes('external_fixture'), 'switching must preserve other extensions');
  const catalogue = await tools.get('find_revit_tools').execute('catalog-b', {});
  assert.deepEqual(catalogue.details.tools.map(tool => tool.name).sort(), ['only_b', 'shared_tool', 'specialist_b']);
  await assert.rejects(tools.get('find_revit_tools').execute('old-tool', { names: ['only_a'] }), /Unknown Revit tool names/);
}));

test('selection drains pending old discovery before publishing the new target catalogue', async () => fixture(async f => {
  await f.remove(f.b.bridgeId);
  let entered, release;
  const started = new Promise(resolve => { entered = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  catalogFetch(f, catalogsFor(f), async info => {
    if (info.bridgeId === f.a.bridgeId) { entered(); await gate; }
  });
  let tools;
  const connecting = f.connect(registered => { tools = registered; });
  await started;
  await f.writeRecord(f.b);
  let selectionFinished = false;
  const selection = tools.get('manage_revit_instances').execute('switch-during-discovery', { action: 'select', bridge_id: f.b.bridgeId })
    .then(result => { selectionFinished = true; return result; });
  await Promise.resolve();
  assert.equal(selectionFinished, false, 'selection must wait for pending previous-target discovery');
  release();
  const [, result] = await Promise.all([connecting, selection]);
  assert.equal(result.details.tool_catalog_ready, true);
  assert.equal(tools.get('shared_tool').parameters.properties.new_argument.type, 'integer');
  assert.equal(tools.active().includes('only_a'), false);
  assert.ok(tools.active().includes('only_b'));
  const catalogue = await tools.get('find_revit_tools').execute('final-catalog', {});
  assert.deepEqual(catalogue.details.tools.map(tool => tool.name).sort(), ['only_b', 'shared_tool', 'specialist_b']);
}));

test('connector blocks silent restart replacement until selection refreshes schemas', async () => fixture(async f => {
  await f.remove(f.b.bridgeId);
  catalogFetch(f, catalogsFor(f));
  const tools = await f.connect();
  assert.equal(tools.get('shared_tool').parameters.properties.old_argument.type, 'string');
  await f.writeRecord(f.b);
  await f.legacy(f.b);
  await f.remove(f.a.bridgeId);
  await assert.rejects(tools.get('shared_tool').execute('after-restart', { old_argument: 'fixture' }), noFallback);
  const selected = await tools.get('manage_revit_instances').execute('reselect', { action: 'select', bridge_id: f.b.bridgeId });
  assert.equal(selected.details.tool_catalog_ready, true);
  assert.equal(tools.get('shared_tool').parameters.properties.new_argument.type, 'integer');
  assert.equal(tools.active().includes('only_a'), false);
}));

test('selection also drains discovery that begins while its bridge probes are pending', async () => fixture(async f => {
  await f.remove(f.b.bridgeId);
  const catalogs = catalogsFor(f);
  let allowDiscovery = false, holdSelection = false, selectionEntered, releaseSelection, discoveryEntered, releaseDiscovery;
  const probing = new Promise(resolve => { selectionEntered = resolve; });
  const probeGate = new Promise(resolve => { releaseSelection = resolve; });
  const discovering = new Promise(resolve => { discoveryEntered = resolve; });
  const discoveryGate = new Promise(resolve => { releaseDiscovery = resolve; });
  globalThis.fetch = async (url, init) => {
    const parsed = new URL(url);
    const info = [f.a, f.b].find(candidate => candidate.baseUrl === parsed.origin);
    assert.ok(info);
    assert.equal(init.method, 'GET');
    if (parsed.pathname === '/ping') {
      if (holdSelection && info.bridgeId === f.b.bridgeId) { selectionEntered(); await probeGate; }
      return new Response(JSON.stringify(await f.probe(info)));
    }
    assert.equal(parsed.pathname, '/tools');
    if (!allowDiscovery) throw new Error('Fixture initial discovery unavailable');
    if (info.bridgeId === f.a.bridgeId) { discoveryEntered(); await discoveryGate; }
    return new Response(JSON.stringify({ tools: catalogs.get(info.bridgeId) }));
  };
  const tools = await f.connect();
  assert.equal(tools.has('shared_tool'), false);
  await f.writeRecord(f.b);
  holdSelection = true;
  const selecting = tools.get('manage_revit_instances').execute('select-b', { action: 'select', bridge_id: f.b.bridgeId });
  await probing;
  allowDiscovery = true;
  const loading = tools.get('find_revit_tools').execute('recover-old-discovery', {});
  await discovering;
  releaseSelection();
  const whilePending = await tools.get('manage_revit_instances').execute('selection-state', { action: 'list' });
  assert.equal(whilePending.details.instances.find(info => info.bridge_id === f.b.bridgeId).selected, true,
    'selection must have changed while the old catalogue response is still held');
  assert.equal(tools.has('shared_tool'), false, 'held old discovery must not publish a schema prematurely');
  releaseDiscovery();
  const [selection] = await Promise.all([selecting, loading]);
  assert.equal(selection.details.tool_catalog_ready, true);
  assert.equal(tools.get('shared_tool').parameters.properties.new_argument.type, 'integer');
  assert.equal(tools.active().includes('only_a'), false);
  const catalogue = await tools.get('find_revit_tools').execute('final-catalog', {});
  assert.deepEqual(catalogue.details.tools.map(tool => tool.name).sort(), ['only_b', 'shared_tool', 'specialist_b']);
}));

test('connector waits for explicit selection, then routes new calls and receipts independently', async () => fixture(async f => {
  const requests = [];
  const descriptor = { name: 'set_parameters', description: 'Fixture edit', write: true, parameters: { type: 'object', properties: {} } };
  globalThis.fetch = async (url, init) => {
    const parsed = new URL(url);
    const info = [f.a, f.b].find(candidate => candidate.baseUrl === parsed.origin);
    assert.ok(info, 'fixture must never contact a real bridge');
    assert.equal(parsed.searchParams.get('token'), info.token, 'request must use the selected session credential');
    requests.push({ bridgeId: info.bridgeId, path: parsed.pathname, method: init.method, operationId: parsed.searchParams.get('operation_id'), body: init.body });
    if (parsed.pathname === '/ping') return new Response(JSON.stringify(await f.probe(info)));
    if (parsed.pathname === '/tools') return new Response(JSON.stringify({ tools: [descriptor] }));
    if (parsed.pathname.startsWith('/operations/')) return new Response(JSON.stringify({ state: 'succeeded', bridge_id: info.bridgeId }));
    assert.equal(parsed.pathname, '/tools/set_parameters/execute');
    assert.equal(init.method, 'POST');
    return new Response(JSON.stringify({ success: true, details: { payload: { updated: 1, fixture_bridge: info.bridgeId } } }));
  };
  const tools = await f.connect();
  assert.ok(tools.has('manage_revit_instances'));
  assert.equal(tools.has('set_parameters'), false, 'ambiguous factory discovery must not choose the legacy pointer');
  assert.ok(requests.every(request => request.path === '/ping'));
  const manage = args => tools.get('manage_revit_instances').execute('manage', args);
  const list = await manage({ action: 'list' });
  assert.equal(list.details.instances.length, 2);
  assert.equal(JSON.stringify(list).includes(f.a.token), false);
  await manage({ action: 'select', bridge_id: f.a.bridgeId });
  assert.ok(tools.has('set_parameters'), 'selection must discover bridge tools');
  await f.legacy(f.b);
  const params = { updates: [{ element_id: 7, parameter: 'Comments', value: 'Fixture' }] };
  const first = await tools.get('set_parameters').execute('new-a', params);
  assert.equal(first.details.bridge_id, f.a.bridgeId, 'legacy pointer movement cannot change explicit selection');
  await manage({ action: 'select', bridge_id: f.b.bridgeId });
  const second = await tools.get('set_parameters').execute('new-b', params);
  assert.equal(second.details.bridge_id, f.b.bridgeId);
  const countBeforeStatus = requests.filter(request => request.method === 'POST').length;
  const status = await tools.get('get_revit_operation').execute('status-a', { operation_id: first.details.operation_id });
  assert.equal(status.details.bridge_id, f.a.bridgeId);
  assert.equal(requests.filter(request => request.method === 'POST').length, countBeforeStatus);
  const retry = await tools.get('set_parameters').execute('retry-a', { ...params, _operation_id: first.details.operation_id });
  assert.equal(retry.details.bridge_id, f.a.bridgeId);
  const posts = requests.filter(request => request.method === 'POST');
  assert.deepEqual(posts.map(request => request.bridgeId), [f.a.bridgeId, f.b.bridgeId, f.a.bridgeId]);
  assert.equal(posts[0].operationId, posts[2].operationId);
  assert.deepEqual(JSON.parse(posts[2].body), params);
  await f.remove(f.b.bridgeId);
  await f.legacy(f.a);
  await assert.rejects(tools.get('set_parameters').execute('removed-b', params), noFallback);
  assert.equal(requests.filter(request => request.method === 'POST').length, posts.length, 'closed selected bridge must not dispatch to another instance');
}));
