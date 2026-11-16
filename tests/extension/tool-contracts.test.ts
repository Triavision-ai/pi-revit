import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { readdir, readFile } from "node:fs/promises";
import { test } from "node:test";

const root = new URL("../../", import.meta.url);
const read = (file: string) => readFile(new URL(file, root), "utf8");
interface PackagedTool {
	name: string; source: string; write: boolean; effects: string[]; requires_document: boolean;
	document_kinds: string[] | null; contract_hash: string; parameters: unknown;
}
const snapshot = async () => (JSON.parse(await read("skills/pi-revit/contracts.generated.json")).tools as PackagedTool[]);

// The executable-contract hash, as defined by contractHash in extensions/pi-revit/contracts.ts.
// That module cannot be imported here (it needs Pi's module loader), so the formula is repeated:
// when it changes there, this test fails until the snapshot and this copy follow.
function canonicalJson(value: unknown): string {
	if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
	if (value !== null && typeof value === "object") {
		const object = value as Record<string, unknown>;
		return `{${Object.keys(object).sort().filter(key => object[key] !== undefined).map(key => `${JSON.stringify(key)}:${canonicalJson(object[key])}`).join(",")}}`;
	}
	return JSON.stringify(value ?? null);
}
function withoutGuidance(schema: unknown, isPropertyMap = false): unknown {
	if (Array.isArray(schema)) return schema.map(item => withoutGuidance(item));
	if (schema === null || typeof schema !== "object") return schema;
	const result: Record<string, unknown> = {};
	for (const [key, value] of Object.entries(schema as Record<string, unknown>)) {
		if (!isPropertyMap && (key === "description" || key === "title" || key === "examples")) continue;
		result[key] = withoutGuidance(value, !isPropertyMap && (key === "properties" || key === "patternProperties" || key === "$defs" || key === "definitions"));
	}
	return result;
}
function contractHash(tool: PackagedTool): string {
	const write = tool.write === true;
	const kinds = [...(tool.document_kinds ?? [])].sort();
	const restricted = kinds.length > 0 && kinds.join(",") !== "family,project";
	const payload = canonicalJson({
		parameters: withoutGuidance(tool.parameters ?? { type: "object", properties: {} }),
		write,
		effects: tool.effects ?? (write ? ["model"] : []),
		requiresDocument: tool.requires_document ?? true,
		...(restricted ? { documentKinds: kinds } : {}),
	});
	return createHash("sha256").update(payload).digest("hex").slice(0, 16);
}

/** Every schema object inside a schema, with the path that leads to it. */
function* schemas(value: unknown, at = "parameters"): Generator<[string, Record<string, unknown>]> {
	if (Array.isArray(value)) { for (const [index, item] of value.entries()) yield* schemas(item, `${at}[${index}]`); return; }
	if (value === null || typeof value !== "object") return;
	yield [at, value as Record<string, unknown>];
	for (const [key, child] of Object.entries(value)) yield* schemas(child, `${at}.${key}`);
}

test("every packaged contract hash matches its packaged schema", async () => {
	const stale = (await snapshot()).filter(tool => contractHash(tool) !== tool.contract_hash).map(tool => tool.name);

	assert.deepEqual(stale, [], "the snapshot was edited without updating contract_hash");
});

test("no tool describes a string choice as a union of constants", async () => {
	// Some model providers reject anyOf/const; a plain { type: "string", enum: [...] } works everywhere.
	const unions: string[] = [];
	for (const tool of await snapshot())
		for (const [at, schema] of schemas(tool.parameters)) {
			const options = schema.anyOf ?? schema.oneOf;
			if (Array.isArray(options) && options.length > 0 && options.every(option => option && typeof option === "object" && "const" in option))
				unions.push(`${tool.name} ${at}`);
		}

	assert.deepEqual(unions, []);
});

test("no packaged schema carries an internal TypeBox marker", async () => {
	// Type.Optional applied to a plain schema object leaves "~optional" in the JSON the provider receives.
	const marked: string[] = [];
	for (const tool of await snapshot())
		for (const [at, schema] of schemas(tool.parameters))
			for (const key of Object.keys(schema)) if (key.startsWith("~")) marked.push(`${tool.name} ${at}.${key}`);

	assert.deepEqual(marked, []);
});

test("string choices of Pi-native tools are declared through string-choice.ts only", async () => {
	const folder = new URL("extensions/pi-revit/", root);
	const offenders: string[] = [];
	for (const file of (await readdir(folder)).filter(name => name.endsWith(".ts"))) {
		const source = await readFile(new URL(file, folder), "utf8");
		if (/Type\.Literal\(/.test(source)) offenders.push(`${file}: Type.Literal`);
		if (file !== "string-choice.ts" && /StringEnum\(/.test(source)) offenders.push(`${file}: StringEnum used directly`);
	}

	assert.deepEqual(offenders, []);
});

test("every tool manual prints the contract hash of the packaged snapshot", async () => {
	const wrong: string[] = [];
	let checked = 0;
	for (const tool of await snapshot()) {
		const manual = new URL(`skills/pi-revit/references/tools/${tool.name}.md`, root);
		if (!existsSync(manual)) continue;
		const printed = /\*\*Contract hash:\*\* `([0-9a-f]{16})`/.exec(await readFile(manual, "utf8"))?.[1];
		if (printed === undefined) continue;
		checked++;
		if (printed !== tool.contract_hash) wrong.push(`${tool.name}: manual ${printed}, snapshot ${tool.contract_hash}`);
	}

	assert.deepEqual(wrong, []);
	assert.ok(checked >= 30, `only ${checked} manuals print a contract hash`);
});
