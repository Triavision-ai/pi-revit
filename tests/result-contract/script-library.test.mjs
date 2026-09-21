import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, readdir, writeFile, rename, rm } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { createScriptLibrary } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/script-library.ts', import.meta.url)));
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const packageVersion = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const definition = (patch = {}) => ({ name: 'audit', description: 'Fixture script', code: 'return inputs.GetProperty("label").GetString();', input_types: { label: 'string' }, ...patch });

async function fixture(action, options = {}) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-script-test-'));
  const root = path.join(directory, 'pi-revit', 'scripts');
  const library = createScriptLibrary(root);
  const previous = { appData: process.env.APPDATA, fetch: globalThis.fetch };
  const events = new Map();
  const tools = new Map();
  const posts = [];
  const bridge = { baseUrl: 'http://scripts.invalid', token: 'fixture-secret', bridgeId: randomUUID().replaceAll('-', ''), supportsOperationTracking: true, ...options };
  let respond;
  try {
    process.env.APPDATA = directory;
    await mkdir(path.join(directory, 'RevitBridge'));
    await writeFile(path.join(directory, 'RevitBridge', 'bridge.json'), JSON.stringify(bridge));
    globalThis.fetch = async (url, init) => {
      const parsed = new URL(url);
      assert.equal(parsed.origin, 'http://scripts.invalid', 'fixture must never contact a real bridge');
      if (parsed.pathname === '/ping') return new Response(JSON.stringify({ ok: true, bridgeId: bridge.bridgeId, addinVersion: packageVersion }));
      if (parsed.pathname === '/tools') return new Response(JSON.stringify({ tools: [{ name: 'execute_csharp', write: true, parameters: { type: 'object', properties: {} } }] }));
      assert.equal(parsed.pathname, '/tools/execute_csharp/execute');
      assert.equal(init.method, 'POST');
      const call = { url: parsed, body: JSON.parse(init.body), signal: init.signal };
      posts.push(call);
      if (respond) return respond(call);
      return new Response(JSON.stringify({ success: true, details: { payload: { answer: 'Fixture answer' } } }));
    };
    await connector({ registerTool(tool) { tools.set(tool.name, tool); }, on(name, callback) { events.set(name, callback); } });
    const execute = args => tools.get('manage_revit_scripts').execute('script-fixture', args);
    await action({ library, root, bridge, posts, execute, respond(callback) { respond = callback; } });
  } finally {
    try { await events.get('session_shutdown')?.({}, {}); }
    finally {
      globalThis.fetch = previous.fetch;
      if (previous.appData === undefined) delete process.env.APPDATA;
      else process.env.APPDATA = previous.appData;
      assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
      assert.ok(path.basename(directory).startsWith('pi-revit-script-test-'));
      await rm(directory, { recursive: true, force: true });
    }
  }
}

test('saved versions are immutable, content-addressed and deduplicated across input declaration order', async () => fixture(async f => {
  const first = await f.library.save(definition({ input_types: { z: 'boolean', a: 'string' } }));
  const repeated = await f.library.save(definition({ input_types: { a: 'string', z: 'boolean' } }));
  assert.match(first.version, /^[a-f0-9]{64}$/);
  assert.equal(first.version, repeated.version);
  assert.equal(first.created_at, repeated.created_at);
  assert.equal((await readdir(path.join(f.root, 'versions'))).length, 1);
  const changed = await f.library.save(definition({ code: 'return "Changed";' }));
  assert.notEqual(changed.version, first.version);
  assert.equal((await f.library.read(first.name, first.version)).code, definition().code);
  assert.equal((await f.library.read(changed.name, changed.version)).code, 'return "Changed";');
}));

test('tampering with saved content or version is rejected without replacing the original file', async () => fixture(async f => {
  for (const field of ['code', 'description', 'input_types', 'version', 'name']) {
    const saved = await f.library.save(definition({ name: `audit_${field}` }));
    const value = JSON.parse(await readFile(saved.file_path, 'utf8'));
    value[field] = field === 'input_types' ? { changed: 'number' } : field === 'version' ? 'f'.repeat(64) : 'tampered';
    await writeFile(saved.file_path, JSON.stringify(value));
    await assert.rejects(f.library.read(saved.name, saved.version), /integrity check failed/);
    await assert.rejects(f.library.save(definition({ name: saved.name })), /integrity check failed/);
    assert.deepEqual(JSON.parse(await readFile(saved.file_path, 'utf8')), value, 'dedupe must never overwrite tampered content');
  }
  assert.equal(f.posts.length, 0);
}));

