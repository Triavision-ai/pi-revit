import assert from "node:assert/strict";
import { mkdtemp, mkdir, readFile, writeFile, rm } from "node:fs/promises";
import { createRequire } from "node:module";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { after, test } from "node:test";

// Uses the same TS loader and typebox dependency as the installed Pi runtime.
// No bridge or Revit process is contacted: every fetch is intercepted below.
const runtimeRoot = process.env.PI_CODING_AGENT_PATH;
const require = createRequire(runtimeRoot ? path.join(runtimeRoot, "package.json") : import.meta.url);
const { createJiti } = require("jiti");
const jiti = createJiti(import.meta.url, { alias: { typebox: require.resolve("typebox") } });
const extensionPath = fileURLToPath(new URL("../../extensions/pi-revit/index.ts", import.meta.url));
const { default: connector } = await jiti.import(extensionPath);
const version = JSON.parse(await readFile(new URL('../../package.json', import.meta.url), 'utf8')).version;
const directory = await mkdtemp(path.join(os.tmpdir(), "pi-revit-contract-"));
const originalAppData = process.env.APPDATA;
const originalFetch = globalThis.fetch;
process.env.APPDATA = directory;
await mkdir(path.join(directory, "RevitBridge"));
await writeFile(path.join(directory, "RevitBridge", "bridge.json"), JSON.stringify({
  baseUrl: "http://mock.invalid", token: "offline-test-token",
}));
let response;
globalThis.fetch = async (url) => {
  const parsed = new URL(url);
  assert.equal(parsed.origin, "http://mock.invalid", "must never contact the real bridge");
  const body = parsed.pathname === "/ping" ? { ok: true, addinVersion: version } : parsed.pathname === "/tools"
    ? { tools: [{ name: "get_element_details" }, { name: "get_elements" }] }
    : response;
  assert.ok(body, `unexpected mock request: ${parsed.pathname}`);
  return new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } });
};
const tools = new Map();
await connector({ registerTool(tool) { tools.set(tool.name, tool); }, on() {} });
const savedPaths = [];
after(async () => {
  globalThis.fetch = originalFetch;
  if (originalAppData === undefined) delete process.env.APPDATA;
  else process.env.APPDATA = originalAppData;
  await rm(directory, { recursive: true, force: true });
  for (const file of savedPaths) await rm(file, { force: true });
});

async function execute(name, payload, summary) {
  response = { success: true, content: [{ type: "text", text: summary }], details: { payload, contentTruncated: false } };
  return tools.get(name).execute("offline-contract", {}, undefined);
}

test("requested parameter values are delivered in model-visible content", async () => {
  const payload = { elements: [{ id: 42, parameters: [{ name: "Comments", value: "Fixture-only value ü 空" }] }] };
  const result = await execute("get_element_details", payload, "1 element, 1 parameter.");
  assert.deepEqual(JSON.parse(result.content[0].text), payload);
  assert.deepEqual(result.details.payload, payload);
});

test("a returned page exposes every ID, including beyond the three-item sample", async () => {
  const payload = { total_count: 7, returned_count: 7, has_more: false, elements: Array.from({ length: 7 }, (_, n) => ({ id: n + 101 })) };
  const result = await execute("get_elements", payload, "7 total. Sample: 101, 102, 103.");
  assert.deepEqual(JSON.parse(result.content[0].text), payload);
});

test("a failed result-directory creation can recover on the next large result", async () => {
  const payload = { value: "x".repeat(13000) };
  const actualTmpdir = os.tmpdir;
  os.tmpdir = () => path.join(directory, "missing-parent");
  try {
    await assert.rejects(execute("get_element_details", payload, "Summary."), /Revit completed.*could not be saved locally/);
  } finally {
    os.tmpdir = actualTmpdir;
  }
  const result = await execute("get_element_details", payload, "Summary.");
  const locator = JSON.parse(result.content[0].text);
  savedPaths.push(locator.file_path);
  assert.deepEqual(JSON.parse(await readFile(locator.file_path, "utf8")), payload);
});

