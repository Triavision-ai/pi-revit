/**
 * Discovery over every PI-Revit resource kind: tools, workflows, shared references and
 * subject skills (present and future). Tool vocabulary is English, one canonical language.
 * There are no per-language rules: the model translates a request written in any language
 * into English search words, replies in the user's language, and reads localized Revit
 * names from results. Matching only normalizes the English vocabulary: case, accents,
 * simple inflection and word forms, with ranked all-word then partial matches.
 */
export interface DiscoveryDocument {
	name: string;
	/** Strong fields: name, keywords. Medium: summary, declared limits and input names. Weak: description. */
	keywords?: readonly string[];
	summary?: string | null;
	/** Text of declared limits, so a request just outside a tool finds the tool and its alternative. */
	limits?: readonly string[];
	/** Input property names, e.g. "level" or "sheet_id": what a tool can be asked about. */
	inputs?: readonly string[];
	description?: string | null;
}
export interface DiscoveryMatch<T> { item: T; matched: number; score: number; complete: boolean }
export interface DiscoveryResult<T> { mode: "all" | "partial" | "none"; groups: number; matches: DiscoveryMatch<T>[] }

/** Words that carry no tool meaning in English search phrases. */
const STOPWORDS = new Set(["the", "and", "for", "with", "all", "this", "that", "these", "those", "please", "can", "could", "you", "are", "was",
	"how", "what", "which", "does", "from", "into", "each", "every", "some", "any", "get", "give", "tell", "want", "need", "would", "should",
	"its", "there", "their", "them", "use", "using", "revit", "model", "project", "pi", "via", "about", "then", "than", "also", "one", "two"]);

export function normalizeText(text: string): string {
	return text.toLowerCase().normalize("NFKD").replace(/[̀-ͯ]/g, "").replace(/c#/g, "csharp");
}

export function tokenize(text: string): string[] {
	return normalizeText(text).split(/[^a-z0-9]+/).filter(token => token.length >= 3 || /\d/.test(token));
}

/** Simple English inflection stripping. */
export function stem(token: string): string {
	if (token.length > 4 && token.endsWith("ies")) return token.slice(0, -3) + "y";
	if (token.length > 4 && /(ches|shes|xes|sses|zes)$/.test(token)) return token.slice(0, -2);
	if (token.length > 3 && token.endsWith("s") && !token.endsWith("ss") && !token.endsWith("us")) return token.slice(0, -1);
	return token;
}

/** Same word, different form: "connected"/"connection", "rotate"/"rotation", "create"/"creating". */
function hit(variant: string, term: string) {
	if (variant === term) return true;
	if (variant.length < 5 || term.length < 5) return false;
	let common = 0;
	while (common < variant.length && common < term.length && variant[common] === term[common]) common++;
	return common === Math.min(variant.length, term.length) || common >= Math.max(5, Math.min(variant.length, term.length) - 3);
}

const WEIGHTS = { strong: 3, medium: 2, weak: 1 } as const;

export function createDiscoveryIndex() {
	/** Meaningful query words; numbers are call arguments, not capability words. */
	function groups(query: string): string[] {
		return [...new Set(normalizeText(query).split(/[^a-z0-9]+/)
			.filter(word => word.length >= 3 && !/^\d+$/.test(word) && !STOPWORDS.has(word)).map(stem))];
	}
	function terms(document: DiscoveryDocument) {
		const strong = [...tokenize(document.name.replaceAll("_", " ")), ...(document.keywords ?? []).flatMap(tokenize)].map(stem);
		const medium = [document.summary ?? "", ...(document.limits ?? []), ...(document.inputs ?? []).map(name => name.replaceAll("_", " "))]
			.flatMap(tokenize).map(stem);
		const weak = tokenize(document.description ?? "").map(stem);
		return [[WEIGHTS.strong, new Set(strong)], [WEIGHTS.medium, new Set(medium)], [WEIGHTS.weak, new Set(weak)]] as const;
	}
	function search<T>(items: readonly T[], query: string, toDocument: (item: T) => DiscoveryDocument): DiscoveryResult<T> {
		const queryGroups = groups(query);
		if (queryGroups.length === 0) return { mode: "none", groups: 0, matches: [] };
		const scored = items.map(item => {
			const fields = terms(toDocument(item));
			let matched = 0, score = 0, strongHit = false;
			for (const word of queryGroups) {
				let best = 0;
				for (const [weight, set] of fields) {
					if (weight <= best) continue;
					for (const term of set) if (hit(word, term)) { best = weight; break; }
				}
				if (best > 0) { matched++; score += best; if (best === WEIGHTS.strong) strongHit = true; }
			}
			return { item, matched, score, strongHit, complete: matched === queryGroups.length };
		});
		// Complete matches first; then partial matches with most words and a hit on a name or keyword.
		const needed = Math.max(1, Math.floor(queryGroups.length / 2));
		const ranked = scored.filter(entry => entry.complete || (entry.matched >= needed && entry.strongHit))
			.sort((a, b) => Number(b.complete) - Number(a.complete) || b.matched - a.matched || b.score - a.score
				|| toDocument(a.item).name.localeCompare(toDocument(b.item).name));
		const mode = ranked.some(entry => entry.complete) ? "all" : ranked.length ? "partial" : "none";
		return { mode, groups: queryGroups.length, matches: ranked.map(({ item, matched, score, complete }) => ({ item, matched, score, complete })) };
	}
	return { search, groups };
}
