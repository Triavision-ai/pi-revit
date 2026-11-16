import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";

const root = new URL("../../", import.meta.url);
const read = (file: string) => readFile(new URL(file, root), "utf8");

// Pi shows a package's CHANGELOG.md after an update ("Extension Updates"). pi-revit relies on
// that instead of announcing updates itself, which showed every update twice.

test("the published version has a changelog entry in the format Pi reads", async () => {
	const { version } = JSON.parse(await read("package.json"));
	const changelog = await read("CHANGELOG.md");
	const headings = [...changelog.matchAll(/^##\s+\[?(\d+\.\d+\.\d+)\]?/gm)].map(match => match[1]);

	assert.ok(headings.includes(version), `CHANGELOG.md has no "## [${version}]" entry`);
	assert.equal(headings[0], version, "the newest changelog entry must be the package version");
});

test("the changelog ships in the package and Pi's own display is not switched off", async () => {
	const manifest = JSON.parse(await read("package.json"));

	assert.ok(manifest.files.includes("CHANGELOG.md"));
	assert.notEqual(manifest.pi?.changelogPath, "");
});

test("the extension does not announce updates a second time", async () => {
	const source = await read("extensions/pi-revit/index.ts");

	assert.equal(/What's new in pi-revit/.test(source), false);
	assert.equal(/lastAnnouncedVersion/.test(source), false);
});
