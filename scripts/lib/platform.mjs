// Shared offline platform helpers for the contract generator and the documentation
// checker. Nothing here contacts Revit, the network or a live model: bridge metadata
// comes from the real C# registry compiled with stubbed tool bodies, and the Pi
// extension is registered against an isolated fake bridge.
import { createRequire } from 'node:module';
import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
export const skillRoot = path.join(root, 'skills', 'pi-revit');
export const manualRoot = path.join(skillRoot, 'references', 'tools');
export const snapshotPath = path.join(skillRoot, 'contracts.generated.json');
export const manifestPath = path.join(skillRoot, 'tool-manifest.json');
export const toolIndexPath = path.join(skillRoot, 'references', 'tool-index.md');

export function resolvePiDependencies() {
  const candidates = [process.env.PI_CODING_AGENT_PATH,
    path.join(path.dirname(process.execPath), 'node_modules', '@earendil-works', 'pi-coding-agent')].filter(Boolean);
  for (const candidate of candidates) {
    const require = createRequire(path.join(path.resolve(candidate), 'package.json'));
    try { require.resolve('jiti'); require.resolve('typebox/value'); return require; } catch {}
  }
  throw new Error('Set PI_CODING_AGENT_PATH to an existing @earendil-works/pi-coding-agent directory containing jiti and typebox. These checks never install dependencies.');
}

export function createLoader(piRequire) {
  const { createJiti } = piRequire('jiti');
  return createJiti(import.meta.url, { alias: { typebox: piRequire.resolve('typebox') }, moduleCache: false });
}

