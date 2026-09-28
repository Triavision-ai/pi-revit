import { createHash } from "node:crypto";
import snapshot from "../../skills/pi-revit/contracts.generated.json";

/**
 * Resource contract v2. Bridge tools declare it in C# (ITool.Keywords/Limits/Verification);
 * Pi-native tools declare it here. scripts/generate-contracts.mjs snapshots both into
 * skills/pi-revit/contracts.generated.json for offline discovery and manual generation.
 * Code is the single owner; the snapshot is generated and checked, never hand-edited.
 */
export type AlternativeKind = "tool" | "api" | "user" | "revit_unsupported";
export type VerificationKind = "reread" | "capture" | "inspect_output" | "none";
export interface ToolLimit { what: string; alternative: { kind: AlternativeKind; ref: string | null } }
export interface ToolContract {
	name: string;
	source: "bridge" | "native";
	tier: string | null;
	write: boolean;
	effects: string[];
	requires_document: boolean;
	/** Document kinds a bridge tool works in ("project", "family"); null for native utilities. */
	document_kinds: string[] | null;
	keywords: string[];
	limits: ToolLimit[];
	verification: VerificationKind | null;
	contract_hash: string;
	parameters: unknown;
}
export const ALTERNATIVE_KINDS: readonly AlternativeKind[] = ["tool", "api", "user", "revit_unsupported"];
export const VERIFICATION_KINDS: readonly VerificationKind[] = ["reread", "capture", "inspect_output", "none"];

const limit = (what: string, kind: AlternativeKind, ref: string): ToolLimit => ({ what, alternative: { kind, ref } });

/** Contracts of tools implemented by this extension (not advertised by a bridge). */
export const NATIVE_CONTRACTS: Record<string, Pick<ToolContract, "keywords" | "limits" | "verification" | "effects" | "write">> = {
	ping: {
		keywords: ["connection", "status", "version", "revit running", "bridge", "loaded version"],
		limits: [limit("Whether a project document is open", "tool", "get_model_overview")],
		verification: null, effects: [], write: false,
	},
	manage_revit_instances: {
		keywords: ["instances", "sessions", "multiple revit", "switch revit", "select session"],
		limits: [limit("Activating a document inside a Revit session", "user", "Open or activate the document in Revit")],
		verification: "reread", effects: ["session"], write: false,
	},
	find_revit_tools: {
		keywords: ["capabilities", "what can you do", "tools", "manuals", "documentation", "help", "workflows"],
		limits: [limit("Reading manual contents", "tool", "read (open the returned path)")],
		verification: "none", effects: ["session"], write: false,
	},
	read_revit_result: {
		keywords: ["large result", "saved result", "continue result", "next fragment"],
		limits: [limit("Paging the original query", "tool", "the original query tool (for example get_elements) with its next_offset")],
		verification: null, effects: [], write: false,
	},
	get_revit_operation: {
		keywords: ["receipt", "timeout", "operation status", "did it run", "retry"],
		limits: [limit("Operations from an earlier bridge session", "user", "Inspect the model state directly; receipts do not survive a restart")],
		verification: null, effects: [], write: false,
	},
	manage_revit_scripts: {
		keywords: ["saved scripts", "script library", "reusable script", "run script", "script history"],
		limits: [limit("Preview or rollback of a script run", "tool", "dedicated tools with preview (set_parameters, transform_elements, delete_elements, manage_views, ...)")],
		verification: "reread", effects: ["model", "ui", "files", "external"], write: true,
	},
};

/** Deterministic JSON with sorted object keys; arrays keep their order. */
export function canonicalJson(value: unknown): string {
	if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
	if (value !== null && typeof value === "object")
		return `{${Object.keys(value as object).sort().filter(key => (value as Record<string, unknown>)[key] !== undefined)
			.map(key => `${JSON.stringify(key)}:${canonicalJson((value as Record<string, unknown>)[key])}`).join(",")}}`;
	return JSON.stringify(value ?? null);
}

/**
 * Executable-contract hash: the input schema and declared effects only. Descriptions,
 * guidelines, keywords and limits are guidance, so rewording them never flags a bridge
 * as incompatible. Runtime discovery and the generator share this one function.
 */
export function contractHash(descriptor: { parameters?: unknown; write?: boolean; effects?: string[]; requiresDocument?: boolean; documentKinds?: string[] | null }): string {
	const write = descriptor.write === true;
	// Support for both document kinds is the default and leaves the hash unchanged; a restriction
	// is executable behavior, so a project-only tool differs from a bridge that does not declare it.
	const kinds = [...(descriptor.documentKinds ?? [])].sort();
	const restricted = kinds.length > 0 && kinds.join(",") !== "family,project";
	const payload = canonicalJson({
		parameters: withoutGuidance(descriptor.parameters ?? { type: "object", properties: {} }),
		write,
		effects: descriptor.effects ?? (write ? ["model"] : []),
		requiresDocument: descriptor.requiresDocument ?? true,
		...(restricted ? { documentKinds: kinds } : {}),
	});
	return createHash("sha256").update(payload).digest("hex").slice(0, 16);
}

/**
 * Schema annotations are guidance: rewording a property description, title or example
 * must not flag a bridge as incompatible. Types, required lists, enums, constants,
 * bounds and defaults remain part of the executable contract. Property names are kept
 * even when a property is literally called "description".
 */
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

export const packagedContracts = new Map((snapshot.tools as ToolContract[]).map(tool => [tool.name, tool]));

/**
 * The one-call API lookup for an "api" limit: the API names in its reference joined with "; ",
 * the multi-member form of search_api_docs, so an alternative's members are verified in one call
 * instead of one search per member. Dotted members and multi-word CamelCase types count; prose
 * words do not. Null when the reference names no API member. The documentation gate checks every
 * name against the installed RevitAPI.xml.
 */
export function apiLookupQuery(ref: string | null | undefined): string | null {
	if (!ref) return null;
	const names = [...ref.matchAll(/\b[A-Z][A-Za-z0-9]*(?:\.[A-Z_][A-Za-z0-9_]*)+\b|\b[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+\b/g)].map(match => match[0]);
	const unique = [...new Set(names)].slice(0, 10);
	return unique.length ? unique.join("; ") : null;
}

/** Limits as shown to the agent: an "api" alternative carries its one-call lookup. */
export function withApiLookups(limits: ToolLimit[]): (ToolLimit & { lookup?: { tool: "search_api_docs"; query: string } })[] {
	return limits.map(limit => {
		const query = limit.alternative.kind === "api" ? apiLookupQuery(limit.alternative.ref) : null;
		return query ? { ...limit, lookup: { tool: "search_api_docs" as const, query } } : limit;
	});
}

/** Normalize a live bridge descriptor's optional v2 fields; absent fields stay unknown. */
export function descriptorLimits(value: unknown): ToolLimit[] | null {
	if (!Array.isArray(value)) return null;
	return value.flatMap(item => {
		const what = (item as ToolLimit)?.what, kind = (item as ToolLimit)?.alternative?.kind;
		return typeof what === "string" && ALTERNATIVE_KINDS.includes(kind)
			? [{ what, alternative: { kind, ref: typeof (item as ToolLimit).alternative.ref === "string" ? (item as ToolLimit).alternative.ref : null } }] : [];
	});
}
