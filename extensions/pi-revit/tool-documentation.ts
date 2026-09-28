import { accessSync, constants, realpathSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import manifest from "../../skills/pi-revit/tool-manifest.json";
import { version as packageVersion } from "../../package.json";
import { packagedContracts } from "./contracts.js";

export interface DocumentedTool { name: string; source: "native" | "bridge"; group: string; summary: string; }
export interface GuidanceEntry { name: string; kind: string; path: string; summary: string; keywords?: string[] }
// This index is documentation metadata only. Live registration remains authoritative
// for input schemas, effects, tool availability and execution.
export const documentedTools = manifest.tools as DocumentedTool[];
export const packagedGuidance = manifest.guidance as GuidanceEntry[];
export const documentationRevision = manifest.documentation_revision as string;
const byName = new Map(documentedTools.map(tool => [tool.name, tool]));
const defaultRoot = fileURLToPath(new URL("../../skills/pi-revit/references/tools/", import.meta.url));
export const skillRoot = fileURLToPath(new URL("../../skills/pi-revit/", import.meta.url));
export const manualDirectory = defaultRoot;

/**
 * Compatibility compares the executable-contract hash of the selected bridge's live
 * descriptor with the hash the packaged manual was generated from:
 *   package_local     native tool documented and shipped with this extension
 *   contract_match    identical input schema and effects; the manual describes this contract
 *   contract_changed  the live contract differs; trust the active schema over the manual
 *   undocumented      the live tool has no packaged contract (newer bridge)
 *   unknown           no live contract observed (bridge not discovered)
 */
export interface LiveEvidence { contractHash?: string | null; bridgeVersion?: string | null }

/** Real-path containment keeps documentation lookup inside `root`; failures are nonfatal. */
export function readablePathWithin(root: string, relative: string): string | null {
	try {
		const base = realpathSync(root), real = realpathSync(path.resolve(root, relative));
		const inside = path.relative(base, real);
		if (inside && inside !== ".." && !inside.startsWith(`..${path.sep}`) && !path.isAbsolute(inside) && statSync(real).isFile()) {
			accessSync(real, constants.R_OK);
			return real;
		}
	} catch {
		// Missing, unreadable or concurrently replaced documentation must not
		// prevent tool registration or discovery. Do not read manual bodies here.
	}
	return null;
}

export function createDocumentationResolver(root = defaultRoot) {
	return (name: string, live: LiveEvidence = {}) => {
		const entry = byName.get(name);
		const contract = packagedContracts.get(name);
		const liveHash = live.contractHash ?? null;
		if (!entry) return {
			key: name, status: "missing", path: null, packaged_contract_hash: null, live_contract_hash: liveHash,
			compatibility: liveHash ? "undocumented" : "unknown",
		};
		const readablePath = readablePathWithin(root, `${entry.name}.md`);
		const packagedHash = contract?.contract_hash ?? null;
		return {
			key: entry.name, status: readablePath ? "available" : "missing", path: readablePath,
			revision: documentationRevision, package_version: packageVersion,
			packaged_contract_hash: packagedHash,
			live_contract_hash: entry.source === "bridge" ? liveHash : null,
			observed_bridge_version: entry.source === "bridge" ? live.bridgeVersion ?? null : null,
			compatibility: entry.source === "native" ? "package_local"
				: !liveHash ? "unknown"
				: !packagedHash ? "undocumented"
				: liveHash === packagedHash ? "contract_match" : "contract_changed",
		};
	};
}

export const resolveToolDocumentation = createDocumentationResolver();