test('script names and version identifiers cannot escape their storage path', async () => fixture(async f => {
  for (const name of ['', '../audit', 'audit/child', 'audit\\child', 'Audit', '.hidden', 'a'.repeat(65)]) {
    await assert.rejects(f.library.save(definition({ name })), /name must/);
    await assert.rejects(f.library.list(name, 0, 10), /name must/);
  }
  const saved = await f.library.save(definition());
  for (const version of ['', '../version', 'a'.repeat(63), 'A'.repeat(64), 17])
    await assert.rejects(f.library.read(saved.name, version), /version must/);
}));

test('exact-name listing handles double-hyphen names and stable pagination', async () => fixture(async f => {
  await f.library.save(definition({ name: 'audit', code: 'return 1;' }));
  await f.library.save(definition({ name: 'audit', code: 'return 2;' }));
  await f.library.save(definition({ name: 'audit--child', code: 'return 3;' }));
  const first = await f.library.list('audit', 0, 1);
  const second = await f.library.list('audit', first.next_offset, 1);
  assert.equal(first.total_count, 2);
  assert.equal(second.total_count, 2);
  assert.equal(second.next_offset, null);
  assert.notEqual(first.entries[0].version, second.entries[0].version);
  assert.ok([...first.entries, ...second.entries].every(entry => entry.name === 'audit'));
  const child = await f.library.list('audit--child', 0, 10);
  assert.equal(child.total_count, 1);
  assert.equal(child.entries[0].name, 'audit--child');
  assert.equal((await f.library.list(undefined, 0, 10)).total_count, 3);
}));

test('definition constraints reject unsupported declarations and enforce documented size limits', async () => fixture(async f => {
  for (const patch of [
    { code: '' }, { code: '   ' }, { code: 'x'.repeat(100001) }, { description: 'x'.repeat(2001) },
    { input_types: null }, { input_types: [] }, { input_types: { data: 'schema' } },
    { input_types: { data: { type: 'object' } } }, { input_types: { '../data': 'string' } },
    { input_types: Object.fromEntries(Array.from({ length: 41 }, (_, i) => [`input${i}`, 'string'])) },
  ]) await assert.rejects(f.library.save(definition(patch)));
  const maximum = await f.library.save(definition({ code: 'x'.repeat(100000), description: 'x'.repeat(2000), input_types: Object.fromEntries(Array.from({ length: 40 }, (_, i) => [`input${i}`, 'string'])) }));
  assert.equal((await f.library.read(maximum.name, maximum.version)).code.length, 100000);
}));

test('all declared input kinds are strict and top-level object/array contents remain script responsibility', async () => fixture(async f => {
  const savedInfo = await f.library.save(definition({ input_types: { text: 'string', count: 'integer', value: 'number', flag: 'boolean', data: 'object', rows: 'array' } }));
  const saved = await f.library.read(savedInfo.name, savedInfo.version);
  const valid = { text: 'value', count: 2, value: 2.5, flag: false, data: { nested: [null, 'domain-specific'] }, rows: [{ arbitrary: true }, 3] };
  const before = structuredClone(valid);
  assert.equal(f.library.validateInputs(saved, valid), valid);
  assert.deepEqual(valid, before, 'validation must not coerce or mutate inputs');
  for (const [key, values] of Object.entries({ text: [2, null], count: [2.5, '2', Number.MAX_SAFE_INTEGER + 1], value: ['2.5', NaN, Infinity], flag: ['false', 0], data: [null, []], rows: [{}, '[]'] }))
    for (const value of values) assert.throws(() => f.library.validateInputs(saved, { ...valid, [key]: value }), new RegExp(`Input '${key}'`));
  const missing = { ...valid }; delete missing.flag;
  assert.throws(() => f.library.validateInputs(saved, missing), /flag.*required/);
  assert.throws(() => f.library.validateInputs(saved, { ...valid, extra: true }), /Undeclared input: extra/);
  for (const value of [null, [], 'text']) assert.throws(() => f.library.validateInputs(saved, value), /inputs must be a JSON object/);
  assert.throws(() => f.library.validateInputs(saved, { ...valid, text: 'x'.repeat(100000) }), /exceed 100,000 JSON characters/);
}));

