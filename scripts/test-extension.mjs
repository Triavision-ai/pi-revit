import { readdirSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

// Run sequentially: fixtures replace fetch, timers and discovery paths in-process.
// Each module has a separate process and uses Node's test API directly.
const directory = new URL('../tests/result-contract/', import.meta.url);
for (const file of readdirSync(directory).filter(name => name.endsWith('.test.mjs')).sort()) {
  process.stdout.write(`\n${file}\n`);
  const result = spawnSync(process.execPath, [fileURLToPath(new URL(file, directory))], {
    stdio: 'inherit', env: process.env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
}
