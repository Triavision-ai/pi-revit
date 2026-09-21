import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { mkdir, readdir, readFile, writeFile, rename } from "node:fs/promises";
import { createHash, randomUUID } from "node:crypto";
import path from "node:path";
import os from "node:os";

type InputKind = "string" | "number" | "integer" | "boolean" | "object" | "array";
interface Definition { name: string; description: string; code: string; input_types: Record<string, InputKind>; }
interface Version extends Definition { version: string; created_at: string; }
interface Receipt { operation_id: string; bridge_id: string; }
type Result = { content: { type: "text"; text: string }[]; details?: unknown };
type Runner = (args: Record<string, unknown>, signal: AbortSignal | undefined, prepared: (receipt: Receipt) => Promise<void>) => Promise<Result>;
const kinds = ["string", "number", "integer", "boolean", "object", "array"] as const;
const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
const hash = (value: unknown) => createHash("sha256").update(JSON.stringify(value)).digest("hex");
const scriptName = (name: unknown) => {
    if (typeof name !== "string" || !/^[a-z][a-z0-9_-]{0,63}$/.test(name)) throw new Error("name must be 1–64 lowercase letters, digits, underscores or hyphens, starting with a letter.");
    return name;
};
const versionId = (version: unknown) => {
    if (typeof version !== "string" || !/^[a-f0-9]{64}$/.test(version)) throw new Error("version must be the exact saved 64-character hash.");
    return version;
};

export function createScriptLibrary(root: string) {
    const versions = path.join(root, "versions"), history = path.join(root, "history");
    const definition = (value: Record<string, unknown>): Definition => {
        const name = scriptName(value.name);
        if (typeof value.code !== "string" || !value.code.trim() || value.code.length > 100000) throw new Error("code must contain 1–100,000 characters.");
        if (typeof value.description !== "string" || value.description.length > 2000) throw new Error("description must be a string of at most 2,000 characters.");
        if (!object(value.input_types) || Object.keys(value.input_types).length > 40) throw new Error("input_types must declare at most 40 named inputs.");
        const input_types: Record<string, InputKind> = Object.create(null);
        for (const key of Object.keys(value.input_types).sort()) {
            if (!/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(key) || !kinds.includes(value.input_types[key] as InputKind)) throw new Error(`Invalid input declaration: ${key}.`);
            input_types[key] = value.input_types[key] as InputKind;
        }
        return { name, description: value.description, code: value.code, input_types };
    };
    async function files(directory: string) {
        try { return (await readdir(directory)).filter(file => file.endsWith(".json")); }
        catch (error) { if ((error as NodeJS.ErrnoException).code === "ENOENT") return []; throw error; }
    }
    async function read(name: unknown, version: unknown): Promise<Version> {
        const value = JSON.parse(await readFile(path.join(versions, `${scriptName(name)}--${versionId(version)}.json`), "utf8"));
        const content = definition(value);
        if (content.name !== name || hash(content) !== version || value.version !== version) throw new Error("Saved script integrity check failed; no code was executed.");
        return { ...content, version: value.version, created_at: value.created_at };
    }
    async function save(args: Record<string, unknown>) {
        const content = definition(args), version = hash(content);
        await mkdir(versions, { recursive: true });
        const file = path.join(versions, `${content.name}--${version}.json`);
        const value = { ...content, version, created_at: new Date().toISOString() };
        try { await writeFile(file, JSON.stringify(value, null, 2), { encoding: "utf8", flag: "wx" }); }
        catch (error) { if ((error as NodeJS.ErrnoException).code !== "EEXIST") throw error; }
        const saved = await read(content.name, version);
        return { name: saved.name, version, created_at: saved.created_at, file_path: file, executed: false };
    }
    function validateInputs(saved: Version, input: unknown) {
        if (!object(input)) throw new Error("inputs must be a JSON object.");
        if (JSON.stringify(input).length > 100000) throw new Error("inputs exceed 100,000 JSON characters.");
        for (const name of Object.keys(input)) if (!Object.hasOwn(saved.input_types, name)) throw new Error(`Undeclared input: ${name}.`);
        for (const [name, kind] of Object.entries(saved.input_types)) {
            const value = input[name];
            const valid = Object.hasOwn(input, name) && (kind === "array" ? Array.isArray(value) : kind === "object" ? object(value)
                : kind === "integer" ? Number.isSafeInteger(value) : kind === "number" ? typeof value === "number" && Number.isFinite(value) : typeof value === kind);
            if (!valid) throw new Error(`Input '${name}' is required and must be ${kind}.`);
        }
        return input;
    }
    async function list(name: unknown, offset: number, limit: number) {
        const filter = name === undefined ? undefined : scriptName(name);
        const names = (await files(versions)).filter(file => /^[a-z][a-z0-9_-]{0,63}--[a-f0-9]{64}\.json$/.test(file) && (!filter || file.slice(0, file.lastIndexOf("--")) === filter)).sort();
        const entries = [];
        for (const file of names.slice(offset, offset + limit)) {
            const split = file.lastIndexOf("--");
            const value = await read(file.slice(0, split), file.slice(split + 2, -5));
            entries.push({ name: value.name, version: value.version, description: value.description, input_types: value.input_types, created_at: value.created_at });
        }
        return { total_count: names.length, offset, entries, next_offset: offset + entries.length < names.length ? offset + entries.length : null };
    }
    async function writeHistory(record: Record<string, unknown>) {
        await mkdir(history, { recursive: true });
        const file = path.join(history, `${record.run_id}.json`), temp = `${file}.${randomUUID()}.tmp`;
        await writeFile(temp, JSON.stringify(record, null, 2), { encoding: "utf8", flag: "wx" });
        await rename(temp, file);
    }
    async function readHistory(name: unknown, offset: number, limit: number) {
        const filter = name === undefined ? undefined : scriptName(name);
        const entries = [];
        for (const file of await files(history)) {
            if (!/^[a-f0-9-]{36}\.json$/.test(file)) continue;
            const value = JSON.parse(await readFile(path.join(history, file), "utf8"));
            if (!filter || value.name === filter) entries.push(value);
        }
        entries.sort((a, b) => b.started_at.localeCompare(a.started_at) || a.run_id.localeCompare(b.run_id));
        return { total_count: entries.length, offset, entries: entries.slice(offset, offset + limit), next_offset: offset + limit < entries.length ? offset + limit : null };
    }
    return { root, read, save, list, validateInputs, writeHistory, readHistory };
}