test('native save read list and history never execute a script', async () => fixture(async f => {
  const saved = await f.execute({ action: 'save', ...definition() });
  assert.equal(saved.details.executed, false);
  const read = await f.execute({ action: 'read', name: 'audit', version: saved.details.version });
  assert.equal(read.details.code, definition().code);
  assert.equal((await f.execute({ action: 'list', name: 'audit' })).details.total_count, 1);
  assert.equal((await f.execute({ action: 'history' })).details.total_count, 0);
  assert.equal(f.posts.length, 0);
}));

test('native run requires the exact saved version document identity and valid inputs before dispatch', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  const run = { action: 'run', name: saved.name, version: saved.version, inputs: { label: 'Fixture' }, expected_document_id: 'document-exact-id' };
  for (const id of [undefined, '', null, 12]) await assert.rejects(f.execute({ ...run, expected_document_id: id }), /Run requires expected_document_id/);
  await assert.rejects(f.execute({ ...run, version: undefined }), /version must/);
  await assert.rejects(f.execute({ ...run, version: 'e'.repeat(64) }));
  await assert.rejects(f.execute({ ...run, inputs: {} }), /label.*required/);
  await assert.rejects(f.execute({ ...run, inputs: { label: 'Fixture', extra: 1 } }), /Undeclared input/);
  assert.equal(f.posts.length, 0);
  assert.equal((await f.library.readHistory(undefined, 0, 20)).total_count, 0);
}));

test('native execution records its receipt before POST and preserves code inputs and document arguments', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  const input = { label: 'Quoted "value" with {braces} and 日本語' };
  let prepared;
  f.respond(async call => {
    const history = await f.library.readHistory('audit', 0, 20);
    assert.equal(history.total_count, 1, 'receipt history must exist before fetch dispatch');
    prepared = history.entries[0];
    assert.equal(prepared.state, 'prepared');
    assert.equal(prepared.operation_id, call.url.searchParams.get('operation_id'));
    assert.equal(prepared.bridge_id, f.bridge.bridgeId);
    assert.equal(prepared.expected_document_id, 'document-exact-id');
    assert.equal(prepared.version, saved.version);
    assert.match(prepared.inputs_hash, /^[a-f0-9]{64}$/);
    assert.equal(Object.hasOwn(prepared, 'inputs'), false);
    assert.equal(JSON.stringify(prepared).includes(input.label), false);
    assert.equal(call.url.searchParams.get('timeout_ms'), '120000');
    assert.deepEqual(call.body, { code: definition().code, inputs: input, expected_document_id: 'document-exact-id' });
    return new Response(JSON.stringify({ success: true, details: { payload: { result: 'Script result' } } }));
  });
  const result = await f.execute({ action: 'run', name: saved.name, version: saved.version, inputs: input, expected_document_id: 'document-exact-id' });
  assert.equal(result.details.operation_id, prepared.operation_id);
  const completed = (await f.library.readHistory('audit', 0, 20)).entries[0];
  assert.equal(completed.state, 'response_received');
  assert.equal(completed.run_id, prepared.run_id);
  assert.ok(completed.completed_at);
  assert.equal(Object.hasOwn(completed, 'result'), false);
}));

