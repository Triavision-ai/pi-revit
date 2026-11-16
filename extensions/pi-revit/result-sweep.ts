import { lstat, readdir, rm } from "node:fs/promises";
import path from "node:path";

/** Saved large results stay readable by file_path after a reload or resume; folders whose
 * last write is older than this are removed at session start. */
export const RESULT_RETENTION_MS = 7 * 24 * 60 * 60 * 1000;

/** Session folders in pi-revit's own results folder are named <date>-<random>. A date prefix
 * identifies pi-revit only there: nothing else writes to that folder. */
export const WORKSPACE_RESULT_FOLDER = /^\d{4}-\d{2}-\d{2}-/;

/** The system temp folder is shared with every other program, so only pi-revit's own prefix
 * may match there: the fallback session folders, and where 0.5.2 and earlier saved results. */
export const SYSTEM_TEMP_RESULT_FOLDER = /^pi-revit-results-/;

/**
 * Removes saved-result folders in `directory` whose last write is older than
 * RESULT_RETENTION_MS. Only real folders whose name matches `pattern` are considered; links
 * and other entries are left alone. The caller names the pattern for each directory, so a
 * pattern that is safe in one place can never be applied to another by default. `keep` is
 * the folder of the running session and is never removed.
 */
export async function sweepOldResults(now: number, directory: string, pattern: RegExp, keep?: string): Promise<number> {
	let names: string[];
	try { names = await readdir(directory); } catch { return 0; }
	let removed = 0;
	for (const name of names) {
		if (!pattern.test(name)) continue;
		const folder = path.join(directory, name);
		if (keep && path.resolve(folder) === path.resolve(keep)) continue;
		try {
			const info = await lstat(folder);
			if (!info.isDirectory() || now - info.mtimeMs < RESULT_RETENTION_MS) continue;
			await rm(folder, { recursive: true, force: true });
			removed++;
		} catch {
			// Another Pi session may be removing or using it; leave it for the next sweep.
		}
	}
	return removed;
}
