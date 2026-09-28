import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { contractHash, descriptorLimits, packagedContracts, withApiLookups, type ToolLimit } from "./contracts.js";
import { createDiscoveryIndex } from "./discovery.js";
import { documentedTools, packagedGuidance, readablePathWithin, resolveToolDocumentation, skillRoot } from "./tool-documentation.js";

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
	documentKinds?: string[] | null;
	promptSnippet?: string | null;
	promptGuidelines?: string[] | null;
	keywords?: string[] | null;
	limits?: unknown;
	verification?: string | null;
}

interface Candidate {
	name: string; source: "native" | "bridge"; description: string | null; summary: string | null; keywords: string[];
	tier: string | null; effects: string[] | null; verification: string | null; limits: ToolLimit[] | null; inputs: string[]; documentKinds: string[] | null;
}
interface Guide { name: string; kind: string; path: string | null; summary: string; keywords: string[] }

/** Packaged guidance plus every skill found beside this package's skill (future subject skills included). */
function loadGuidance(): Guide[] {
	const guides: Guide[] = packagedGuidance.map(g => ({ name: g.name, kind: g.kind, path: readablePathWithin(skillRoot, g.path), summary: g.summary, keywords: g.keywords ?? [] }));
	const skillsRoot = path.resolve(skillRoot, "..");
	let directories: string[] = [];
	try { directories = readdirSync(skillsRoot, { withFileTypes: true }).filter(d => d.isDirectory()).map(d => d.name).sort(); } catch { /* no sibling skills */ }
	for (const directory of directories) {
		const file = readablePathWithin(skillsRoot, path.join(directory, "SKILL.md"));
		if (!file) continue;
		try {
			const front = readFileSync(file, "utf8").match(/^---\r?\n([\s\S]*?)\r?\n---/)?.[1] ?? "";
			const field = (key: string) => front.match(new RegExp(`^${key}:\\s*(.+)$`, "m"))?.[1]?.trim().replace(/^["']|["']$/g, "") ?? null;
			const keywords = (field("keywords") ?? "").split(",").map(k => k.trim()).filter(Boolean);
			guides.push({ name: field("name") ?? directory, kind: "skill", path: file, summary: field("description") ?? "", keywords });
		} catch { /* unreadable skill metadata is skipped, never fatal */ }
	}
	return guides;
}

const FALLBACK_ROUTE = [
	"A missing dedicated tool does not mean the operation is impossible.",
	"If a tool is still expected, retry with fewer task words, a different wording, exact names, or scope=documentation.",
	"Otherwise verify the needed Revit API members with search_api_docs, then use execute_csharp within the requested scope.",
	"Report that something is not possible only after that check, naming what was checked.",
];

/** The bridge registry is the source of truth; discovery never duplicates its schemas. */
export function createToolCatalog(pi: ExtensionAPI, discover: () => Promise<boolean>) {
	const entries = new Map<string, BridgeToolDescriptor>();
	const liveHashes = new Map<string, string>();
	// Pi retains registrations when a bridge switch merely deactivates them.
	// Keep this separate from the selected bridge's current discovery snapshot.
	const registeredBridgeNames = new Set<string>();
	const manifestByName = new Map(documentedTools.map(entry => [entry.name, entry]));
	const natives = documentedTools.filter(entry => entry.source === "native");
	const guides = loadGuidance();
	const index = createDiscoveryIndex();
	let bridgeVersion: string | null = null;
	let observedAt: string | null = null;
	function hideAdvanced(names: string[] = [...entries.keys()]) {
		const advanced = new Set(names.filter(name => entries.get(name)?.tier === "advanced"));
		pi.setActiveTools(pi.getActiveTools().filter(name => !advanced.has(name)));
	}
	function candidate(name: string, source: "native" | "bridge", live?: BridgeToolDescriptor): Candidate {
		const packaged = packagedContracts.get(name);
		return {
			name, source,
			description: live?.description ?? null,
			summary: manifestByName.get(name)?.summary ?? null,
			// Live v2 metadata wins; packaged metadata fills gaps from older bridges; unknown stays unknown.
			keywords: live?.keywords ?? packaged?.keywords ?? [],
			tier: live?.tier ?? packaged?.tier ?? (source === "native" ? "core" : null),
			effects: live?.effects ?? (live?.write !== undefined ? (live.write ? ["model"] : []) : packaged?.effects ?? null),
			verification: live?.verification ?? packaged?.verification ?? null,
			limits: descriptorLimits(live?.limits) ?? packaged?.limits ?? null,
			inputs: Object.keys(((live?.parameters ?? packaged?.parameters) as { properties?: object } | undefined)?.properties ?? {}),
			documentKinds: live?.documentKinds ?? packaged?.document_kinds ?? null,
		};
	}

	pi.registerTool({
		name: "find_revit_tools",
		label: "Find Revit Tools",
		description: "Search PI-Revit tools, workflows and guidance by English task words, and activate matching tools. Tool vocabulary is English: translate a request written in another language into English search words first. The default available scope covers native utilities and the selected bridge's last-discovered tools; a query or names activates matches, while browsing only lists. scope=documentation searches packaged manuals without contacting Revit or activating tools, including when Revit is closed. Each tool result gives its declared limits with alternatives, its verification method, its manual path and whether that manual matches the selected bridge's contract. When nothing matches every word, the result states the remaining route: API search, then custom execution. Activation is additive and runs no Revit operation.",
		promptSnippet: "Find PI-Revit capabilities, their limits and alternatives, and activate specialist tools.",
		promptGuidelines: ["find_revit_tools: search with short English task words (translate the user's language first); use names for a known tool and scope=documentation while Revit is closed. Read only the returned manuals you need."],
		parameters: Type.Object({
			scope: Type.Optional(Type.Union([Type.Literal("available"), Type.Literal("documentation")], { description: "available (default): search registered native and last-discovered bridge tools. documentation: read-only packaged manual lookup; no bridge request or activation." })),
			query: Type.Optional(Type.String({ description: "Short English task words, such as 'tag rooms' or 'count walls per level'; translate from the user's language first. Tools matching every word rank first, followed by strong partial matches; when nothing matches every word the remaining route is explained. Use separate queries for different capabilities." })),
			names: Type.Optional(Type.Array(Type.String(), { maxItems: 20, description: "Exact tool names; takes precedence over query." })),
			activate: Type.Optional(Type.Boolean({ description: "Activate returned matches. Default true for a query or exact names; false for browsing." })),
			offset: Type.Optional(Type.Integer({ minimum: 0 })),
			limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 20 })),
		}),
		executionMode: "sequential",
		async execute(_id, args) {
			const scope = args.scope ?? "available";
			if (scope !== "available" && scope !== "documentation") throw new Error("scope must be available or documentation.");
			if (scope === "documentation" && args.activate === true) throw new Error("documentation scope does not activate tools. Use available scope to activate registered tools.");
			if (scope === "available" && observedAt === null) await discover();
			const candidates = new Map<string, Candidate>();
			if (scope === "documentation") for (const tool of documentedTools) candidates.set(tool.name, candidate(tool.name, tool.source));
			for (const tool of natives) candidates.set(tool.name, candidate(tool.name, "native"));
			for (const tool of entries.values()) candidates.set(tool.name, candidate(tool.name, "bridge", tool));
			const names = args.names?.length ? [...new Set(args.names)] : undefined;
			if (names?.some(name => !candidates.has(name))) throw new Error(`Unknown Revit tool names or tools unavailable in this scope: ${names.filter(name => !candidates.has(name)).join(", ")}. Use scope=documentation to look up packaged manuals independently of bridge availability.`);
			const query = (args.query ?? "").trim();
			const toDocument = (c: Candidate) => ({ name: c.name, keywords: c.keywords, summary: c.summary, description: c.description, limits: c.limits?.map(limit => limit.what), inputs: c.inputs });
			let mode: "names" | "browse" | "all" | "partial" | "none";
			let matches: Candidate[];
			if (names) { mode = "names"; matches = [...candidates.values()].filter(c => names.includes(c.name)).sort((a, b) => a.name.localeCompare(b.name)); }
			else if (!query) { mode = "browse"; matches = [...candidates.values()].sort((a, b) => a.name.localeCompare(b.name)); }
			else { const result = index.search([...candidates.values()], query, toDocument); mode = result.mode; matches = result.matches.map(m => m.item); }
			const offset = Math.max(0, args.offset ?? 0);
			const pageSize = Math.max(1, Math.min(20, args.limit ?? (mode === "partial" ? 5 : 10)));
			const page = matches.slice(offset, offset + pageSize);
			const activate = scope === "available" && (args.activate ?? (mode === "names" || mode === "all" || mode === "partial"));
			if (activate && page.length) pi.setActiveTools([...new Set([...pi.getActiveTools(), ...page.map(entry => entry.name)])]);
			const active = new Set(pi.getActiveTools());
			const guidance = query && !names ? index.search(guides, query, g => ({ name: g.name, keywords: g.keywords, summary: g.summary })).matches.slice(0, 5)
				.map(({ item }) => ({ name: item.name, kind: item.kind, path: item.path, summary: item.summary })) : [];
			const result = {
				scope, bridge_catalog_known: observedAt !== null, bridge_catalog_observed_at: observedAt, match: mode,
				instructions: "Bridge availability is a discovery snapshot, not a fresh health check; registration, advertisement and activation are separate. Follow a tool's limits alternatives when it does not cover the request; an API alternative's lookup verifies all its members in one search_api_docs call. contract_match means the manual was generated from the selected bridge's exact input contract; otherwise trust the active schema. Read only relevant manual paths.",
				total_count: matches.length, offset, returned_count: page.length,
				next_offset: offset + page.length < matches.length ? offset + page.length : null,
				tools: page.map(entry => ({ name: entry.name, description: entry.description ?? entry.summary, source: entry.source,
					tier: entry.tier, effects: entry.effects, verification: entry.verification, document_kinds: entry.documentKinds,
					limits: entry.limits ? withApiLookups(entry.limits) : "unknown: no declared limits; if the tool does not cover the request, use the capability route",
					registered: entry.source === "native" || registeredBridgeNames.has(entry.name),
					advertised_by_selected_bridge: entry.source === "native" ? null : observedAt !== null ? entries.has(entry.name) : null,
					active: active.has(entry.name),
					documentation: resolveToolDocumentation(entry.name, { contractHash: entries.has(entry.name) ? liveHashes.get(entry.name) ?? null : null, bridgeVersion }) })),
				guidance,
				...(query && (mode === "none" || mode === "partial") ? { fallback: { reason: mode === "none" ? "No tool matched this query." : "No tool matched every word; these are partial matches.", route: FALLBACK_ROUTE } } : {}),
			};
			return { content: [{ type: "text" as const, text: JSON.stringify(result) }], details: result };
		},
	});
	function reset() {
		pi.setActiveTools(pi.getActiveTools().filter(name => !entries.has(name)));
		entries.clear();
		liveHashes.clear();
		bridgeVersion = null;
		observedAt = null;
	}
	return { add: (entry: BridgeToolDescriptor) => {
		entries.set(entry.name, entry); liveHashes.set(entry.name, contractHash(entry)); registeredBridgeNames.add(entry.name); observedAt = new Date().toISOString();
	},
		// Called after successful /tools discovery, including a valid empty array.
		setBridgeVersion: (value: string | null) => { bridgeVersion = value; observedAt = new Date().toISOString(); },
		liveContracts: () => new Map(liveHashes), hideAdvanced, reset };
}