test('invalid explicit operation IDs survive library argument preparation and reject before execution', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  for (const id of ['', null, 17, {}]) {
    await assert.rejects(f.execute({ action: 'run', name: saved.name, version: saved.version,
      inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc', _operation_id: id }), /_operation_id must be the exact ID/);
  }
  assert.equal(f.posts.length, 0, 'invalid retry IDs must not become fresh executions');
  assert.equal((await f.library.readHistory(undefined, 0, 20)).total_count, 0);
}));

test('running an earlier immutable version executes its code rather than the newest version', async () => fixture(async f => {
  const old = await f.library.save(definition({ code: 'return "old";' }));
  await f.library.save(definition({ code: 'return "new";' }));
  await f.execute({ action: 'run', name: old.name, version: old.version, inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc' });
  assert.equal(f.posts[0].body.code, 'return "old";');
}));

test('interrupted run records outcome_unconfirmed and identical retry preserves receipt and arguments', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  const args = { action: 'run', name: saved.name, version: saved.version, inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc' };
  f.respond(() => { throw new Error('Fixture connection lost after dispatch'); });
  await assert.rejects(f.execute(args), /Operation ID:.*get_revit_operation/s);
  const failed = (await f.library.readHistory('audit', 0, 20)).entries[0];
  assert.equal(failed.state, 'outcome_unconfirmed');
  assert.equal(failed.operation_id, f.posts[0].url.searchParams.get('operation_id'));
  f.respond(() => new Response(JSON.stringify({ success: true, details: { payload: { recovered: true } } })));
  await f.execute({ ...args, _operation_id: failed.operation_id });
  assert.equal(f.posts[1].url.searchParams.get('operation_id'), failed.operation_id);
  assert.deepEqual(f.posts[1].body, f.posts[0].body);
  assert.equal(Object.hasOwn(f.posts[1].body, '_operation_id'), false);
  const history = await f.library.readHistory('audit', 0, 20);
  assert.equal(history.total_count, 2);
  assert.deepEqual(history.entries.map(entry => entry.state).sort(), ['outcome_unconfirmed', 'response_received']);
  assert.ok(history.entries.every(entry => entry.operation_id === failed.operation_id));
}));

test('script domain rejection is surfaced and never treated as a successful response', async () => fixture(async f => {
  const saved = await f.library.save(definition({ input_types: { data: 'object' } }));
  f.respond(() => new Response(JSON.stringify({ error: true, message: 'Fixture script domain rule rejected nested data' }), { status: 400 }));
  await assert.rejects(f.execute({ action: 'run', name: saved.name, version: saved.version, inputs: { data: { nested: 'invalid-for-script' } }, expected_document_id: 'exact-doc' }), /domain rule rejected/);
  assert.equal(f.posts.length, 1, 'nested domain rules belong to the script, not top-level kind validation');
  assert.equal((await f.library.readHistory('audit', 0, 20)).entries[0].state, 'outcome_unconfirmed');
}));

test('missing tracking support rejects library execution before POST', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  await assert.rejects(f.execute({ action: 'run', name: saved.name, version: saved.version, inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc' }), /require a bridge with operation receipts.*not sent/);
  assert.equal(f.posts.length, 0);
  assert.equal((await f.library.readHistory(undefined, 0, 20)).total_count, 0);
}, { supportsOperationTracking: false }));

test('pre-dispatch history failure prevents execution', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  await writeFile(path.join(f.root, 'history'), 'fixture blocking history directory');
  await assert.rejects(f.execute({ action: 'run', name: saved.name, version: saved.version, inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc' }), /Operation ID:/);
  assert.equal(f.posts.length, 0, 'script must not run if its prepared receipt cannot be persisted');
}));

test('final history failure preserves the received result and adds an explicit warning', async () => fixture(async f => {
  const saved = await f.library.save(definition());
  f.respond(async () => {
    const prepared = (await f.library.readHistory('audit', 0, 20)).entries[0];
    const historyFile = path.join(f.root, 'history', `${prepared.run_id}.json`);
    await rename(historyFile, `${historyFile}.fixture-backup`);
    await mkdir(historyFile);
    return new Response(JSON.stringify({ success: true, details: { payload: { answer: 'received' } } }));
  });
  const result = await f.execute({ action: 'run', name: saved.name, version: saved.version, inputs: { label: 'Fixture' }, expected_document_id: 'exact-doc' });
  assert.deepEqual(result.details.payload, { answer: 'received' });
  assert.ok(result.content.some(block => /final local history update failed/.test(block.text)));
  assert.equal(f.posts.length, 1, 'history failure must not trigger another execution');
}));

test('history filters exact script names and paginates deterministically', async () => fixture(async f => {
  for (const [name, started_at] of [['audit', '2026-01-01T00:00:00.000Z'], ['audit', '2026-01-02T00:00:00.000Z'], ['audit--child', '2026-01-03T00:00:00.000Z']])
    await f.library.writeHistory({ run_id: randomUUID(), name, started_at, state: 'prepared' });
  const first = await f.library.readHistory('audit', 0, 1);
  const second = await f.library.readHistory('audit', first.next_offset, 1);
  assert.equal(first.total_count, 2);
  assert.equal(first.entries[0].started_at, '2026-01-02T00:00:00.000Z');
  assert.equal(second.entries[0].started_at, '2026-01-01T00:00:00.000Z');
  assert.equal(second.next_offset, null);
}));
