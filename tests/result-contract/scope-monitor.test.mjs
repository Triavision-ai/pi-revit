import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir, readFile, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import { fileURLToPath } from 'node:url';

// Result-driven request monitors, for every present and future tool: objects that predate the
// request (inv:existing-objects-not-reused), name collisions, and whether a call actually changed
// the model (model_changes, inv:model-changes-reported). The bridge is an in-memory fixture.
const require = createRequire(path.join(process.env.PI_CODING_AGENT_PATH, 'package.json'));
const { createJiti } = require('jiti');
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve('typebox') } });
const { default: connector } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/index.ts', import.meta.url)));
const { createScopeMonitor, changedModel, findCollisions, wordBoundaries, mentionsName } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/scope-monitor.ts', import.meta.url)));
const { createCompletionMonitor } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/completion-monitor.ts', import.meta.url)));
const { apiLookupQuery } = await jiti.import(fileURLToPath(new URL('../../extensions/pi-revit/contracts.ts', import.meta.url)));
const version = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const packaged = new Map(JSON.parse(await readFile(new URL('../../skills/pi-revit/contracts.generated.json', import.meta.url), 'utf8')).tools.map(t => [t.name, t]));
const descriptor = name => { const p = packaged.get(name); return { name, description: name, tier: 'core', write: p.write, effects: p.effects, requiresDocument: p.requires_document, parameters: structuredClone(p.parameters) }; };

const changes = ({ added = [], modified = [] } = {}) => ({ observed: true,
  added: { count: added.length, items: added }, modified: { count: modified.length, items: modified }, deleted: { count: 0, ids: [] } });

async function fixture(action) {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-scope-'));
  const originals = { fetch: globalThis.fetch, appData: process.env.APPDATA, setInterval: globalThis.setInterval };
  process.env.APPDATA = directory;
  await mkdir(path.join(directory, 'RevitBridge'));
  await writeFile(path.join(directory, 'RevitBridge', 'bridge.json'), JSON.stringify({ baseUrl: 'http://scope.invalid', token: 'test' }));
  globalThis.setInterval = () => ({ unref() {} });
  const replies = [];
  globalThis.fetch = async url => {
    const parsed = new URL(url);
    if (parsed.pathname === '/tools') return new Response(JSON.stringify({ tools: [descriptor('manage_views'), descriptor('execute_csharp')] }));
    if (parsed.pathname.endsWith('/execute')) {
      const payload = replies.shift();
      return new Response(JSON.stringify({ success: true, content: [{ type: 'text', text: JSON.stringify(payload) }], details: { payload } }));
    }
    return new Response(JSON.stringify({ ok: true, addinVersion: version }));
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
    await handlers.get('session_start')({}, { ui: { notify() {} } });
    const request = prompt => handlers.get('before_agent_start')({ prompt, systemPromptOptions: { sections: {} } }, {});
    const call = async (name, payload) => { replies.push(payload); return (await registered.get(name).execute('t', { expected_document_id: 'doc' })).content.map(c => c.text).join('\n'); };
    await action({ request, call });
  } finally {
    await handlers.get('session_shutdown')?.({}, {});
    globalThis.fetch = originals.fetch; globalThis.setInterval = originals.setInterval;
    if (originals.appData === undefined) delete process.env.APPDATA; else process.env.APPDATA = originals.appData;
    await rm(directory, { recursive: true, force: true });
  }
}

test('a write to a pre-existing object named in the request adds a scope note', async () => fixture(async f => {
  await f.request('Create a perspective 3D view of the building named "East Roof Study". Leave the file unsaved.');
  const text = await f.call('execute_csharp', { returnValue: 'ok', model_changes: changes({ modified: [{ id: 4101, name: 'East Roof Study' }, { id: 12, name: 'Project Browser' }] }) });
  assert.match(text, /PI-Revit scope note: this call changed 'East Roof Study' \(id 4101\), which existed before this request/);
  assert.match(text, /ask before changing it further/);
  const note = text.split('\n').filter(line => line.startsWith('PI-Revit scope note')).join('\n');
  assert.doesNotMatch(note, /Project Browser/, 'side effects on objects the request does not name are not flagged');
  assert.doesNotMatch(await f.call('execute_csharp', { model_changes: changes({ modified: [{ id: 4101, name: 'East Roof Study' }] }) }), /scope note/, 'noted once per request');
  await f.request('Rename the view "East Roof Study" to something shorter.');
  assert.match(await f.call('execute_csharp', { model_changes: changes({ modified: [{ id: 4101, name: 'East Roof Study' }] }) }), /If the request asked you to change this existing object, continue/,
    'a new request starts a new ledger; the note leaves an intended change to the agent');
}));

test('objects created in this request are not flagged when edited again', async () => fixture(async f => {
  await f.request('Create a 3D view named "Section Copy" and show the roof.');
  assert.doesNotMatch(await f.call('manage_views', { created: true, model_changes: changes({ added: [{ id: 950, name: 'Section Copy' }] }) }), /scope note/);
  assert.doesNotMatch(await f.call('execute_csharp', { model_changes: changes({ modified: [{ id: 950, name: 'Section Copy' }] }) }), /scope note/);
}));

