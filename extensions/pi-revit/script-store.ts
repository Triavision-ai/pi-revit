import { mkdir, readdir, readFile, writeFile, rename, stat, unlink } from "node:fs/promises";
import { createHash, randomUUID } from "node:crypto";
import path from "node:path";

/**
 * Local storage of the script library: immutable script versions and one run record per run.
 * No Pi or bridge dependency, so it can be exercised directly.
 */
export type InputKind = "string" | "number" | "integer" | "boolean" | "object" | "array";
export interface Definition { name: string; description: string; code: string; input_types: Record<string, InputKind>; }
export interface Version extends Definition { version: string; created_at: string; }
export const kinds = ["string", "number", "integer", "boolean", "object", "array"] as const;
export const object = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
export const hash = (value: unknown) => createHash("sha256").update(JSON.stringify(value)).digest("hex");
const scriptName = (name: unknown) => {
    if (typeof name !== "string" || !/^[a-z][a-z0-9_-]{0,63}$/.test(name)) throw new Error("name must be 1–64 lowercase letters, digits, underscores or hyphens, starting with a letter.");
    return name;
};
const versionId = (version: unknown) => {
    if (typeof version !== "string" || !/^[a-f0-9]{64}$/.test(version)) throw new Error("version must be the exact saved 64-character hash.");
    return version;
};

/** Run records kept on disk. Older ones are removed once the folder exceeds this by 10%. */
export const DEFAULT_HISTORY_LIMIT = 1000;
const HISTORY_FILE = /^[a-f0-9-]{36}\.json$/;
const text = (value: unknown) => typeof value === "string" ? value : "";
const compare = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;

export function createScriptLibrary(root: string, options: { historyLimit?: number } = {}) {
    const versions = path.join(root, "versions"), history = path.join(root, "history");
    const historyLimit = Math.max(1, options.historyLimit ?? DEFAULT_HISTORY_LIMIT);
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
            // One damaged or hand-edited version must not hide every other script: list it
            // as invalid. read() stays strict, so an invalid version can never run.
            try {
                const value = await read(file.slice(0, split), file.slice(split + 2, -5));
                entries.push({ name: value.name, version: value.version, description: value.description, input_types: value.input_types, created_at: value.created_at });
            } catch (error) {
                entries.push({ name: file.slice(0, split), version: file.slice(split + 2, -5), invalid: true, error: error instanceof Error ? error.message : String(error) });
            }
        }
        return { total_count: names.length, offset, entries, next_offset: offset + entries.length < names.length ? offset + entries.length : null };
    }
    /** Keeps the newest historyLimit run records (by last write). It only runs once the folder
     * has grown 10% past the limit, so an ordinary write lists the folder and nothing more. */
    async function pruneHistory() {
        const names = (await files(history)).filter(file => HISTORY_FILE.test(file));
        if (names.length <= historyLimit + Math.ceil(historyLimit / 10)) return;
        const dated = await Promise.all(names.map(async file => {
            try { return { file, written: (await stat(path.join(history, file))).mtimeMs }; }
            catch { return { file, written: Number.POSITIVE_INFINITY }; } // being replaced right now: keep
        }));
        dated.sort((a, b) => b.written - a.written || compare(a.file, b.file));
        for (const { file } of dated.slice(historyLimit)) await unlink(path.join(history, file)).catch(() => undefined);
    }
    async function writeHistory(record: Record<string, unknown>) {
        await mkdir(history, { recursive: true });
        const file = path.join(history, `${record.run_id}.json`), temp = `${file}.${randomUUID()}.tmp`;
        await writeFile(temp, JSON.stringify(record, null, 2), { encoding: "utf8", flag: "wx" });
        await rename(temp, file);
        // Housekeeping must never fail the run whose record was just written.
        await pruneHistory().catch(() => undefined);
    }
    async function readHistory(name: unknown, offset: number, limit: number) {
        const filter = name === undefined ? undefined : scriptName(name);
        const entries: Record<string, unknown>[] = [];
        let unreadable = 0;
        for (const file of await files(history)) {
            if (!HISTORY_FILE.test(file)) continue;
            // One damaged or hand-edited record must not hide every other run: count it and go on.
            let value: unknown;
            try { value = JSON.parse(await readFile(path.join(history, file), "utf8")); }
            catch { unreadable++; continue; }
            if (!object(value)) { unreadable++; continue; }
            if (!filter || value.name === filter) entries.push(value);
        }
        // Newest first. A record without a readable started_at sorts last instead of throwing.
        entries.sort((a, b) => compare(text(b.started_at), text(a.started_at)) || compare(text(a.run_id), text(b.run_id)));
        return {
            total_count: entries.length, offset, entries: entries.slice(offset, offset + limit),
            next_offset: offset + limit < entries.length ? offset + limit : null,
            ...(unreadable > 0 ? { unreadable_records: unreadable } : {}),
        };
    }
    return { root, read, save, list, validateInputs, writeHistory, readHistory };
}
