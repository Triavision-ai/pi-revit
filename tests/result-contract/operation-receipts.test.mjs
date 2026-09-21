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
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const version = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const descriptor = {
  name: 'set_parameters', description: 'Edit fixture parameters', tier: 'core', write: true,
  parameters: { type: 'object', properties: { updates: { type: 'array' } }, required: ['updates'] },
};
const changes = { updates: [{ element_id: 7, parameter: 'Comments', value: 'Fixture' }] };
const success = () => new Response(JSON.stringify({ success: true, details: { payload: { updated: 1 } } }));

async function fixture(action, bridgeOptions = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-receipts-'));
  const previous = { fetch: globalThis.fetch, appData: process.env.APPDATA };
  const handlers = new Map();
  const registered = new Map();
  const calls = [];
  let active = [];
  let respond;
  let bridge = { baseUrl: 'http://receipts.invalid', token: 'fixture-token', bridgeId: randomUUID().replaceAll('-', ''), supportsOperationTracking: true, ...bridgeOptions };
  const saveBridge = async patch => {
    bridge = { ...bridge, ...patch };
    await writeFile(path.join(directory, 'RevitBridge', 'bridge.json'), JSON.stringify(bridge));
  };
  try {
    process.env.APPDATA = directory;
    await mkdir(path.join(directory, 'RevitBridge'));
    await saveBridge({});
    globalThis.fetch = async (url, init) => {
      const parsed = new URL(url);
      assert.equal(parsed.origin, 'http://receipts.invalid', 'fixture must never contact a real bridge');
      assert.equal(parsed.searchParams.get('token'), 'fixture-token');
      const call = { url: parsed, method: init.method, body: init.body, signal: init.signal };
      calls.push(call);
      if (parsed.pathname === '/ping') {
        assert.equal(init.method, 'GET');
        return new Response(JSON.stringify({ ok: true, bridgeId: bridge.bridgeId, addinVersion: version }));
      }
      if (parsed.pathname === '/tools') {
        assert.equal(init.method, 'GET');
        return new Response(JSON.stringify({ tools: [descriptor] }));
      }
      if (respond) return respond(call);
      assert.equal(parsed.pathname, '/tools/set_parameters/execute');
      assert.equal(init.method, 'POST');
      return success();
    };
    await connector({
      registerTool(tool) { registered.set(tool.name, tool); active = [...new Set([...active, tool.name])]; },
      on(event, callback) { handlers.set(event, callback); },
      getActiveTools() { return [...active]; },
      setActiveTools(names) { active = [...names]; },
      sendMessage() {},
    });
    assert.ok(registered.has('set_parameters'), 'production connector must discover the bridge tool');
    await action({
      calls, registered, saveBridge, bridge: () => bridge,
      respond(callback) { respond = callback; },
      posts: () => calls.filter(call => call.method === 'POST'),
      execute: (args = changes, signal) => registered.get('set_parameters').execute('fixture-call', args, signal),
    });
  } finally {
    try { await handlers.get('session_shutdown')?.({}, {}); }
    finally {
      globalThis.fetch = previous.fetch;
      if (previous.appData === undefined) delete process.env.APPDATA;
      else process.env.APPDATA = previous.appData;
      assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
      assert.ok(path.basename(directory).startsWith('pi-revit-receipts-'));
      await rm(directory, { recursive: true, force: true });
    }
  }
}

test('tracked request assigns its operation ID before POST and exposes it in model content and details', async () => fixture(async f => {
  let idAtDispatch;
  f.respond(call => {
    assert.equal(call.method, 'POST');
    idAtDispatch = call.url.searchParams.get('operation_id');
    assert.match(idAtDispatch, new RegExp(`^${f.bridge().bridgeId}:[0-9a-f-]{36}$`));
    assert.equal(call.url.searchParams.get('timeout_ms'), '30000');
    assert.deepEqual(JSON.parse(call.body), changes);
    return success();
  });
  const result = await f.execute();
  assert.equal(f.posts().length, 1);
  assert.equal(result.details.operation_id, idAtDispatch);
  assert.equal(result.details.bridge_id, f.bridge().bridgeId);
  assert.deepEqual(result.details.payload, { updated: 1 });
  assert.ok(result.content.some(block => block.text.includes(`Operation ID: ${idAtDispatch}`)));
  assert.equal(f.registered.get('set_parameters').parameters.properties._operation_id.type, 'string');
}));

test('identical retry sends the original ID and strips connector metadata from the body without mutating arguments', async () => fixture(async f => {
  const first = await f.execute();
  const args = { ...structuredClone(changes), _operation_id: first.details.operation_id };
  const before = structuredClone(args);
  const second = await f.execute(args);
  assert.equal(f.posts().length, 2, 'retry contacts the receipt-aware bridge using the same ID');
  assert.equal(f.posts()[0].url.searchParams.get('operation_id'), f.posts()[1].url.searchParams.get('operation_id'));
  assert.equal(second.details.operation_id, first.details.operation_id);
  assert.deepEqual(JSON.parse(f.posts()[1].body), changes);
  assert.equal(Object.hasOwn(JSON.parse(f.posts()[1].body), '_operation_id'), false);
  assert.deepEqual(args, before, 'caller arguments must remain unchanged');
}));

