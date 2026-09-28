import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, realpath, rm, symlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { createDocumentationResolver, resolveToolDocumentation } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/tool-documentation.ts', import.meta.url)));
const { publicBridgeSchema } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/tool-schema.ts', import.meta.url)));
const { contractHash } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/contracts.ts', import.meta.url)));
const snapshot = JSON.parse(await readFile(new URL('../../skills/pi-revit/contracts.generated.json', import.meta.url), 'utf8'));
const packagedHash = name => snapshot.tools.find(tool => tool.name === name).contract_hash;

async function fixture(action) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-documentation-'));
  try { await action(directory); }
  finally {
    assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('pi-revit-documentation-'));
    await rm(directory, { recursive: true, force: true });
  }
}

test('packaged manual resolves to an absolute readable file independently of working directory', async () => {
  const originalDirectory = process.cwd();
  try {
    process.chdir(os.tmpdir());
    const result = resolveToolDocumentation('get_elements');
    const expected = await realpath(fileURLToPath(new URL('../../skills/pi-revit/references/tools/get_elements.md', import.meta.url)));
    assert.equal(result.path, expected);
    assert.equal(result.status, 'available');
    assert.ok(path.isAbsolute(result.path));
    assert.match(await readFile(result.path, 'utf8'), /get_elements/);
  } finally { process.chdir(originalDirectory); }
});

test('manual compatibility distinguishes matching, changed, undocumented, unknown and native contracts', () => {
  assert.equal(resolveToolDocumentation('get_elements', { contractHash: packagedHash('get_elements') }).compatibility, 'contract_match');
  const changed = resolveToolDocumentation('get_elements', { contractHash: 'fixture-changed', bridgeVersion: 'fixture-version' });
  assert.equal(changed.compatibility, 'contract_changed');
  assert.equal(changed.packaged_contract_hash, packagedHash('get_elements'));
  assert.equal(changed.observed_bridge_version, 'fixture-version');
  assert.equal(resolveToolDocumentation('get_elements').compatibility, 'unknown');
  const future = resolveToolDocumentation('future_bridge_tool', { contractHash: 'fixture' });
  assert.equal(future.compatibility, 'undocumented', 'a newer bridge tool stays usable but is flagged as undocumented');
  assert.equal(future.status, 'missing');
  const native = resolveToolDocumentation('find_revit_tools', { contractHash: 'fixture' });
  assert.equal(native.compatibility, 'package_local');
  assert.equal(native.observed_bridge_version, null);
  assert.equal(native.live_contract_hash, null);
});

test('contract hash covers executable inputs and effects but not guidance wording', () => {
  const base = { parameters: { type: 'object', properties: { a: { type: 'string', description: 'x' } }, required: ['a'] }, write: true, effects: ['model'] };
  const hash = contractHash(base);
  assert.equal(contractHash({ ...base, description: 'reworded', promptGuidelines: ['new'], keywords: ['k'], limits: [] }), hash);
  assert.equal(contractHash({ ...base, parameters: { required: ['a'], properties: { a: { description: 'x', type: 'string' } }, type: 'object' } }), hash, 'key order is irrelevant');
  assert.equal(contractHash({ ...base, parameters: { ...base.parameters, properties: { a: { type: 'string', description: 'reworded property guidance' } } } }), hash,
    'rewording a property description is guidance, not a contract change');
  assert.notEqual(contractHash({ ...base, parameters: { ...base.parameters, properties: { a: { type: 'integer', description: 'x' } } } }), hash, 'a type change is a contract change');
  assert.notEqual(contractHash({ ...base, parameters: { ...base.parameters, properties: { description: { type: 'string' } } } }), hash, 'a property named description is still an input');
  assert.notEqual(contractHash({ ...base, parameters: { ...base.parameters, required: [] } }), hash);
  assert.notEqual(contractHash({ ...base, effects: ['model', 'files'] }), hash);
  assert.notEqual(contractHash({ ...base, requiresDocument: false }), hash);
});

test('only allowlisted tool names can resolve documentation paths', async () => fixture(async directory => {
  await writeFile(path.join(directory, 'unregistered_tool.md'), 'fixture');
  const resolve = createDocumentationResolver(directory);
  for (const name of ['unregistered_tool', '../get_elements', '..\\get_elements', '/get_elements', 'C:\\get_elements']) {
    const result = resolve(name);
    assert.equal(result.status, 'missing');
    assert.equal(result.path, null);
  }
}));

test('missing non-file and invalid documentation roots remain nonfatal', async () => fixture(async directory => {
  const resolve = createDocumentationResolver(directory);
  assert.equal(resolve('get_elements').status, 'missing');
  await mkdir(path.join(directory, 'get_elements.md'));
  const nonFile = resolve('get_elements');
  assert.equal(nonFile.status, 'missing');
  assert.equal(nonFile.path, null);
  assert.equal(createDocumentationResolver(path.join(directory, 'absent'))('get_elements').status, 'missing');
  assert.equal(createDocumentationResolver('\0')('get_elements').status, 'missing', 'filesystem lookup exceptions must not break registration');
}));

test('realpath containment rejects a manual link escaping its documentation root', async () => fixture(async directory => {
  const root = path.join(directory, 'manuals'), outside = path.join(directory, 'outside');
  await mkdir(root); await mkdir(outside);
  // A directory junction is available on Windows without file-symlink privileges.
  await symlink(outside, path.join(root, 'get_elements.md'), process.platform === 'win32' ? 'junction' : 'dir');
  const result = createDocumentationResolver(root)('get_elements');
  assert.equal(result.status, 'missing');
  assert.equal(result.path, null);
}));

test('public schema preserves bridge document guards and does not mutate the bridge schema', () => {
  const bridge = { type: 'object', properties: { expected_document_id: { type: 'string' }, updates: { type: 'array' } }, required: ['updates', 'expected_document_id'] };
  const before = structuredClone(bridge);
  const schema = publicBridgeSchema(bridge);
  assert.deepEqual(schema.required, bridge.required);
  assert.deepEqual(schema.properties.expected_document_id, bridge.properties.expected_document_id);
  assert.equal(schema.properties._operation_id.type, 'string');
  assert.equal(schema.required.includes('_operation_id'), false);
  schema.properties.expected_document_id.description = 'fixture mutation';
  assert.deepEqual(bridge, before);
});