test('a name collision in a rejected creation adds a scope note', async () => fixture(async f => {
  await f.request('Create a 3D view named "Entrance View".');
  const text = await f.call('manage_views', { committed: false, failed: [{ index: 0, reason: 'already exists', name_collision: { existing_id: 900, kind: 'ThreeD view', name: 'Entrance View' } }] });
  assert.match(text, /'Entrance View' is already used by ThreeD view 900/);
  assert.match(text, /Do not edit, rename, reuse, replace or delete it/);
}));

test('names count as mentioned only as whole words', () => {
  const monitor = createScopeMonitor();
  monitor.reset('Show the opposite side of the building.');
  assert.equal(monitor.afterResult({ model_changes: changes({ modified: [{ id: 1, name: 'Site' }, { id: 2, name: 'Top' }, { id: 3, name: 'x' }] }) }), null,
    "'Site' is not mentioned by 'opposite', 'Top' is absent and a one-character name is ignored");
  assert.deepEqual(findCollisions({ succeeded: [], failed: [{ name_collision: { existing_id: 3, name: 'A' } }], model_changes: { name_collision: { existing_id: 4 } } }).map(c => c.existing_id), [3]);
});

test('object names are recognized in any writing system without language rules', () => {
  const cases = [
    ['请创建名为三维视图南立面的视图', '三维视图南立面'],          // no spaces: dictionary boundaries
    ['東側立面図という名前のビューを作成してください', '東側立面図'],
    ['กรุณาสร้างมุมมองชื่อผังพื้นชั้นหนึ่งให้หน่อย', 'ผังพื้นชั้นหนึ่ง'],
    ['أنشئ عرضًا باسم مخطط الطابق الأول من فضلك', 'مخطط الطابق الأول'],
    ['कृपया पहली मंजिल योजना नाम का दृश्य बनाएं', 'पहली मंजिल योजना'],
    ['Создай вид План первого этажа, пожалуйста', 'План первого этажа'],
    ['동측 입면도 라는 이름으로 뷰를 만들어 주세요', '동측 입면도'],
  ];
  for (const [request, name] of cases) {
    const text = request.normalize('NFKC').toLowerCase();
    assert.equal(mentionsName(text, wordBoundaries(text), name), true, `${name} in ${request}`);
  }
  const compound = 'please check the rooflines';
  assert.equal(mentionsName(compound, wordBoundaries(compound), 'Roofline'), false, 'a name inside a longer word is not a mention');
  const monitor = createScopeMonitor();
  monitor.reset('请把三维视图南立面的相机调低');
  assert.match(monitor.afterResult({ model_changes: changes({ modified: [{ id: 9, name: '三维视图南立面' }] }) }) ?? '', /existed before this request/);
});

test('family types, which have no element ID, follow the same rule by name', () => {
  const monitor = createScopeMonitor();
  monitor.reset('Make the type Type A deeper, and add a type called Type B.');
  const family = f => ({ model_changes: { observed: true, added: { count: 0, items: [] }, modified: { count: 0, items: [] }, deleted: { count: 0 }, family: f } });
  assert.equal(monitor.afterResult(family({ types_added: ['Type B'], types_changed: [] })), null, 'a type created in this request is not flagged');
  assert.equal(monitor.afterResult(family({ types_changed: ['Type B'] })), null, 'editing it afterwards is not flagged either');
  assert.match(monitor.afterResult(family({ types_changed: ['Type A'] })) ?? '', /family type 'Type A', which existed before this request/);
});

test('reported model changes decide whether a call counts as an edit', () => {
  assert.equal(changedModel({ model_changes: changes() }), false);
  assert.equal(changedModel({ model_changes: { observed: false } }), false, 'a read through a write-capable tool changed nothing');
  assert.equal(changedModel({ model_changes: changes({ added: [{ id: 1 }] }) }), true);
  assert.equal(changedModel({ returnValue: 1 }), undefined, 'older bridges fall back to declared metadata');
  const monitor = createCompletionMonitor();
  const capture = { view_id: 42 };
  monitor.afterCall('capture_view', capture, { effects: ['files'] });
  for (let i = 0; i < 4; i++) {
    assert.equal(monitor.afterCall('execute_csharp', { code: `read ${i}` }, { write: true }, false), null);
    assert.equal(monitor.afterCall('capture_view', capture, { effects: ['files'] }), null, 'a read-only script between checks is not an edit');
  }
  assert.deepEqual(monitor.ledger(), {});
});

test('api limits carry a one-call lookup of all their members', () => {
  assert.equal(apiLookupQuery('View3D.CreatePerspective; ViewOrientation3D'), 'View3D.CreatePerspective; ViewOrientation3D');
  assert.equal(apiLookupQuery('Document.Export overloads (e.g. DXFExportOptions, NavisworksExportOptions); verify the installed exporter'), 'Document.Export; NavisworksExportOptions');
  assert.equal(apiLookupQuery('TextNote.Create; Creation.ItemFactoryBase.NewDimension'), 'TextNote.Create; Creation.ItemFactoryBase.NewDimension');
  assert.equal(apiLookupQuery('Custom inspection; verify members with search_api_docs'), null, 'prose names no member');
});
