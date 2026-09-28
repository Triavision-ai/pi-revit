import path from "node:path";

/**
 * The PI-Revit platform section: cross-cutting protocols stated exactly once, always in
 * context (injected through before_agent_start), independent of whether the skill is read
 * and of how many tools exist. Tool-specific facts stay in each tool's own guidelines.
 */
export const PLATFORM_PROTOCOL = [
	"PI-Revit protocol. It applies to every Revit tool, present or future, whether or not the pi-revit skill has been read.",
	"1. Capability: before saying PI-Revit can or cannot do something, check. find_revit_tools (scope=documentation also works while Revit is closed) returns matching tools, their declared limits with alternatives, and related workflows. If a tool does not cover the request, follow its declared alternative. If nothing dedicated fits, verify all needed API members with search_api_docs, up to 10 names per call separated by ';' and use execute_csharp within the requested scope. Say an operation is not possible only after that check, and name what you checked; distinguish \"no dedicated tool\", \"the Revit API does not offer it\" and \"needs the user\".",
	"2. Scope and completion: explanations do not change the model and inspections do not repair it. For a change, list the request's explicit requirements, do only those, and verify each with the tool's declared verification method. Then stop and report what changed, how it was verified and anything unmet. Offer further improvements as suggestions instead of making them. Never hide or remove content the request asks to show. An object made from an existing one inherits its state: results report it as inherited_state, and model_changes lists what each call added, modified or deleted, with the visibility of new views. Check that state against the request and say what you derived from.",
	"3. Existing objects: objects that existed before the request are not yours. If one already has a name the request asks you to create, or a creation is rejected as a name collision, do not edit, reuse, replace or delete it: ask the user, or use a distinct name and report the collision.",
	"4. Evidence: base claims about capabilities and model state on tool results, manuals or API documentation checked in this session, and say which.",
	"5. Identity: for model changes, previews, exports, view activation and selection changes, pass project.documentId from get_model_overview unchanged as expected_document_id; project.documentKind says whether it is a project or a family, and tools refuse kinds they do not declare. Refresh it after reopening, restarting or switching instances. Save As keeps the ID but changes which file a later save affects.",
	"6. Language: the user may write in any language, and the model's Revit UI may be localized. Reply in the user's language. Search tools and API documentation with English terms. Read localized category and parameter names from tool results, and prefer exact identities (BuiltInParameter names, guid:<GUID>) over translated display names.",
];

export function buildPlatformSection(options: { manualDirectory: string; skillRoot: string; sharedRules: readonly string[] }): string {
	const lines = [
		...PLATFORM_PROTOCOL,
		`7. Guidance: each tool's manual is ${path.join(options.manualDirectory, "<tool_name>.md")}; read only the manual of a tool you will use. find_revit_tools returns exact paths and whether a manual matches the selected bridge's contract. Shared rules: ${path.join(options.skillRoot, "references", "execution-rules.md")}. Uncertain outcomes: ${path.join(options.skillRoot, "references", "operation-recovery.md")}.`,
	];
	if (options.sharedRules.length) lines.push("Rules shared by several Revit tools:", ...options.sharedRules.map(rule => `- ${rule}`));
	return lines.join("\n");
}

/**
 * Split bridge guidelines into per-tool rules and rules shared by two or more tools.
 * A tool's own name is normalized away first, so "set_parameters: use X" and
 * "open_view: use X" are recognized as one shared rule. Works for any bridge version.
 */
export function hoistSharedGuidelines(descriptors: readonly { name: string; promptGuidelines?: string[] | null }[]) {
	const normalized = (name: string, rule: string) => rule.split(name).join("{tool}").replace(/^\{tool\}:\s*/, "").trim();
	const counts = new Map<string, number>();
	for (const d of descriptors) for (const rule of new Set((d.promptGuidelines ?? []).map(r => normalized(d.name, r)))) counts.set(rule, (counts.get(rule) ?? 0) + 1);
	const shared = [...counts].filter(([, count]) => count >= 2).map(([rule]) => rule);
	const sharedSet = new Set(shared);
	const perTool = new Map(descriptors.map(d => [d.name, (d.promptGuidelines ?? []).filter(rule => !sharedSet.has(normalized(d.name, rule)))]));
	return { shared: shared.map(rule => rule.replaceAll("{tool}", "the tool")), perTool };
}