test('new requests generate separate operation IDs even with identical arguments', async () => fixture(async f => {
  const first = await f.execute();
  const second = await f.execute();
  assert.notEqual(first.details.operation_id, second.details.operation_id);
}));

test('retry from an earlier bridge generation is rejected before any POST', async () => fixture(async f => {
  const oldId = `${randomUUID().replaceAll('-', '')}:${randomUUID()}`;
  await assert.rejects(f.execute({ ...changes, _operation_id: oldId }), /session is unavailable.*outcome is unknown.*no action was sent to another instance/);
  assert.equal(f.posts().length, 0);
}));

test('fresh bridge discovery rejects an old retry after the bridge restarts', async () => fixture(async f => {
  const first = await f.execute();
  await f.saveBridge({ bridgeId: randomUUID().replaceAll('-', '') });
  await assert.rejects(f.execute({ ...changes, _operation_id: first.details.operation_id }), /session is unavailable.*outcome is unknown.*no action was sent to another instance/);
  assert.equal(f.posts().length, 1, 'restart check must happen before a second request is dispatched');
}));

test('retry to an existing generation rejects unsupported tracking before POST', async () => fixture(async f => {
  const id = `${f.bridge().bridgeId}:${randomUUID()}`;
  await assert.rejects(f.execute({ ...changes, _operation_id: id }), /does not support operation receipts.*not sent/);
  assert.equal(f.posts().length, 0);
}, { supportsOperationTracking: false }));

test('retry with no discoverable original generation reports unknown outcome before POST', async () => fixture(async f => {
  await assert.rejects(f.execute({ ...changes, _operation_id: `previous:${randomUUID()}` }), /session is unavailable.*outcome is unknown.*no action was sent to another instance/);
  assert.equal(f.posts().length, 0);
}, { bridgeId: undefined }));

test('invalid explicit retry metadata is rejected before POST', async () => fixture(async f => {
  for (const id of ['', null, 17, {}]) {
    await assert.rejects(f.execute({ ...changes, _operation_id: id }), /_operation_id must be the exact ID/);
  }
  assert.equal(f.posts().length, 0);
}));

for (const failure of ['network', 'http', 'invalid-json']) {
  test(`${failure} error retains the dispatched operation ID and instructs outcome inspection`, async () => fixture(async f => {
    f.respond(() => {
      if (failure === 'network') throw new Error('Fixture network failure');
      if (failure === 'http') return new Response(JSON.stringify({ error: true, message: 'Fixture bridge rejection' }), { status: 409 });
      return new Response('not json', { status: 502 });
    });
    await assert.rejects(f.execute(), error => {
      const id = f.posts()[0].url.searchParams.get('operation_id');
      assert.ok(id, 'ID must exist before the failed fetch finishes');
      assert.ok(error.message.includes(`Operation ID: ${id}`));
      assert.match(error.message, /get_revit_operation/);
      assert.match(error.message, /Do not retry an edit with a new ID/);
      return true;
    });
  }));
}

for (const phase of ['fetch', 'response-body']) {
  test(`client abort during ${phase} preserves operation ID and warns execution may continue`, async () => fixture(async f => {
    const controller = new AbortController();
    f.respond(call => {
      const abort = () => {
        controller.abort();
        assert.equal(call.signal.aborted, true);
        throw new DOMException('Fixture client cancellation', 'AbortError');
      };
      if (phase === 'fetch') return abort();
      return { ok: true, status: 200, async json() { return abort(); } };
    });
    await assert.rejects(f.execute(changes, controller.signal), error => {
      const id = f.posts()[0].url.searchParams.get('operation_id');
      assert.ok(error.message.includes(`Operation ID: ${id}`));
      assert.match(error.message, /cancelled client-side/);
      assert.match(error.message, /Revit may still run/);
      assert.match(error.message, /get_revit_operation/);
      return true;
    });
  }));
}

test('operation status uses only the receipt GET endpoint and returns its recorded state', async () => fixture(async f => {
  const id = `${f.bridge().bridgeId}:${randomUUID()}`;
  const receipt = { operation_id: id, state: 'running', result_available: false };
  const before = f.calls.length;
  f.respond(call => {
    assert.equal(call.method, 'GET');
    assert.equal(call.url.pathname, `/operations/${encodeURIComponent(id)}`);
    assert.equal(call.body, undefined);
    assert.equal(call.url.searchParams.has('operation_id'), false, 'status lookup must not create a new tracked operation');
    return new Response(JSON.stringify(receipt));
  });
  const result = await f.registered.get('get_revit_operation').execute('status-call', { operation_id: id });
  assert.equal(f.calls.length, before + 1);
  assert.equal(f.posts().length, 0, 'status lookup must not dispatch a model tool');
  assert.deepEqual(result.details, receipt);
  assert.deepEqual(JSON.parse(result.content[0].text), receipt);
}));

test('legacy bridge accepts a new call without operation metadata', async () => fixture(async f => {
  const result = await f.execute();
  assert.equal(f.posts().length, 1);
  assert.equal(f.posts()[0].url.searchParams.has('operation_id'), false);
  assert.deepEqual(JSON.parse(f.posts()[0].body), changes);
  assert.deepEqual(result.details, { payload: { updated: 1 } });
  assert.ok(result.content.every(block => !block.text.includes('Operation ID:')));
}, { supportsOperationTracking: undefined, bridgeId: undefined }));
