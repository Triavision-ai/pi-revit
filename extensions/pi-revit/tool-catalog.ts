import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";

export interface BridgeToolDescriptor {
	name: string;
	label?: string;
	description?: string;
	category?: string;
	tier?: string;
	parameters?: unknown;
	executionMode?: string;
	write?: boolean;
	effects?: string[];
	requiresDocument?: boolean;
	promptSnippet?: string | null;
	promptGuidelines?: string[] | null;
}

/** The bridge registry is the source of truth; discovery never duplicates its schemas. */
export function createToolCatalog(pi: ExtensionAPI, discover: () => Promise<boolean>) {
	const entries = new Map<string, BridgeToolDescriptor>();
	function hideAdvanced(names: string[] = [...entries.keys()]) {
		const advanced = new Set(names.filter(name => entries.get(name)?.tier === "advanced"));
		pi.setActiveTools(pi.getActiveTools().filter(name => !advanced.has(name)));
	}

	pi.registerTool({
		name: "find_revit_tools",
		label: "Find Revit Tools",
		description: "Search the installed Revit tool catalogue and activate matching tools for this Pi session. Search words match tool names and descriptions. Supply exact names to activate known tools. With no query or names, list tools without activating them. Activation is additive and does not run any Revit operation. Read the returned schemas on the next model turn before calling a newly activated tool.",
		promptSnippet: "Find and activate specialist Revit tools for links, schedules, relationships, inspection and editing.",
		promptGuidelines: ["Use find_revit_tools to discover and activate specialist Revit tools before using execute_csharp for a task that may already have a dedicated tool."],
		parameters: Type.Object({
			query: Type.Optional(Type.String({ description: "Search words, such as linked elements or schedules." })),
			names: Type.Optional(Type.Array(Type.String(), { maxItems: 20, description: "Exact tool names; takes precedence over query." })),
			activate: Type.Optional(Type.Boolean({ description: "Activate returned matches. Default true for a query or exact names; false for browsing." })),
			offset: Type.Optional(Type.Integer({ minimum: 0 })),
			limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 20 })),
		}),
		executionMode: "sequential",
		async execute(_id, args) {
			if (!entries.size && !(await discover())) throw new Error("The Revit tool catalogue is unavailable. Start Revit with the bridge loaded and retry.");
			const names = args.names?.length ? [...new Set(args.names)] : undefined;
			if (names?.some(name => !entries.has(name))) throw new Error(`Unknown Revit tool names: ${names.filter(name => !entries.has(name)).join(", ")}`);
			const words = (args.query ?? "").trim().toLowerCase().split(/[\s_]+/).filter(Boolean);
			const matches = [...entries.values()].filter(entry => names ? names.includes(entry.name)
				: words.every(word => `${entry.name.replaceAll("_", " ")} ${entry.description ?? ""}`.toLowerCase().includes(word)))
				.sort((a, b) => a.name.localeCompare(b.name));
			const offset = Math.max(0, args.offset ?? 0);
			const page = matches.slice(offset, offset + Math.max(1, Math.min(20, args.limit ?? 10)));
			const activate = args.activate ?? Boolean(names || words.length);
			if (activate && page.length) pi.setActiveTools([...new Set([...pi.getActiveTools(), ...page.map(entry => entry.name)])]);
			const active = new Set(pi.getActiveTools());
			const result = {
				total_count: matches.length, offset, returned_count: page.length,
				next_offset: offset + page.length < matches.length ? offset + page.length : null,
				tools: page.map(entry => ({ name: entry.name, description: entry.description, tier: entry.tier ?? "core", effects: entry.effects ?? (entry.write ? ["model"] : []), active: active.has(entry.name) })),
			};
			return { content: [{ type: "text" as const, text: JSON.stringify(result) }], details: result };
		},
	});
	function reset() {
		pi.setActiveTools(pi.getActiveTools().filter(name => !entries.has(name)));
		entries.clear();
	}
	return { add: (entry: BridgeToolDescriptor) => entries.set(entry.name, entry), hideAdvanced, reset };
}
