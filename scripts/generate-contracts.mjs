// Generates every derived documentation artifact from code, the single owner of
// executable contracts:
//   skills/pi-revit/contracts.generated.json   offline contract snapshot for discovery
//   "Contract (generated)" block in each tool manual
//   generated tables in skills/pi-revit/references/tool-index.md
// Usage: node scripts/generate-contracts.mjs [--check]
// --check writes nothing and fails when any artifact is stale.
import { existsSync } from 'node:fs';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  applyIndexBlock, applyManualBlock, createLoader, extractBridgeDescriptors, manifestPath, manualRoot,
  registerExtension, renderManualBlock, renderToolIndex, resolvePiDependencies, root, snapshotPath, toolIndexPath,
} from './lib/platform.mjs';

export async function buildArtifacts() {
  const piRequire = resolvePiDependencies();
  const loader = createLoader(piRequire);
  // The extension imports the snapshot; bootstrap an empty one on a first run.
  if (!existsSync(snapshotPath)) await writeFile(snapshotPath, JSON.stringify({ schema_version: 1, tools: [] }, null, 2) + '\n');
  const packageInfo = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8'));
  const manifest = JSON.parse(await readFile(manifestPath, 'utf8'));
  const descriptors = await extractBridgeDescriptors();
  const { contractHash, NATIVE_CONTRACTS, withApiLookups } = await loader.import(path.join(root, 'extensions', 'pi-revit', 'contracts.ts'));
  const fixture = await registerExtension(loader, descriptors, packageInfo.version);
  let tools;
  try {
    const bridge = new Map(descriptors.map(d => [d.name, d]));
    tools = [...fixture.registered.keys()].sort().map(name => {
      const d = bridge.get(name);
      if (d) return {
        name, source: 'bridge', tier: d.tier ?? 'core', write: d.write === true, effects: d.effects ?? [], requires_document: d.requiresDocument !== false,
        document_kinds: d.documentKinds ?? null,
        keywords: d.keywords ?? [], limits: (d.limits ?? []).map(l => ({ what: l.what, alternative: { kind: l.alternative.kind, ref: l.alternative.ref ?? null } })),
        verification: d.verification ?? null, contract_hash: contractHash(d), parameters: d.parameters,
      };
      const native = NATIVE_CONTRACTS[name];
      if (!native) throw new Error(`Native tool ${name} has no NATIVE_CONTRACTS entry in extensions/pi-revit/contracts.ts`);
      const parameters = JSON.parse(JSON.stringify(fixture.registered.get(name).parameters));
      return {
        name, source: 'native', tier: 'core', write: native.write, effects: native.effects, requires_document: false, document_kinds: null,
        keywords: native.keywords, limits: native.limits, verification: native.verification,
        contract_hash: contractHash({ parameters, write: native.write, effects: native.effects, requiresDocument: false }), parameters,
      };
    });
  } finally { await fixture.restore(); }

  const snapshot = JSON.stringify({
    schema_version: 1,
    generated_by: 'scripts/generate-contracts.mjs',
    note: 'Generated from src/Revit/Tools/*.cs (bridge) and extensions/pi-revit/contracts.ts (native). Do not edit; change the code and regenerate.',
    tools,
  }, null, 2) + '\n';
  const files = new Map([[snapshotPath, snapshot]]);
  for (const tool of tools) {
    const file = path.join(manualRoot, `${tool.name}.md`);
    if (!existsSync(file)) continue; // missing manuals are reported by the documentation checker
    files.set(file, applyManualBlock(await readFile(file, 'utf8'), renderManualBlock({ ...tool, limits: withApiLookups(tool.limits) })));
  }
  files.set(toolIndexPath, applyIndexBlock(await readFile(toolIndexPath, 'utf8'), renderToolIndex(manifest, tools)));
  return { files, tools, descriptors };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === path.resolve(process.argv[1])) {
  const check = process.argv.includes('--check');
  const { files } = await buildArtifacts();
  const stale = [];
  for (const [file, content] of files) {
    const current = existsSync(file) ? await readFile(file, 'utf8') : null;
    if (current === content) continue;
    stale.push(path.relative(root, file));
    if (!check) await writeFile(file, content);
  }
  if (check && stale.length) {
    console.error(`Generated artifacts are stale. Run npm run generate:contracts:\n${stale.join('\n')}`);
    process.exit(1);
  }
  console.log(check ? `Generated artifacts are current (${files.size} files).` : `Updated ${stale.length} of ${files.size} generated artifacts.`);
}
