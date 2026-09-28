import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type, type TSchema } from "typebox";
import { readFileSync } from "node:fs";
import { mkdir, mkdtemp, readFile, writeFile } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { version as packageVersion } from "../../package.json";
import { createToolCatalog, type BridgeToolDescriptor } from "./tool-catalog.js";
import { createInstanceRouter, type BridgeInfo } from "./instance-router.js";
import { registerScriptLibrary } from "./script-library.js";
import { documentationRevision, documentedTools, manualDirectory, skillRoot } from "./tool-documentation.js";
import { publicBridgeSchema } from "./tool-schema.js";
import { packagedContracts } from "./contracts.js";
import { buildPlatformSection, hoistSharedGuidelines } from "./platform-prompt.js";
import { createCompletionMonitor } from "./completion-monitor.js";
import { changedModel, createScopeMonitor } from "./scope-monitor.js";

type BridgeResolver = (operationId?: string) => Promise<BridgeInfo>;

interface ContentBlock {
	type: "text";
	text: string;
}

interface BridgeToolResponse {
	success?: boolean;
	toolName?: string;
	content?: ContentBlock[];
	details?: unknown;
	isError?: boolean;
	error?: boolean;
	message?: string;
	hasActiveDocument?: boolean;
}

const DEFAULT_TIMEOUT_MS = 30_000;
const LONG_TIMEOUT_MS = 120_000;
const DISCOVERY_TIMEOUT_MS = 10_000;
const MAX_MODEL_CONTENT_CHARS = 12_000;
const MAX_RESULT_PAGE_CHARS = 8_000;

// IDs only resolve results created by this extension instance. A caller cannot
// turn read_revit_result into an arbitrary filesystem read by supplying a path.
const savedResults = new Map<string, string>();
let resultDirectory: Promise<string> | undefined;

/** Tools with a longer budget; everything else gets DEFAULT_TIMEOUT_MS. The same
 * value is sent to the bridge as timeout_ms and used client-side via AbortSignal. */
const TOOL_TIMEOUTS_MS: Record<string, number> = {
	execute_csharp: LONG_TIMEOUT_MS,
	capture_view: LONG_TIMEOUT_MS,
	export_documents: LONG_TIMEOUT_MS,
};

function toolTimeoutMs(name: string): number {
	return TOOL_TIMEOUTS_MS[name] ?? DEFAULT_TIMEOUT_MS;
}

function bridgeInfoPath(): string {
	const appData = process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming");
	return path.join(appData, "RevitBridge", "bridge.json");
}

export async function readBridgeInfo(): Promise<BridgeInfo> {
	const infoPath = bridgeInfoPath();
	let raw: string;
	try {
		raw = await readFile(infoPath, "utf8");
	} catch {
		throw new Error(
			`Revit bridge is not available. Start Revit with the bridge add-in loaded, then retry. Expected bridge info file: ${infoPath}`,
		);
	}

	let info: BridgeInfo;
	try {
		info = JSON.parse(raw) as BridgeInfo;
	} catch {
		throw new Error(`Revit bridge info file is invalid JSON: ${infoPath}`);
	}
	if (!info.baseUrl || !info.token) {
		throw new Error(`Revit bridge info file is invalid: ${infoPath}`);
	}
	return info;
}

/** Cancelling or timing out only abandons the HTTP request: the bridge has no
 * cancellation path, so a queued or already-running work item may still execute
 * in Revit (queued items expire only at the bridge-side timeout_ms deadline). */
function cancelledError(): Error {
	return new Error(
		"Revit bridge call was cancelled client-side; Revit may still run the queued or in-flight tool to completion. Verify model state (e.g. with get_elements/get_element_details) before retrying a write tool.",
	);
}

function timeoutError(timeoutMs: number): Error {
	return new Error(
		`Revit did not answer within ${Math.ceil(timeoutMs / 1000)}s. Revit may be busy or showing a dialog; the request was abandoned client-side, but an already-started tool still runs to completion in Revit. Verify model state before retrying a write tool.`,
	);
}

