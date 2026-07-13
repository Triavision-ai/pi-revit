import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type, type TSchema } from "typebox";
import { readFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";

interface BridgeInfo {
	baseUrl: string;
	token: string;
	pid?: number;
	revitVersion?: string;
}

interface ContentBlock {
	type: string;
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

/** One entry of GET /tools, as served by ToolRegistry.Describe() on the bridge. */
interface BridgeToolDescriptor {
	name: string;
	label?: string;
	description?: string;
	category?: string;
	tier?: string;
	parameters?: unknown;
	executionMode?: string;
	write?: boolean;
	requiresDocument?: boolean;
	promptSnippet?: string | null;
	promptGuidelines?: string[] | null;
}

const DEFAULT_TIMEOUT_MS = 30_000;
const LONG_TIMEOUT_MS = 120_000;
const DISCOVERY_TIMEOUT_MS = 10_000;
const MAX_MODEL_CONTENT_CHARS = 12_000;

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
	init: { method: "GET" | "POST"; body?: string; query?: Record<string, string> },
	signal?: AbortSignal,
	timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<unknown> {
	const info = await readBridgeInfo();
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
	const suffix = `... [truncated at ${MAX_MODEL_CONTENT_CHARS} chars; full payload is in details]`;
	return text.slice(0, Math.max(0, MAX_MODEL_CONTENT_CHARS - suffix.length)) + suffix;
}

async function runBridgeTool(name: string, args: unknown, signal: AbortSignal | undefined, timeoutMs: number) {
	const payload = (await bridgeRequest(
		`/tools/${encodeURIComponent(name)}/execute`,
		{
			method: "POST",
			body: JSON.stringify(args ?? {}),
			query: { timeout_ms: String(timeoutMs) },
		},
		signal,
		timeoutMs,
	)) as BridgeToolResponse;

	const content =
		Array.isArray(payload.content) && payload.content.length > 0
			? payload.content.map((block) =>
					typeof block.text === "string" ? { ...block, text: capText(block.text) } : block,
				)
			: [{ type: "text", text: capText(JSON.stringify(payload.details ?? {})) }];

	return { content, details: payload.details };
}

function registerBridgeTool(pi: ExtensionAPI, descriptor: BridgeToolDescriptor) {
	const timeoutMs = toolTimeoutMs(descriptor.name);
	pi.registerTool({
		name: descriptor.name,
		label: descriptor.label ?? descriptor.name,
		description: descriptor.description ?? `Revit bridge tool '${descriptor.name}'.`,
		parameters: Type.Unsafe((descriptor.parameters ?? { type: "object", properties: {} }) as TSchema),
		promptSnippet: descriptor.promptSnippet ?? undefined,
		promptGuidelines: descriptor.promptGuidelines ?? undefined,
		executionMode: descriptor.executionMode === "parallel" ? "parallel" : "sequential",
		async execute(_toolCallId, params, signal) {
			return runBridgeTool(descriptor.name, params, signal, timeoutMs);
		},
	});
}

function registerPing(pi: ExtensionAPI) {
	pi.registerTool({
		name: "ping",
		label: "Ping Revit Bridge",
		description: "Check that the Revit bridge is reachable and report the Revit version.",
		parameters: Type.Object({}),
		promptSnippet: "Check Revit bridge availability.",
		promptGuidelines: ["Use ping when Revit tools fail or bridge availability is unclear."],
		executionMode: "sequential",
		async execute(_toolCallId, _params, signal) {
			const payload = await bridgeRequest("/ping", { method: "GET" }, signal, 10_000);
			return {
				content: [{ type: "text", text: JSON.stringify(payload) }],
				details: payload,
			};
		},
	});
}

export default async function revitConnector(pi: ExtensionAPI) {
	// ping is hard-coded: it must work (and report clearly) even when the
	// bridge is down, so it is never part of /tools discovery.
	registerPing(pi);

	let descriptors: BridgeToolDescriptor[];
	try {
		const payload = (await bridgeRequest("/tools", { method: "GET" }, undefined, DISCOVERY_TIMEOUT_MS)) as {
			tools?: BridgeToolDescriptor[];
		};
		descriptors = Array.isArray(payload?.tools) ? payload.tools : [];
	} catch {
		// Bridge down at startup (Revit closed, stale bridge.json, ...): keep
		// only ping registered and never block pi startup. /reload re-discovers.
		return;
	}

	for (const descriptor of descriptors) {
		if (!descriptor || typeof descriptor.name !== "string" || !descriptor.name) continue;
		if (descriptor.name === "ping") continue;
		registerBridgeTool(pi, descriptor);
	}
}
