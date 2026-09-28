/**
 * Scope monitor (inv:existing-objects-not-reused). Tool-agnostic: it reads the model_changes
 * report the bridge attaches to every model-changing result, and the name_collision facts a
 * rejected creation carries, never tool names. Per user request it keeps the IDs of objects
 * created in that request. When a call changes an object that existed before the request and
 * whose name the request mentions, or a name the request uses is already taken, it returns a
 * note that the object predates the request; the platform protocol then requires asking or
 * reporting. It steers and never blocks. Reset at the start of every user prompt.
 */
export interface ChangedItem { id: number; name?: string | null; category?: string | null }
export interface ModelChanges {
	observed?: boolean;
	rolled_back?: boolean;
	/** Family types and parameters (not elements) added, removed or changed by the call, in a family document. */
	family?: { types_added?: string[]; types_removed?: string[]; types_changed?: string[]; parameters_added?: string[]; parameters_removed?: string[] };
	added?: { count: number; items?: ChangedItem[] };
	modified?: { count: number; items?: ChangedItem[] };
	deleted?: { count: number; ids?: number[] };
}
export interface NameCollision { existing_id: number | null; kind?: string; name?: string; sheet_number?: string }

/** The model_changes report of a bridge payload, or null (older bridge, read tool, non-object payload). */
export function readModelChanges(payload: unknown): ModelChanges | null {
	const value = payload !== null && typeof payload === "object" ? (payload as { model_changes?: unknown }).model_changes : undefined;
	return value !== null && typeof value === "object" ? value as ModelChanges : null;
}

/**
 * Whether a call actually changed the model, from model_changes: a report without observed
 * document changes means nothing changed (a read through a write-capable tool). Undefined only
 * when there is no report (an older bridge), so declared metadata decides.
 */
export function changedModel(payload: unknown): boolean | undefined {
	const changes = readModelChanges(payload);
	if (!changes) return undefined;
	if (!changes.observed) return false;
	return (changes.added?.count ?? 0) + (changes.modified?.count ?? 0) + (changes.deleted?.count ?? 0) > 0;
}

/** Every name_collision object anywhere in a payload (rejected rows keep it next to their reason). */
export function findCollisions(payload: unknown, found: NameCollision[] = [], depth = 0): NameCollision[] {
	if (depth > 6 || payload === null || typeof payload !== "object") return found;
	for (const [key, value] of Object.entries(payload as Record<string, unknown>)) {
		if (key === "name_collision" && value && typeof value === "object" && "existing_id" in value) found.push(value as NameCollision);
		else if (key !== "model_changes") findCollisions(value, found, depth + 1);
	}
	return found;
}

const normalize = (text: string) => text.normalize("NFKC").toLowerCase().replace(/\s+/g, " ").trim();
const words = new Intl.Segmenter(undefined, { granularity: "word" });
const graphemes = new Intl.Segmenter(undefined, { granularity: "grapheme" });

/**
 * Word boundaries of a text in any writing system, from Unicode segmentation (ICU): spaces and
 * punctuation where a script uses them, dictionary boundaries where it does not (Chinese,
 * Japanese, Thai, Khmer, ...). No language or script list is involved.
 */
export function wordBoundaries(text: string): Set<number> {
	const bounds = new Set([0, text.length]);
	for (const segment of words.segment(text)) { bounds.add(segment.index); bounds.add(segment.index + segment.segment.length); }
	return bounds;
}

/** Whether name occurs in text as whole words: 'Site' is not mentioned by 'opposite'. */
export function mentionsName(text: string, bounds: Set<number>, name: string | null | undefined, minGraphemes = 2): boolean {
	const value = normalize(name ?? "");
	if ([...graphemes.segment(value)].length < minGraphemes) return false;
	for (let at = text.indexOf(value); at >= 0; at = text.indexOf(value, at + 1))
		if (bounds.has(at) && bounds.has(at + value.length)) return true;
	return false;
}

export function createScopeMonitor(options: { minNameGraphemes?: number } = {}) {
	let request = "";
	let bounds = new Set<number>();
	const created = new Set<number>();
	const createdFamilyTypes = new Set<string>();
	const notedFamilyTypes = new Set<string>();
	const noted = new Set<number>();
	const notedCollisions = new Set<number | string>();
	const mentioned = (name: string | null | undefined) => mentionsName(request, bounds, name, options.minNameGraphemes ?? 2);
	return {
		/** Start of a new user request; its text decides which object names the request mentions. */
		reset(prompt: string) {
			request = normalize(prompt ?? ""); bounds = wordBoundaries(request);
			created.clear(); noted.clear(); notedCollisions.clear(); createdFamilyTypes.clear(); notedFamilyTypes.clear();
		},
		/** Record one tool result; returns a scope note to append, or null. */
		afterResult(payload: unknown): string | null {
			const notes: string[] = [];
			const changes = readModelChanges(payload);
			for (const item of changes?.added?.items ?? []) created.add(item.id);
			for (const name of changes?.family?.types_added ?? []) createdFamilyTypes.add(name);
			// Family types have no element ID; the same rule applies to them by name.
			const familyTypes = (changes?.family?.types_changed ?? []).filter(name => !createdFamilyTypes.has(name) && !notedFamilyTypes.has(name) && mentioned(name));
			for (const name of familyTypes) notedFamilyTypes.add(name);
			const preexisting = [...(changes?.modified?.items ?? []).filter(item => !created.has(item.id) && !noted.has(item.id) && mentioned(item.name)),
				...familyTypes.map(name => ({ id: null as unknown as number, name, category: "family type" }))];
			for (const item of preexisting) if (item.id !== null) noted.add(item.id);
			if (preexisting.length) notes.push(`PI-Revit scope note: this call changed ${preexisting.map(item => item.id === null ? `family type '${item.name}'` : `'${item.name}' (id ${item.id})`).join(", ")}, which existed before this request: no call in this request created it. `
				+ "If the request asked you to create an object with this name, you changed an existing object instead: tell the user exactly what you changed and ask before changing it further. If the request asked you to change this existing object, continue.");
			// Family types have no element ID; they are noted by name instead.
			const key = (c: NameCollision) => c.existing_id ?? `${c.kind}:${c.name}`;
			const collisions = findCollisions(payload).filter(collision => !notedCollisions.has(key(collision)));
			for (const collision of collisions) notedCollisions.add(key(collision));
			if (collisions.length) notes.push(`PI-Revit scope note: ${collisions.map(c => `'${c.name ?? c.sheet_number}' is already used by ${c.kind ?? "an object"}${c.existing_id !== null ? ` ${c.existing_id}` : ""}`).join("; ")}. `
				+ "That object existed before this call. Do not edit, rename, reuse, replace or delete it to get past the collision: ask the user which object or name to use, or create under a distinct name and report the collision.");
			return notes.length ? notes.join("\n") : null;
		},
		/** IDs of objects created in this request, as reported by model_changes. */
		created: () => [...created],
	};
}
