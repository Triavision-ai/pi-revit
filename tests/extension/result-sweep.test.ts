import assert from "node:assert/strict";
import { mkdir, mkdtemp, readdir, rm, utimes, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { after, before, test } from "node:test";
import { RESULT_RETENTION_MS, SYSTEM_TEMP_RESULT_FOLDER, WORKSPACE_RESULT_FOLDER, sweepOldResults } from "../../extensions/pi-revit/result-sweep.ts";

const now = Date.now();
const old = new Date(now - RESULT_RETENTION_MS - 60_000);
let sandbox: string;

/** Creates a folder with one file in it; `aged` gives it a last write older than the retention. */
async function folder(parent: string, name: string, aged: boolean): Promise<string> {
	const full = path.join(parent, name);
	await mkdir(full, { recursive: true });
	await writeFile(path.join(full, "data.json"), "{}");
	if (aged) await utimes(full, old, old);
	return full;
}

before(async () => { sandbox = await mkdtemp(path.join(os.tmpdir(), "pi-revit-sweep-test-")); });
after(async () => { await rm(sandbox, { recursive: true, force: true }); });

test("the system temp sweep leaves other programs' date-named folders alone", async () => {
	const temp = path.join(sandbox, "system-temp");
	await folder(temp, "2020-03-01-backup", true);          // someone else's folder: the 0.7.0 bug deleted it
	await folder(temp, "2026-10-01-export-backup", true);
	await folder(temp, "unrelated", true);
	await folder(temp, "pi-revit-results-old", true);       // ours, expired
	await folder(temp, "pi-revit-results-new", false);      // ours, still fresh

	const removed = await sweepOldResults(now, temp, SYSTEM_TEMP_RESULT_FOLDER);

	assert.equal(removed, 1);
	assert.deepEqual((await readdir(temp)).sort(), ["2020-03-01-backup", "2026-10-01-export-backup", "pi-revit-results-new", "unrelated"]);
});

test("the workspace sweep removes expired session folders and keeps fresh ones", async () => {
	const results = path.join(sandbox, "results");
	await folder(results, "2026-01-05-abc123", true);
	await folder(results, "2026-11-16-def456", false);
	await folder(results, "notes", true);                   // not a session folder
	await folder(results, "pi-revit-results-xyz", true);    // system-temp naming is not swept here

	const removed = await sweepOldResults(now, results, WORKSPACE_RESULT_FOLDER);

	assert.equal(removed, 1);
	assert.deepEqual((await readdir(results)).sort(), ["2026-11-16-def456", "notes", "pi-revit-results-xyz"]);
});

test("the running session's folder is never removed, even when it looks expired", async () => {
	const results = path.join(sandbox, "results-current");
	const current = await folder(results, "2026-01-01-current", true);
	await folder(results, "2026-01-01-other", true);

	const removed = await sweepOldResults(now, results, WORKSPACE_RESULT_FOLDER, current);

	assert.equal(removed, 1);
	assert.deepEqual(await readdir(results), ["2026-01-01-current"]);
});

test("files are not removed, only folders", async () => {
	const temp = path.join(sandbox, "files-only");
	await mkdir(temp, { recursive: true });
	const file = path.join(temp, "pi-revit-results-file");
	await writeFile(file, "x");
	await utimes(file, old, old);

	assert.equal(await sweepOldResults(now, temp, SYSTEM_TEMP_RESULT_FOLDER), 0);
	assert.deepEqual(await readdir(temp), ["pi-revit-results-file"]);
});

test("a missing directory is not an error", async () => {
	assert.equal(await sweepOldResults(now, path.join(sandbox, "does-not-exist"), SYSTEM_TEMP_RESULT_FOLDER), 0);
});

test("the two name patterns do not overlap", () => {
	assert.equal(SYSTEM_TEMP_RESULT_FOLDER.test("2020-03-01-backup"), false);
	assert.equal(SYSTEM_TEMP_RESULT_FOLDER.test("pi-revit-results-a1b2c3"), true);
	assert.equal(WORKSPACE_RESULT_FOLDER.test("2026-11-16-a1b2c3"), true);
	assert.equal(WORKSPACE_RESULT_FOLDER.test("pi-revit-results-a1b2c3"), false);
});
