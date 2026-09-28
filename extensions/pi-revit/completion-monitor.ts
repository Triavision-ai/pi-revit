import { canonicalJson } from "./contracts.js";

/**
 * Completion monitor (inv:completion-proportionate). Metadata-driven and tool-agnostic:
 * it only knows whether a call changed the model and whether an identical verification call
 * repeats after further changes within one run. A call's reported model_changes decide whether
 * it changed anything; declared write/effects are the fallback for results without that report,
 * so a read-only custom script is not counted as an edit. It steers
 * with a completion check appended to that result; it never blocks, so a legitimate
 * multi-step task is not stopped. Reset at the start of every user prompt.
 */
export interface CallMetadata { write?: boolean; effects?: string[] | null }

export function changesModel(meta: CallMetadata): boolean {
	return meta.write === true || (meta.effects ?? []).includes("model");
}

export function createCompletionMonitor(options: { threshold?: number } = {}) {
	const threshold = options.threshold ?? 3;
	let sequence = 0, lastChange = -1;
	const changes = new Map<string, number>();
	const seen = new Map<string, { at: number; repeats: number }>();
	return {
		/** Start of a new user request. */
		reset() { sequence = 0; lastChange = -1; changes.clear(); seen.clear(); },
		/**
		 * Record a successful call; returns a completion check to append, or null.
		 * `args` exclude transport-only retry metadata so identical checks compare equal.
		 */
		afterCall(tool: string, args: unknown, meta: CallMetadata, changed?: boolean): string | null {
			sequence++;
			if (changed ?? changesModel(meta)) {
				lastChange = sequence;
				changes.set(tool, (changes.get(tool) ?? 0) + 1);
				return null;
			}
			const { _operation_id: _retry, ...rest } = (args ?? {}) as Record<string, unknown>;
			const key = `${tool}\u0000${canonicalJson(rest)}`;
			const previous = seen.get(key);
			const afterEdits = previous !== undefined && lastChange > previous.at;
			const repeats = afterEdits ? previous.repeats + 1 : previous?.repeats ?? 0;
			seen.set(key, { at: sequence, repeats });
			// Only a re-check that follows new edits counts. The first one is a normal
			// before/after pair; from the threshold on, every `threshold`-th one steers.
			if (!afterEdits || repeats + 1 < threshold || (repeats + 1 - threshold) % threshold !== 0) return null;
			const total = [...changes.values()].reduce((sum, count) => sum + count, 0);
			const summary = [...changes].map(([name, count]) => `${name} ×${count}`).join(", ");
			return `PI-Revit completion check: this is check ${repeats + 1} of the same target after further changes in this request (${total} model-changing calls so far: ${summary}). `
				+ "Compare the current result with the request's explicit requirements. If they are met, stop and report what changed and how it was verified; offer any further improvements as suggestions instead of making them. "
				+ "If a requirement is still unmet, state which one and why before changing anything else. Do not hide or remove content the request asks to show.";
		},
		/** Model-changing calls recorded in this request, by tool. */
		ledger: () => Object.fromEntries(changes),
	};
}
