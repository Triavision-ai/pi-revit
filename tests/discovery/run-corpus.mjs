// Report discovery corpus recall and every miss (npm run test:docs enforces the thresholds).
// Usage: node tests/discovery/run-corpus.mjs
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { createLoader, evaluateCorpus, resolvePiDependencies, root } from '../../scripts/lib/platform.mjs';

const corpus = JSON.parse(await readFile(path.join(root, 'tests', 'discovery', 'corpus.json'), 'utf8'));
const version = JSON.parse(await readFile(path.join(root, 'package.json'), 'utf8')).version;
const results = await evaluateCorpus(createLoader(resolvePiDependencies()), corpus, version);
const passed = results.filter(r => r.pass).length;
for (const miss of results.filter(r => !r.pass))
  console.log(`MISS [${miss.lang}] "${miss.q}" expected ${miss.expect ?? miss.expect_guidance ?? 'fallback'} (${miss.match}) got ${miss.got.slice(0, 5).join(', ') || '-'}`);
for (const lang of [...new Set(results.map(r => r.lang))]) {
  const subset = results.filter(r => r.lang === lang);
  console.log(`${lang}: ${subset.filter(r => r.pass).length}/${subset.length}`);
}
console.log(`recall ${passed}/${results.length} = ${(passed / results.length).toFixed(3)} (minimum ${corpus.min_recall})`);
