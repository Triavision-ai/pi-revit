import { setTimeout as delay } from "node:timers/promises";

export interface OperationProgress {
	state: string;
	elapsed_ms: number;
}

/** Wait only on the original receipt. This helper never dispatches a model action. */
export async function waitForOperation(
	read: (signal: AbortSignal | undefined, timeoutMs: number) => Promise<unknown>,
	waitMs = 0,
	signal?: AbortSignal,
	onProgress?: (progress: OperationProgress) => void,
	clock: () => number = () => performance.now(),
	pause: (ms: number, signal?: AbortSignal) => Promise<void> = (ms, signal) => delay(ms, undefined, { signal }),
): Promise<unknown> {
	if (!Number.isInteger(waitMs) || waitMs < 0 || waitMs > 60_000)
		throw new Error("wait_ms must be an integer from 0 to 60000.");
	signal?.throwIfAborted();
	if (waitMs === 0) return read(signal, 10_000);
	const start = clock();
	const deadline = start + waitMs;
	let latest: unknown;
	let lastUpdate = -Infinity;
	let lastState: string | undefined;
	for (;;) {
		signal?.throwIfAborted();
		const remaining = Math.ceil(deadline - clock());
		if (remaining <= 0 && latest !== undefined) return latest;
		try {
			latest = await read(signal, Math.max(1, Math.min(10_000, remaining)));
		} catch (error) {
			// A status request can time out before the overall wait budget. Return
			// the last known unfinished receipt; never turn transport uncertainty
			// into completion, or swallow other network/HTTP failures.
			signal?.throwIfAborted();
			if (latest !== undefined && (error as { name?: string })?.name === "TimeoutError") return latest;
			throw error;
		}
		signal?.throwIfAborted();
		const state = (latest as { state?: unknown } | null)?.state;
		if (state !== "queued" && state !== "running") return latest;
		const now = clock();
		if (state !== lastState || now - lastUpdate >= 5_000) {
			onProgress?.({ state, elapsed_ms: Math.max(0, Math.round(now - start)) });
			lastState = state;
			lastUpdate = now;
		}
		const rest = deadline - clock();
		if (rest <= 0) return latest;
		await pause(Math.min(2_000, rest), signal);
	}
}
