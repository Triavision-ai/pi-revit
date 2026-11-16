import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdir, mkdtemp, readdir, rm, utimes, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { after, before, test } from "node:test";
import { createScriptLibrary } from "../../extensions/pi-revit/script-store.ts";

let sandbox: string;
before(async () => { sandbox = await mkdtemp(path.join(os.tmpdir(), "pi-revit-history-test-")); });
after(async () => { await rm(sandbox, { recursive: true, force: true }); });

const record = (name: string, started_at: string) => ({ run_id: randomUUID(), name, version: "0".repeat(64), started_at, state: "response_received" });

test("history lists runs newest first", async () => {
	const library = createScriptLibrary(path.join(sandbox, "ordered"));
	await library.writeHistory(record("alpha", "2026-11-01T10:00:00.000Z"));
	await library.writeHistory(record("beta", "2026-11-03T10:00:00.000Z"));
	await library.writeHistory(record("alpha", "2026-11-02T10:00:00.000Z"));

	const all = await library.readHistory(undefined, 0, 20);
	assert.deepEqual(all.entries.map(entry => entry.started_at), ["2026-11-03T10:00:00.000Z", "2026-11-02T10:00:00.000Z", "2026-11-01T10:00:00.000Z"]);
	assert.equal(all.total_count, 3);
	assert.equal("unreadable_records" in all, false);

	const alpha = await library.readHistory("alpha", 0, 20);
	assert.equal(alpha.total_count, 2);
});

test("one damaged record does not hide the others", async () => {
	const root = path.join(sandbox, "damaged");
	const library = createScriptLibrary(root);
	await library.writeHistory(record("alpha", "2026-11-01T10:00:00.000Z"));
	await library.writeHistory(record("alpha", "2026-11-02T10:00:00.000Z"));
	// A record cut off mid-write, and one that is valid JSON but not a record at all.
	await writeFile(path.join(root, "history", `${randomUUID()}.json`), '{"run_id": "abc", "name": "al');
	await writeFile(path.join(root, "history", `${randomUUID()}.json`), "[1, 2, 3]");

	const result = await library.readHistory(undefined, 0, 20);

	assert.equal(result.total_count, 2);
	assert.equal(result.unreadable_records, 2);
});

test("a record without started_at sorts last instead of failing", async () => {
	const root = path.join(sandbox, "no-start");
	const library = createScriptLibrary(root);
	await library.writeHistory(record("alpha", "2026-11-02T10:00:00.000Z"));
	await mkdir(path.join(root, "history"), { recursive: true });
	await writeFile(path.join(root, "history", `${randomUUID()}.json`), JSON.stringify({ run_id: randomUUID(), name: "alpha", started_at: 42 }));

	const result = await library.readHistory(undefined, 0, 20);

	assert.equal(result.total_count, 2);
	assert.equal(result.entries[0].started_at, "2026-11-02T10:00:00.000Z");
	assert.equal(result.entries[1].started_at, 42);
});

test("paging reports the next offset", async () => {
	const library = createScriptLibrary(path.join(sandbox, "paged"));
	for (let day = 1; day <= 5; day++) await library.writeHistory(record("alpha", `2026-11-0${day}T10:00:00.000Z`));

	const first = await library.readHistory(undefined, 0, 2);
	assert.equal(first.entries.length, 2);
	assert.equal(first.next_offset, 2);
	const last = await library.readHistory(undefined, 4, 2);
	assert.equal(last.entries.length, 1);
	assert.equal(last.next_offset, null);
});

test("old run records are pruned once the folder outgrows the limit", async () => {
	const root = path.join(sandbox, "pruned");
	const library = createScriptLibrary(root, { historyLimit: 10 });
	const historyFolder = path.join(root, "history");
	// 11 records is still inside the 10% slack: nothing is removed yet.
	for (let index = 0; index < 11; index++) {
		const entry = record("alpha", `2026-10-${String(index + 1).padStart(2, "0")}T10:00:00.000Z`);
		await library.writeHistory(entry);
		const written = new Date(Date.UTC(2026, 9, index + 1));
		await utimes(path.join(historyFolder, `${entry.run_id}.json`), written, written);
	}
	assert.equal((await readdir(historyFolder)).length, 11);

	// The 12th write crosses the slack and prunes back to the newest 10.
	const newest = record("alpha", "2026-11-16T10:00:00.000Z");
	await library.writeHistory(newest);

	const result = await library.readHistory(undefined, 0, 100);
	assert.equal(result.total_count, 10);
	assert.equal(result.entries[0].run_id, newest.run_id);
	assert.equal(result.entries.some(entry => entry.started_at === "2026-10-01T10:00:00.000Z"), false);
	assert.equal(result.entries.some(entry => entry.started_at === "2026-10-02T10:00:00.000Z"), false);
});

test("an empty library has an empty history", async () => {
	const library = createScriptLibrary(path.join(sandbox, "empty"));
	assert.deepEqual(await library.readHistory(undefined, 0, 20), { total_count: 0, offset: 0, entries: [], next_offset: null });
});