export function registerScriptLibrary(pi: ExtensionAPI, run: Runner, render: (value: unknown) => Promise<Result>) {
    const library = createScriptLibrary(path.join(process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"), "pi-revit", "scripts"));
    pi.registerTool({
        name: "manage_revit_scripts", label: "Manage Revit Scripts",
        description: "Save, list, read, run and inspect local reusable C# scripts. Saving never executes code. Versions are immutable content hashes; read a version before explicitly running that exact hash. Every declared input is required; extra inputs are rejected. input_types validates top-level JSON kinds only; scripts validate nested contents and domain rules. Runs use execute_csharp with separate inputs JsonElement, exact expected_document_id and operation receipts. Scripts have the same unrestricted model/UI/file/external effects and transaction behavior as execute_csharp. Local history records version, document, input hash and receipt, not input values or results. After interruption, inspect get_revit_operation before retrying with the same _operation_id and identical inputs. No automatic runs or model saving.",
        parameters: Type.Object({
            action: Type.Union(["list", "save", "read", "run", "history"].map(value => Type.Literal(value))),
            name: Type.Optional(Type.String()), version: Type.Optional(Type.String()), description: Type.Optional(Type.String({ maxLength: 2000 })),
            code: Type.Optional(Type.String({ minLength: 1, maxLength: 100000 })),
            input_types: Type.Optional(Type.Record(Type.String(), Type.Union(kinds.map(value => Type.Literal(value))))),
            inputs: Type.Optional(Type.Record(Type.String(), Type.Unknown())), expected_document_id: Type.Optional(Type.String()), _operation_id: Type.Optional(Type.String()),
            offset: Type.Optional(Type.Integer({ minimum: 0 })), limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 100 })),
        }),
        executionMode: "sequential",
        async execute(_id, args, signal) {
            const offset = Math.max(0, args.offset ?? 0), limit = Math.max(1, Math.min(100, args.limit ?? 20));
            if (args.action === "save") return render(await library.save(args));
            if (args.action === "list") return render(await library.list(args.name, offset, limit));
            if (args.action === "history") return render(await library.readHistory(args.name, offset, limit));
            if (args.action !== "read" && args.action !== "run") throw new Error("Unknown script library action.");
            const saved = await library.read(args.name, args.version);
            if (args.action === "read") return render(saved);
            if (typeof args.expected_document_id !== "string" || !args.expected_document_id) throw new Error("Run requires expected_document_id from get_model_overview.");
            const inputs = library.validateInputs(saved, args.inputs ?? {});
            const record: Record<string, unknown> = { run_id: randomUUID(), name: saved.name, version: saved.version, started_at: new Date().toISOString(), expected_document_id: args.expected_document_id, inputs_hash: hash(inputs), state: "prepared" };
            let dispatched = false;
            try {
                const result = await run({ code: saved.code, inputs, expected_document_id: args.expected_document_id, ...(args._operation_id !== undefined ? { _operation_id: args._operation_id } : {}) }, signal, async receipt => {
                    Object.assign(record, receipt); await library.writeHistory(record); dispatched = true;
                });
                Object.assign(record, { state: "response_received", completed_at: new Date().toISOString() });
                try { await library.writeHistory(record); }
                catch { result.content.push({ type: "text", text: "The script response was received, but the final local history update failed. Use the operation receipt for its outcome." }); }
                return result;
            } catch (error) {
                if (dispatched) {
                    Object.assign(record, { state: "outcome_unconfirmed", completed_at: new Date().toISOString() });
                    try { await library.writeHistory(record); } catch { /* Preserve the original error and operation ID. */ }
                }
                throw error;
            }
        },
    });
}