/** Real ToolRegistry.DescribeAll() output, via the Roslyn schema extractor. */
export async function extractBridgeDescriptors() {
  const scratch = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-contracts-'));
  try {
    const output = path.join(scratch, 'bridge.json');
    const run = spawnSync('dotnet', ['run', '--project', path.join(root, 'tests', 'tool-documentation', 'schema-extractor.csproj'), '--', root, output],
      { cwd: root, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024, env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1' } });
    if (run.error || run.status !== 0)
      throw new Error(`Bridge schema extraction failed (installed .NET SDK 8+ required).\n${run.error?.message ?? ''}\n${run.stdout}\n${run.stderr}`);
    return JSON.parse(await readFile(output, 'utf8'));
  } finally { await rm(scratch, { recursive: true, force: true }); }
}

/**
 * Register the real extension against an in-memory bridge that serves `descriptors`.
 * Returns the registrations and event handlers. Every non-metadata route and all
 * real network access are rejected, and no registered execute() is invoked.
 */
export async function registerExtension(loader, descriptors, addinVersion) {
  const scratch = await mkdtemp(path.join(os.tmpdir(), 'pi-revit-register-'));
  const original = { fetch: globalThis.fetch, appdata: process.env.APPDATA, setInterval: globalThis.setInterval };
  process.env.APPDATA = scratch;
  await mkdir(path.join(scratch, 'RevitBridge'));
  await writeFile(path.join(scratch, 'RevitBridge', 'bridge.json'), JSON.stringify({ baseUrl: 'http://platform-fixture.invalid', token: 'offline-fixture' }));
  const routes = [];
  globalThis.fetch = async (input, init) => {
    const url = new URL(typeof input === 'string' || input instanceof URL ? input : input.url);
    if (url.origin !== 'http://platform-fixture.invalid') throw new Error('real network access is forbidden');
    if ((init?.method ?? 'GET') !== 'GET' || !['/ping', '/tools'].includes(url.pathname)) throw new Error(`tool execution is forbidden: ${url.pathname}`);
    routes.push(url.pathname);
    return new Response(JSON.stringify(url.pathname === '/tools' ? { tools: descriptors } : { ok: true, addinVersion }));
  };
  globalThis.setInterval = () => { throw new Error('Background polling is forbidden in platform fixtures'); };
  const registered = new Map(), events = new Map();
  let active = [];
  const pi = {
    registerTool(tool) { if (registered.has(tool.name)) throw new Error(`duplicate registered tool ${tool.name}`); registered.set(tool.name, tool); active = [...active, tool.name]; },
    on(name, handler) { events.set(name, handler); },
    getActiveTools() { return [...active]; },
    setActiveTools(names) { active = [...names]; },
    sendMessage() { throw new Error('Messages are forbidden in platform fixtures'); },
  };
  const restore = async () => {
    await events.get('session_shutdown')?.({}, {});
    globalThis.fetch = original.fetch; globalThis.setInterval = original.setInterval;
    if (original.appdata === undefined) delete process.env.APPDATA; else process.env.APPDATA = original.appdata;
    await rm(scratch, { recursive: true, force: true });
  };
  try {
    const { default: connector } = await loader.import(path.join(root, 'extensions', 'pi-revit', 'index.ts'));
    await connector(pi);
  } catch (error) { await restore(); throw error; }
  return { pi, registered, events, routes, activeTools: () => [...active], restore };
}

/** Human-readable type, requiredness, default and allowed values of one JSON schema property. */
function describeProperty(schema) {
  const values = [];
  let type = schema?.type;
  if (Array.isArray(schema?.enum)) values.push(...schema.enum);
  for (const variant of schema?.anyOf ?? schema?.oneOf ?? []) {
    if (variant.const !== undefined) { values.push(variant.const); type ??= typeof variant.const; }
    else if (variant.type) type = type ? `${type} | ${variant.type}` : variant.type;
  }
  if (schema?.const !== undefined) values.push(schema.const);
  if (type === 'array') {
    const items = schema.items ?? {};
    const itemValues = Array.isArray(items.enum) ? items.enum : (items.anyOf ?? []).filter(v => v.const !== undefined).map(v => v.const);
    values.push(...itemValues);
    type = `array of ${items.type ?? (itemValues.length ? typeof itemValues[0] : 'value')}`;
  }
  if (Array.isArray(type)) type = type.join(' | ');
  return { type: type ?? 'value', values, default: schema?.default };
}

const VERIFICATION_TEXT = {
  reread: 'query the changed state again with a read tool',
  capture: 'capture the visible result and inspect the image',
  inspect_output: 'open and inspect the produced file',
  none: 'no durable outcome to check; report what was done',
};
const cell = text => String(text).replace(/\|/g, '\\|').replace(/\r?\n/g, ' ');

export function alternativeText(limit) {
  const ref = limit.alternative.ref ? cell(limit.alternative.ref) : '';
  switch (limit.alternative.kind) {
    case 'tool': return `Tool: ${ref}`;
    case 'api': return `Revit API: ${ref}. ${limit.lookup ? `Check all its members in one call: \`search_api_docs\` with query \`${cell(limit.lookup.query)}\`` : 'Check with `search_api_docs`'}, then use \`execute_csharp\` within the requested scope.`;
    case 'user': return `User action: ${ref}`;
    case 'revit_unsupported': return `Not offered by the Revit API (${ref}). Report this with the evidence checked.`;
    default: return ref;
  }
}

export const CONTRACT_START = '<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->';
export const CONTRACT_END = '<!-- generated:contract:end -->';

export function renderManualBlock(tool) {
  const lines = [CONTRACT_START, '## Contract (generated)', ''];
  const tier = tool.source === 'native' ? 'Pi extension utility, always active'
    : tool.tier === 'advanced' ? 'bridge tool, advanced tier: activate it with `find_revit_tools`' : 'bridge tool, core tier: active by default';
  lines.push(`- **Source:** ${tier}.`);
  lines.push(`- **Writes model:** ${tool.write ? 'yes' : 'no'}. **Effects:** ${tool.effects.length ? tool.effects.join(', ') : 'none'}. **Requires an open document:** ${tool.requires_document ? 'yes' : 'no'}.`);
  if (tool.document_kinds?.length && tool.requires_document)
    lines.push(`- **Works in:** ${tool.document_kinds.length > 1 ? 'project and family documents' : `${tool.document_kinds[0]} documents only; the bridge refuses other documents before running, with the route to use instead`}.`);
  if (tool.verification) lines.push(`- **Verify the outcome:** \`${tool.verification}\`: ${VERIFICATION_TEXT[tool.verification]}.`);
  lines.push(`- **Contract hash:** \`${tool.contract_hash}\`. \`find_revit_tools\` compares it with the selected bridge's live contract.`);
  lines.push('', '| Input | Type | Required | Default | Allowed values |', '| --- | --- | --- | --- | --- |');
  const schema = tool.parameters ?? {};
  const required = new Set(schema.required ?? []);
  const properties = Object.entries(schema.properties ?? {});
  if (!properties.length) lines.push('| _(none)_ | | | | |');
  for (const [name, property] of properties) {
    const d = describeProperty(property);
    lines.push(`| \`${name}\` | ${cell(d.type)} | ${required.has(name) ? 'yes' : 'no'} | ${d.default === undefined ? '' : `\`${cell(JSON.stringify(d.default))}\``} | ${d.values.map(v => `\`${cell(v)}\``).join(', ')} |`);
  }
  if (tool.source === 'bridge') lines.push('', 'Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).');
  if (tool.limits.length) {
    lines.push('', '| Not covered by this tool | Use instead |', '| --- | --- |');
    for (const limit of tool.limits) lines.push(`| ${cell(limit.what)} | ${alternativeText(limit)} |`);
  }
  lines.push(CONTRACT_END);
  return lines.join('\n');
}

/** Replace the generated block, or insert it before the first "## Inputs" heading (else the second H2). */
export function applyManualBlock(markdown, block) {
  const eol = markdown.includes('\r\n') ? '\r\n' : '\n';
  const body = block.replace(/\n/g, eol);
  const start = markdown.indexOf(CONTRACT_START), end = markdown.indexOf(CONTRACT_END);
  if (start >= 0 && end > start) return markdown.slice(0, start) + body + markdown.slice(end + CONTRACT_END.length);
  const headings = [...markdown.matchAll(/^## .*$/gm)];
  const anchor = headings.find(h => /^## Inputs/.test(h[0])) ?? headings[1];
  if (!anchor) return markdown.trimEnd() + eol + eol + body + eol;
  return markdown.slice(0, anchor.index) + body + eol + eol + markdown.slice(anchor.index);
}

export const INDEX_START = '<!-- generated:tool-index:start (npm run generate:contracts; do not edit this block) -->';
export const INDEX_END = '<!-- generated:tool-index:end -->';

export function renderToolIndex(manifest, contracts) {
  const byName = new Map(contracts.map(tool => [tool.name, tool]));
  const lines = [INDEX_START];
  for (const group of manifest.groups) {
    lines.push('', `## ${group.title}`, '', '| Manual | Use | Tier | Verify |', '| --- | --- | --- | --- |');
    for (const tool of manifest.tools.filter(entry => entry.group === group.id)) {
      const contract = byName.get(tool.name);
      const tier = tool.source === 'native' ? 'native' : contract?.tier ?? '';
      lines.push(`| [${tool.name}](tools/${tool.name}.md) | ${cell(tool.summary)} | ${tier} | ${contract?.verification ?? ''} |`);
    }
  }
  lines.push('', '## Workflows and shared guidance', '', '| Guide | Kind | Use |', '| --- | --- | --- |');
  for (const guide of manifest.guidance) lines.push(`| [${guide.name}](${guide.path.replace(/^references\//, '')}) | ${guide.kind} | ${cell(guide.summary)} |`);
  lines.push(INDEX_END);
  return lines.join('\n');
}

export function applyIndexBlock(markdown, block) {
  const eol = markdown.includes('\r\n') ? '\r\n' : '\n';
  const body = block.replace(/\n/g, eol);
  const start = markdown.indexOf(INDEX_START), end = markdown.indexOf(INDEX_END);
  if (start < 0 || end < start) throw new Error(`tool-index.md needs ${INDEX_START} ... ${INDEX_END} markers`);
  return markdown.slice(0, start) + body + markdown.slice(end + INDEX_END.length);
}

/**
 * Score the discovery corpus against packaged documentation (no bridge needed).
 * An entry passes when its expected tool or guidance appears in the top_n results,
 * or, for expect_fallback, when the response carries the capability route.
 */
export async function evaluateCorpus(loader, corpus, addinVersion) {
  const fixture = await registerExtension(loader, [], addinVersion);
  try {
    const find = fixture.registered.get('find_revit_tools');
    const results = [];
    for (const entry of corpus.entries) {
      const response = (await find.execute('corpus', { scope: 'documentation', query: entry.q, limit: corpus.top_n })).details;
      const tools = response.tools.map(tool => tool.name), guides = response.guidance.map(guide => guide.name);
      const pass = entry.expect_fallback ? Boolean(response.fallback) && response.match === 'none'
        : entry.expect_guidance ? guides.slice(0, corpus.top_n).includes(entry.expect_guidance)
        : tools.slice(0, corpus.top_n).includes(entry.expect);
      results.push({ ...entry, pass, match: response.match, got: entry.expect_guidance ? guides : tools });
    }
    return results;
  } finally { await fixture.restore(); }
}

/** Candidate RevitAPI.xml files for optional API-reference verification. */
export function revitApiXmlPaths() {
  const roots = [process.env.REVIT_API_PATH, 'C:\\Program Files\\Autodesk\\Revit 2025', 'C:\\Program Files\\Autodesk\\Revit 2026', 'C:\\Program Files\\Autodesk\\Revit 2027'].filter(Boolean);
  return roots.map(directory => path.join(directory, 'RevitAPI.xml')).filter(file => existsSync(file));
}