export async function bridgeRequest(
	pathname: string,
	init: { method: "GET" | "POST"; body?: string; query?: Record<string, string>; bridge?: BridgeInfo },
	signal?: AbortSignal,
	timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<unknown> {
	const info = init.bridge ?? await readBridgeInfo();
	const query = new URLSearchParams({ ...(init.query ?? {}), token: info.token });
	const url = `${info.baseUrl}${pathname}?${query.toString()}`;

	const timeoutSignal = AbortSignal.timeout(timeoutMs);
	const signals = [timeoutSignal];
	if (signal) signals.push(signal);

	let response: Response;
	try {
		response = await fetch(url, {
			method: init.method,
			body: init.body,
			headers: init.body ? { "content-type": "application/json; charset=utf-8" } : undefined,
			signal: AbortSignal.any(signals),
		});
	} catch (error) {
		if (signal?.aborted) throw cancelledError();
		if (timeoutSignal.aborted) throw timeoutError(timeoutMs);
		const reason = error instanceof Error ? error.message : String(error);
		throw new Error(
			`Could not reach the Revit bridge at ${info.baseUrl} (${reason}). Revit may have been closed; start Revit and retry.`,
		);
	}

	let payload: unknown;
	try {
		payload = await response.json();
	} catch {
		// The abort signal also cancels body consumption: a timeout/cancel that
		// fires after headers arrive must not be misreported as a bad response.
		if (signal?.aborted) throw cancelledError();
		if (timeoutSignal.aborted) throw timeoutError(timeoutMs);
		throw new Error(`Revit bridge returned a non-JSON response (HTTP ${response.status}).`);
	}

	const body = payload as BridgeToolResponse;
	if (!response.ok || body.error || body.isError || body.success === false) {
		throw new Error(body.message ?? body.content?.[0]?.text ?? `Revit bridge request failed (HTTP ${response.status}).`);
	}
	return payload;
}

export function capText(text: string): string {
	if (text.length <= MAX_MODEL_CONTENT_CHARS) return text;
	const suffix = `... [truncated preview at ${MAX_MODEL_CONTENT_CHARS} chars]`;
	return text.slice(0, Math.max(0, MAX_MODEL_CONTENT_CHARS - suffix.length)) + suffix;
}

async function modelContent(name: string, payload: BridgeToolResponse): Promise<{ type: "text"; text: string }[]> {
	const details = payload.details;
	const value = details !== null && typeof details === "object" && Object.hasOwn(details, "payload")
		? (details as { payload: unknown }).payload
		: details;
	// Current and older bridges both carry the full value in details.payload.
	// Pi sends content to the model; details alone is only available to its UI.
	const text = details !== undefined
		? JSON.stringify(value, null, 2) ?? "null"
		: payload.content?.map((block) => block.text).join("\n") ?? "{}";
	if (text.length <= MAX_MODEL_CONTENT_CHARS) return [{ type: "text", text }];

	const resultId = randomUUID();
	let filePath: string;
	try {
		const directory = await (resultDirectory ??= mkdtemp(path.join(os.tmpdir(), "pi-revit-results-")).catch((error) => {
			// A transient failure must not poison every later large result in this session.
			resultDirectory = undefined;
			throw error;
		}));
		filePath = path.join(directory, `${resultId}.json`);
		await writeFile(filePath, text, { encoding: "utf8", flag: "wx", mode: 0o600 });
	} catch (error) {
		const reason = error instanceof Error ? error.message : String(error);
		throw new Error(`Revit completed '${name}', but its large result could not be saved locally: ${reason}. Verify model state before retrying a write.`);
	}
	savedResults.set(resultId, filePath);
	return [{ type: "text", text: JSON.stringify({
		result_id: resultId,
		file_path: filePath,
		total_chars: text.length,
		complete_inline: false,
		retrieval: { tool: "read_revit_result", result_id: resultId, offset: 0, limit: MAX_RESULT_PAGE_CHARS },
		instructions: "The complete result is saved locally. Call read_revit_result, then follow next_offset until has_more is false. Each page is a fragment of the saved text, not a standalone result. Offsets count UTF-16 code units. The absolute file can also be opened with read; it remains available after an extension reload, when this session's result ID may no longer resolve.",
	}) }];
}

function registerResultReader(pi: ExtensionAPI) {
	pi.registerTool({
		name: "read_revit_result",
		label: "Read Saved Revit Result",
		description: "Read a bounded fragment of a large Revit tool result using its opaque result_id. This reads a saved local result and does not contact Revit. Follow next_offset until has_more is false; text fragments concatenate to the complete saved result. Offsets count UTF-16 code units.",
		parameters: Type.Object({
			result_id: Type.String({ description: "Opaque result_id returned by a Revit tool in this extension session." }),
			offset: Type.Optional(Type.Integer({ minimum: 0, description: "Character offset from the previous page's next_offset; default 0." })),
			limit: Type.Optional(Type.Integer({ minimum: 1, maximum: MAX_RESULT_PAGE_CHARS, description: "Maximum characters to return; default 8000. Escaping may require a smaller fragment." })),
		}),
		executionMode: "sequential",
		async execute(_toolCallId, params) {
			const offset = params.offset ?? 0;
			const limit = params.limit ?? MAX_RESULT_PAGE_CHARS;
			if (!Number.isSafeInteger(offset) || offset < 0) throw new Error("offset must be a non-negative integer.");
			if (!Number.isSafeInteger(limit) || limit < 1 || limit > MAX_RESULT_PAGE_CHARS)
				throw new Error(`limit must be an integer from 1 to ${MAX_RESULT_PAGE_CHARS}.`);
			const filePath = savedResults.get(params.result_id);
			if (!filePath) throw new Error("Unknown result_id for this extension session. Use the original result's file_path with read if the extension was reloaded.");
			const text = await readFile(filePath, "utf8");
			if (offset > text.length) throw new Error(`offset exceeds this result's ${text.length} characters.`);
			const encode = (count: number) => JSON.stringify({
				result_id: params.result_id, offset, returned_chars: count, total_chars: text.length,
				has_more: offset + count < text.length,
				next_offset: offset + count < text.length ? offset + count : null,
				fragment: true, text: text.slice(offset, offset + count),
			});
			// Bound the actual model message, including JSON escaping and metadata.
			let low = 0;
			let high = Math.min(limit, text.length - offset);
			while (low < high) {
				const count = Math.ceil((low + high) / 2);
				if (encode(count).length <= MAX_MODEL_CONTENT_CHARS) low = count;
				else high = count - 1;
			}
			return { content: [{ type: "text", text: encode(low) }], details: { filePath } };
		},
	});
}

async function runBridgeTool(name: string, args: unknown, signal: AbortSignal | undefined, timeoutMs: number, resolve: BridgeResolver,
	prepared?: (receipt: { operation_id: string; bridge_id: string }) => Promise<void>) {
	const body = { ...(args as Record<string, unknown> ?? {}) };
	const retryId = body._operation_id;
	delete body._operation_id;
	if (retryId !== undefined && (typeof retryId !== "string" || !retryId)) throw new Error("_operation_id must be the exact ID of a previous request.");
	const info = await resolve(retryId as string | undefined);
	if (retryId && (!info.supportsOperationTracking || !info.bridgeId)) throw new Error("This bridge does not support operation receipts; the request was not sent.");
	const operationId = info.supportsOperationTracking && info.bridgeId ? (retryId as string | undefined) ?? `${info.bridgeId}:${randomUUID()}` : undefined;
	if (operationId && !operationId.startsWith(`${info.bridgeId}:`)) throw new Error("This operation belongs to a different bridge session. Its outcome is unknown here; it was not replayed.");
	if (prepared && (!operationId || !info.bridgeId)) throw new Error("Script library runs require a bridge with operation receipts. The script was not sent.");
	try {
		if (prepared) await prepared({ operation_id: operationId!, bridge_id: info.bridgeId! });
		const payload = (await bridgeRequest(
			`/tools/${encodeURIComponent(name)}/execute`,
			{ method: "POST", body: JSON.stringify(body), bridge: info,
				query: { timeout_ms: String(timeoutMs), ...(operationId ? { operation_id: operationId } : {}) } },
			signal, timeoutMs,
		)) as BridgeToolResponse;
		const content = await modelContent(name, payload);
		if (operationId) content.push({ type: "text", text: `Operation ID: ${operationId}. Check get_revit_operation after a timeout; retrying with this exact _operation_id and identical arguments will not repeat the action.` });
		return { content, details: operationId ? { ...(payload.details as object ?? {}), operation_id: operationId, bridge_id: info.bridgeId } : payload.details };
	} catch (error) {
		if (!operationId) throw error;
		throw new Error(`${error instanceof Error ? error.message : String(error)}\nOperation ID: ${operationId}. Use get_revit_operation to inspect its outcome. Do not retry an edit with a new ID until its effects are known.`);
	}
}

function registerOperationReader(pi: ExtensionAPI, resolve: BridgeResolver) {
	pi.registerTool({
		name: "get_revit_operation",
		label: "Get Revit Operation",
		description: "Read an operation receipt without waiting for Revit's model thread. Reports queued, running, succeeded, failed, expired_before_start, result_unavailable or unknown, with the original result when retained. Unknown after restart is not proof that the edit never ran. Full results are bounded to the latest 128 receipts / 32 MiB; IDs remain reserved for up to 10,000 operations per bridge session so expired results never cause re-execution.",
		promptSnippet: "Check the outcome of a timed-out Revit operation before retrying an edit.",
		parameters: Type.Object({ operation_id: Type.String({ minLength: 1, maxLength: 120 }) }),
		executionMode: "sequential",
		async execute(_id, args, signal) {
			const bridge = await resolve(args.operation_id);
			const result = await bridgeRequest(`/operations/${encodeURIComponent(args.operation_id)}`, { method: "GET", bridge }, signal, 10_000);
			return { content: await modelContent("get_revit_operation", { details: { payload: result } }), details: result };
		},
	});
}

interface RequestMonitors { completion: ReturnType<typeof createCompletionMonitor>; scope: ReturnType<typeof createScopeMonitor> }

/** The bridge value of a tool result (details.payload for current and older bridges). */
function resultPayload(details: unknown): unknown {
	return details !== null && typeof details === "object" && Object.hasOwn(details, "payload") ? (details as { payload: unknown }).payload : details;
}

/** Notes the request monitors add to a result: scope first (it can require asking the user), then completion. */
function monitorNotes(monitors: RequestMonitors | undefined, tool: string, params: unknown, meta: { write?: boolean; effects?: string[] | null }, details: unknown): string[] {
	if (!monitors) return [];
	const payload = resultPayload(details);
	return [monitors.scope.afterResult(payload), monitors.completion.afterCall(tool, params, meta, changedModel(payload))].filter((note): note is string => !!note);
}

/** `guidelines` are this tool's own rules; rules shared by several tools live once in the platform section. */
function registerBridgeTool(pi: ExtensionAPI, descriptor: BridgeToolDescriptor, resolve: BridgeResolver, guidelines: string[],
	monitors?: RequestMonitors) {
	const timeoutMs = toolTimeoutMs(descriptor.name);
	const schema = publicBridgeSchema(descriptor.parameters);
	pi.registerTool({
		name: descriptor.name,
		label: descriptor.label ?? descriptor.name,
		description: descriptor.description ?? `Revit bridge tool '${descriptor.name}'.`,
		parameters: Type.Unsafe(schema as TSchema),
		promptSnippet: descriptor.promptSnippet ?? undefined,
		promptGuidelines: guidelines,
		executionMode: descriptor.executionMode === "parallel" ? "parallel" : "sequential",
		async execute(_toolCallId, params, signal) {
			const result = await runBridgeTool(descriptor.name, params, signal, timeoutMs, resolve);
			// Result- and metadata-driven steering: objects that predate the request, and repeated re-verification after edits.
			const notes = monitorNotes(monitors, descriptor.name, params, descriptor, result.details);
			return notes.length ? { ...result, content: [...result.content, ...notes.map(text => ({ type: "text" as const, text }))] } : result;
		},
	});
}

/** `pi update --extensions` refreshes this package but not the deployed Revit add-in,
 * so a newer extension can silently talk to an older bridge. The add-in reports the
 * package version it was built from (stamped by scripts/build.ps1); any difference
 * means the update is incomplete. */
function versionMismatch(addinVersion: unknown): string | null {
	const addin = typeof addinVersion === "string" && addinVersion.length > 0 ? addinVersion : null;
	if (addin === packageVersion) return null;
	const state = addin ? `still runs version ${addin}` : "predates version reporting";
	return `pi-revit ${packageVersion} is installed, but the Revit add-in ${state} — the update is incomplete. Close Revit and run: npx.cmd -y pi-revit (or ask the agent to run scripts\\deploy.ps1 from the installed package, then restart Revit).`;
}

// ------------------------------------------------------------- what's new

/** Stable per-user state file. Deliberately NOT under node_modules: npm wipes
 * the package folder on every update, which is exactly when the last-announced
 * version must survive. */
function announcerStatePath(): string {
	const appData = process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming");
	return path.join(appData, "pi-revit", "state.json");
}

function compareVersions(a: string, b: string): number {
	const pa = a.split(".").map((part) => Number.parseInt(part, 10) || 0);
	const pb = b.split(".").map((part) => Number.parseInt(part, 10) || 0);
	for (let i = 0; i < 3; i++) {
		if ((pa[i] ?? 0) !== (pb[i] ?? 0)) return (pa[i] ?? 0) - (pb[i] ?? 0);
	}
	return 0;
}

/** Parses `## [x.y.z]` entries from the packaged CHANGELOG.md (the same header
 * format pi's own changelog parser reads) and returns the ones newer than
 * sinceVersion, newest first — so a 0.2.5 -> 0.2.9 jump shows all four. */
async function newChangelogEntries(sinceVersion: string): Promise<{ version: string; body: string }[]> {
	const changelogPath = path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "..", "CHANGELOG.md");
	const markdown = await readFile(changelogPath, "utf8");
	const headers = [...markdown.matchAll(/^##\s+\[?(\d+\.\d+\.\d+)\]?[^\n]*$/gm)];
	const entries: { version: string; body: string }[] = [];
	for (let i = 0; i < headers.length; i++) {
		const version = headers[i][1];
		if (compareVersions(version, sinceVersion) <= 0) continue;
		const start = (headers[i].index ?? 0) + headers[i][0].length;
		const end = i + 1 < headers.length ? headers[i + 1].index : markdown.length;
		entries.push({ version, body: markdown.slice(start, end).trim() });
	}
	return entries;
}

/** Shows the changelog entries between the last announced version and the
 * current one, once per update, then records the current version. A fresh
 * install records silently (nothing is "new" yet). Every failure is swallowed:
 * the announcer must never break a session. */
async function announceUpdateOnce(notify: (message: string, level: "info") => void): Promise<void> {
	try {
		const statePath = announcerStatePath();
		let lastVersion: string | null = null;
		try {
			const state = JSON.parse(await readFile(statePath, "utf8")) as { lastAnnouncedVersion?: string };
			if (typeof state.lastAnnouncedVersion === "string") lastVersion = state.lastAnnouncedVersion;
		} catch {
			// First run: no state yet.
		}
		if (lastVersion === packageVersion) return;

		if (lastVersion) {
			const entries = await newChangelogEntries(lastVersion);
			if (entries.length > 0) {
				const text = entries.map((entry) => `pi-revit ${entry.version}\n${entry.body}`).join("\n\n");
				notify(`What's new in pi-revit (updated from ${lastVersion}):\n\n${text}`, "info");
			}
		}
		await mkdir(path.dirname(statePath), { recursive: true });
		await writeFile(statePath, JSON.stringify({ lastAnnouncedVersion: packageVersion }, null, 2), "utf8");
	} catch {
		// Never let the announcer break a session.
	}
}

const packageRoot = fileURLToPath(new URL("../../", import.meta.url));
const nativeToolNames = new Set(documentedTools.filter(tool => tool.source === "native").map(tool => tool.name));

/** Branch and commit when the extension runs from a source checkout; null for an installed package. */
function sourceRevision(): { branch: string | null; commit: string | null } | null {
	try {
		const gitDir = path.join(packageRoot, ".git");
		const head = readFileSync(path.join(gitDir, "HEAD"), "utf8").trim();
		if (!head.startsWith("ref: ")) return { branch: null, commit: head };
		const ref = head.slice(5);
		let commit: string | null = null;
		try { commit = readFileSync(path.join(gitDir, ref), "utf8").trim(); }
		catch { commit = readFileSync(path.join(gitDir, "packed-refs"), "utf8").split(/\r?\n/).find(line => line.endsWith(` ${ref}`))?.split(" ")[0] ?? null; }
		return { branch: ref.replace(/^refs\/heads\//, ""), commit };
	} catch { return null; }
}

/** What is actually loaded: package, guidance revision, source revision and per-tool contract agreement. */
function loadedStatus(live: Map<string, string>) {
	const matching: string[] = [], changed: string[] = [], undocumented: string[] = [];
	for (const [name, hash] of live) {
		const packaged = packagedContracts.get(name);
		if (!packaged) undocumented.push(name);
		else if (packaged.contract_hash === hash) matching.push(name);
		else changed.push(name);
	}
	return {
		extension_package: packageVersion, extension_root: packageRoot, documentation_revision: documentationRevision, source: sourceRevision(),
		contracts: live.size ? { matching: matching.length, changed, undocumented,
			packaged_not_advertised: [...packagedContracts.values()].filter(tool => tool.source === "bridge" && !live.has(tool.name)).map(tool => tool.name) }
			: "no bridge catalogue discovered yet",
	};
}

function registerPing(pi: ExtensionAPI, resolve: BridgeResolver, onBridgeAlive?: () => Promise<"ready" | "registered" | "failed">, status?: () => unknown) {
	pi.registerTool({
		name: "ping",
		label: "Ping Revit Bridge",
		description: "Check that the Revit bridge is reachable and report the Revit version, plus which PI-Revit extension package, guidance revision, source revision and tool contracts are loaded (changed or undocumented contracts mean manuals may not match the bridge).",
		parameters: Type.Object({}),
		promptSnippet: "Check Revit bridge availability.",
		promptGuidelines: ["Use ping when Revit tools fail or bridge availability is unclear; it also reports which PI-Revit package, guidance revision and contracts are loaded."],
		executionMode: "sequential",
		async execute(_toolCallId, _params, signal) {
			const payload = await bridgeRequest("/ping", { method: "GET", bridge: await resolve() }, signal, 10_000);
			const warning = versionMismatch((payload as { addinVersion?: string }).addinVersion);
			// The bridge is alive: if this session started before Revit and only has
			// ping, register the bridge tools now and tell the model they arrived.
			let registrationNote = "";
			if (onBridgeAlive) {
				const state = await onBridgeAlive();
				if (state === "registered")
					registrationNote = "\nNOTE: The Revit bridge tools (get_elements, set_parameters, execute_csharp, ...) were just registered in this session and are available from now on.";
				else if (state === "failed")
					registrationNote = "\nNOTE: Bridge tool discovery failed even though ping succeeded; retry ping or restart pi.";
			}
			const loaded = status?.();
			return {
				content: [{ type: "text", text: JSON.stringify(payload) + (warning ? `\nWARNING: ${warning}` : "") + registrationNote
					+ (loaded ? `\nPI-Revit loaded: ${JSON.stringify(loaded)}` : "") }],
				details: loaded ? { ...(payload as object), pi_revit: loaded } : payload,
			};
		},
	});
}

const REDISCOVERY_INTERVAL_MS = 15_000;

export default async function revitConnector(pi: ExtensionAPI) {
	const bridgeVersions = new Map<string, string | null>();
	const bridgeKey = (info: BridgeInfo) => info.bridgeId ?? `${info.baseUrl}\0${info.token}`;
	const instances = createInstanceRouter(readBridgeInfo, async info => {
		const payload = await bridgeRequest("/ping", { method: "GET", bridge: info }, undefined, 2000) as Record<string, unknown>;
		bridgeVersions.set(bridgeKey(info), typeof payload.addinVersion === "string" ? payload.addinVersion : null);
		return payload;
	});
	registerResultReader(pi);
	registerOperationReader(pi, instances.resolve);
	const monitors: RequestMonitors = { completion: createCompletionMonitor(), scope: createScopeMonitor() };
	registerScriptLibrary(pi, async (args, signal, prepared) => {
		const result = await runBridgeTool("execute_csharp", args, signal, LONG_TIMEOUT_MS, instances.resolve, prepared);
		const notes = monitorNotes(monitors, "manage_revit_scripts", args, { write: true }, result.details);
		return notes.length ? { ...result, content: [...result.content, ...notes.map(text => ({ type: "text" as const, text }))] } : result;
	},
		async value => ({ content: await modelContent("manage_revit_scripts", { details: { payload: value } }), details: value }));
	pi.registerTool({
		name: "manage_revit_instances", label: "Manage Revit Instances",
		description: "List reachable local Revit bridge sessions or select one for this Pi session. The first sole instance is bound automatically; multiple instances require explicit selection before model calls. After that session closes or restarts, select its new bridge_id: calls never fall back to another session. Selection refreshes the tool catalogue. Operation receipt lookups and identical retries use their original session. Read get_model_overview again after switching; document IDs are session-specific.",
		parameters: Type.Object({ action: Type.Optional(Type.Union([Type.Literal("list"), Type.Literal("select")])), bridge_id: Type.Optional(Type.String()) }),
		executionMode: "sequential",
		async execute(_id, args) {
			let result: unknown;
			if ((args.action ?? "list") === "list") result = { instances: await instances.list() };
			else if (args.action === "select" && args.bridge_id) {
				// Drain discovery for the previous target before changing selection.
				if (discoveryInFlight) await discoveryInFlight;
				const selection = await instances.select(args.bridge_id);
				// A retry timer may have started another discovery while selection probed.
				// Drain that request too, then reset synchronously before fetching anew.
				if (discoveryInFlight) await discoveryInFlight;
				bridgeToolsRegistered = false;
				catalog.reset();
				const ready = await discoverAndRegister();
				result = { ...selection, tool_catalog_ready: ready };
				if (!ready && sessionActive) startRetry();
			} else throw new Error("select requires bridge_id from the instance list.");
			return { content: [{ type: "text", text: JSON.stringify(result) }], details: result };
		},
	});
	// Self-healing discovery: when pi starts before Revit is ready, the initial
	// GET /tools fails and only native utilities are registered. Rather than requiring a
	// fresh pi start (/reload does not reliably re-run async registration), a
	// background retry keeps probing until the bridge appears, and a successful
	// ping also triggers an immediate attempt.
	let bridgeToolsRegistered = false;
	let sharedRules: string[] = [];
	let discoveryInFlight: Promise<boolean> | null = null;
	let sessionActive = false;
	let disposed = false;
	let timer: ReturnType<typeof setInterval> | undefined;
	const catalog = createToolCatalog(pi, discoverAndRegister);

	async function discoverAndRegister(): Promise<boolean> {
		if (disposed) return false;
		if (bridgeToolsRegistered) return true;
		if (discoveryInFlight) return discoveryInFlight;
		discoveryInFlight = (async () => {
			try {
				const selectedBridge = await instances.resolve();
				const payload = (await bridgeRequest("/tools", { method: "GET", bridge: selectedBridge }, undefined, DISCOVERY_TIMEOUT_MS)) as {
					tools?: BridgeToolDescriptor[];
				};
				const descriptors = Array.isArray(payload?.tools) ? payload.tools : [];
				if (disposed || !Array.isArray(payload?.tools)) return false;
				catalog.setBridgeVersion(bridgeVersions.get(bridgeKey(selectedBridge)) ?? null);
				// Native tool names are reserved: a bridge cannot replace an extension utility.
				const valid = descriptors.filter(descriptor => descriptor && typeof descriptor.name === "string" && descriptor.name
					&& !nativeToolNames.has(descriptor.name));
				const { shared, perTool } = hoistSharedGuidelines(valid);
				sharedRules = shared;
				const added: string[] = [];
				for (const descriptor of valid) {
					registerBridgeTool(pi, descriptor, instances.resolve, perTool.get(descriptor.name) ?? [], monitors);
					catalog.add(descriptor);
					added.push(descriptor.name);
				}
				if (sessionActive) {
					catalog.hideAdvanced(added);
					pi.setActiveTools([...new Set([...pi.getActiveTools(), ...descriptors.filter(d => added.includes(d.name) && d.tier !== "advanced").map(d => d.name)])]);
				}
				bridgeToolsRegistered = true;
				return true;
			} catch {
				// Bridge down (Revit closed, still starting, stale bridge.json):
				// keep native utilities available and try again later.
				return false;
			} finally {
				discoveryInFlight = null;
			}
		})();
		return discoveryInFlight;
	}

	// ping is hard-coded: it must work (and report clearly) even when the
	// bridge is down, so it is never part of /tools discovery. A successful
	// ping doubles as a re-discovery trigger — the natural first call in a
	// session that finds itself without bridge tools.
	registerPing(pi, instances.resolve, async () => {
		if (bridgeToolsRegistered) return "ready";
		return (await discoverAndRegister()) ? "registered" : "failed";
	}, () => loadedStatus(catalog.liveContracts()));

	// One always-visible platform section per run: global protocols plus rules shared
	// by several tools. It does not depend on the skill being read or on tool count.
	pi.on("before_agent_start", async event => {
		monitors.completion.reset();
		monitors.scope.reset((event as { prompt?: string }).prompt ?? "");
		const options = (event as { systemPromptOptions?: { sections?: Record<string, string> } }).systemPromptOptions;
		if (options?.sections) options.sections.pi_revit = buildPlatformSection({ manualDirectory, skillRoot, sharedRules });
	});

	// Surface an incomplete update (see versionMismatch) once per session, right
	// where the user lands after running `pi update --extensions`. Bridge down at
	// session start is the normal Revit-closed case: stay quiet.
	pi.on("session_start", async (_event, ctx) => {
		sessionActive = true;
		catalog.hideAdvanced();
		if (!bridgeToolsRegistered) startRetry();
		await announceUpdateOnce((message, level) => ctx.ui.notify(message, level));
		try {
			const payload = (await bridgeRequest("/ping", { method: "GET", bridge: await instances.resolve() }, undefined, 3_000)) as { addinVersion?: string };
			const warning = versionMismatch(payload.addinVersion);
			if (warning) ctx.ui.notify(warning, "warning");
		} catch {
			// No bridge, no verdict.
		}
	});

	pi.on("session_shutdown", async () => {
		disposed = true;
		sessionActive = false;
		if (timer) clearInterval(timer);
		timer = undefined;
	});

	function startRetry() {
		if (timer || disposed) return;
		timer = setInterval(async () => {
			if (!(await discoverAndRegister())) return;
			if (timer) clearInterval(timer);
			timer = undefined;
			if (disposed) return;
			// Refresh the model's knowledge on its next turn without interrupting the user.
			try {
				pi.sendMessage(
					{
						customType: "pi-revit",
						content: "Revit is now reachable. Core bridge tools are available; use find_revit_tools to activate specialist tools.",
						display: true,
					},
					{ deliverAs: "nextTurn" },
				);
			} catch {
				// Tool registration remains valid if the session cannot accept a message.
			}
		}, REDISCOVERY_INTERVAL_MS);
		timer.unref?.();
	}
	await discoverAndRegister();
}