test("large results are saved completely and reconstruct through bounded local retrieval", async () => {
  const payload = { elements: Array.from({ length: 90 }, (_, n) => ({ id: n + 500, value: `row ${n}: ` + "ä😀".repeat(500) })) };
  const result = await execute("get_elements", payload, "90 total. Sample: 500, 501, 502.");
  assert.ok(result.content[0].text.length <= 12000);
  const locator = JSON.parse(result.content[0].text);
  assert.equal(locator.retrieval.tool, "read_revit_result");
  assert.ok(path.isAbsolute(locator.file_path));
  savedPaths.push(locator.file_path);
  const saved = await readFile(locator.file_path, "utf8");
  assert.deepEqual(JSON.parse(saved), payload);
  const reader = tools.get("read_revit_result");
  assert.ok(reader, "retrieval tool is registered");
  let reconstructed = "";
  let offset = 0;
  while (offset < saved.length) {
    const page = await reader.execute("offline-page", { result_id: locator.result_id, offset, limit: 8000 }, undefined);
    assert.ok(page.content[0].text.length <= 12000, "bounded model message");
    const data = JSON.parse(page.content[0].text);
    assert.equal(data.offset, offset);
    assert.equal(data.total_chars, saved.length);
    assert.ok(data.returned_chars > 0);
    reconstructed += data.text;
    offset = data.next_offset ?? saved.length;
  }
  assert.equal(reconstructed, saved, "all content is retrievable without omissions or duplicate slices");
});

test("retrieval rejects unknown IDs and invalid ranges without arbitrary file access", async () => {
  const reader = tools.get("read_revit_result");
  assert.ok(reader, "retrieval tool is registered");
  await assert.rejects(reader.execute("bad", { result_id: "../../outside", offset: 0 }, undefined), /unknown|not found/i);
  await assert.rejects(reader.execute("bad", { result_id: "unused", offset: -1 }, undefined), /offset/i);
});

test("null payloads retain their meaning instead of falling back to a summary", async () => {
  const result = await execute("get_element_details", null, "Summary with no value.");
  assert.equal(JSON.parse(result.content[0].text), null);
});

test("inline boundary is complete, and the next character switches to a saved result", async () => {
  const atLimit = "x".repeat(11998); // JSON string quotes bring the total to 12000.
  const inline = await execute("get_element_details", atLimit, "Short summary.");
  assert.equal(inline.content[0].text.length, 12000);
  assert.equal(JSON.parse(inline.content[0].text), atLimit);
  const large = await execute("get_element_details", atLimit + "x", "Short summary.");
  const locator = JSON.parse(large.content[0].text);
  savedPaths.push(locator.file_path);
  assert.equal(locator.total_chars, 12001);
  assert.equal(JSON.parse(await readFile(locator.file_path, "utf8")), atLimit + "x");
});

test("escaped long strings stay bounded and remain exact across one-character and end pages", async () => {
  const payload = { value: "\u0000\n\"\\😀".repeat(4000) };
  const result = await execute("get_element_details", payload, "Summary.");
  const locator = JSON.parse(result.content[0].text);
  savedPaths.push(locator.file_path);
  const original = await readFile(locator.file_path, "utf8");
  const reader = tools.get("read_revit_result");
  let text = "";
  let offset = 0;
  while (offset < original.length) {
    const page = await reader.execute("escaped", { result_id: locator.result_id, offset, limit: offset === 0 ? 1 : 8000 });
    assert.ok(page.content[0].text.length <= 12000);
    const data = JSON.parse(page.content[0].text);
    assert.equal(data.fragment, true);
    assert.ok(data.returned_chars > 0);
    text += data.text;
    offset += data.returned_chars;
    assert.equal(data.next_offset, offset === original.length ? null : offset);
  }
  assert.equal(text, original);
  assert.deepEqual(JSON.parse(text), payload);
  const eof = JSON.parse((await reader.execute("end", { result_id: locator.result_id, offset: original.length })).content[0].text);
  assert.equal(eof.text, "");
  assert.equal(eof.has_more, false);
  assert.equal(eof.next_offset, null);
  for (const bad of [{ offset: original.length + 1 }, { offset: 0.5 }, { limit: 0 }, { limit: 8001 }])
    await assert.rejects(reader.execute("invalid", { result_id: locator.result_id, ...bad }), /offset|limit/);
  await rm(locator.file_path);
  await assert.rejects(reader.execute("missing", { result_id: locator.result_id }), /ENOENT/);
});

test("legacy text-only responses remain visible and bridge errors are not reported as success", async () => {
  response = { success: true, content: [{ type: "text", text: "Legacy text response." }] };
  const result = await tools.get("get_elements").execute("legacy", {}, undefined);
  assert.equal(result.content[0].text, "Legacy text response.");
  response = { success: false, message: "Fixture failure: rolled back." };
  await assert.rejects(tools.get("get_elements").execute("error", {}, undefined), /Fixture failure: rolled back/);
});
