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
const descriptors = [
  { name: 'get_elements', description: 'Query elements', tier: 'core' },
  { name: 'get_linked_models', description: 'List linked models', tier: 'advanced', promptSnippet: 'must be omitted', promptGuidelines: ['must be omitted'] },
  { name: 'get_schedules', description: 'Read schedules', tier: 'advanced' },
];

async function fixture(action, online = true) {
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
    return new Response(JSON.stringify(parsed.pathname === '/tools' ? { tools: descriptors } : { ok: true, addinVersion: version }));
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
    await action({ pi, registered, timers, event, online: () => { available = true; }, fetches: () => fetches,
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
  assert.equal(f.registered.get('get_linked_models').promptSnippet, undefined);
  assert.equal(f.registered.get('get_linked_models').promptGuidelines, undefined);
  const find = args => f.registered.get('find_revit_tools').execute('catalog-test', args);
  const list = await find({});
  assert.equal(list.details.total_count, 3);
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
