import { readFile, readdir } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { createHash } from "node:crypto";

export interface BridgeInfo {
    baseUrl: string;
    token: string;
    pid?: number;
    revitVersion?: string;
    bridgeId?: string;
    supportsOperationTracking?: boolean;
}

/** Selection belongs to one extension instance. A selected session never silently falls back. */
export function createInstanceRouter(readLegacy: () => Promise<BridgeInfo>, probe: (info: BridgeInfo) => Promise<Record<string, unknown>>) {
    let selected: string | undefined;
    // Older bridges have no generation ID. Derive a stable opaque selector from
    // their per-start credentials without exposing the credential itself.
    const identity = (info: BridgeInfo) => info.bridgeId ?? createHash("sha256").update(info.baseUrl + "\0" + info.token).digest("hex").slice(0, 32);
    const directory = () => path.join(process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"), "RevitBridge", "instances");
    async function candidates(): Promise<BridgeInfo[]> {
        const entries = new Map<string, BridgeInfo>();
        let files: string[] = [];
        try { files = (await readdir(directory())).filter(name => /^[0-9a-f]{32}\.json$/.test(name)); } catch { }
        // Old crash records may accumulate; probe only records whose process still exists.
        for (const file of files) {
            try {
                const info = JSON.parse(await readFile(path.join(directory(), file), "utf8")) as BridgeInfo;
                if (info.bridgeId !== file.slice(0, -5) || !info.baseUrl || !info.token || !Number.isSafeInteger(info.pid)) continue;
                try { process.kill(info.pid!, 0); } catch { continue; }
                entries.set(info.bridgeId, info);
            } catch { }
        }
        try {
            const legacy = await readLegacy();
            entries.set(identity(legacy), legacy);
        } catch { }
        return [...entries.values()];
    }
    async function live() {
        const found = await Promise.all((await candidates()).map(async info => {
            try {
                const ping = await probe(info);
                if (info.bridgeId && ping.bridgeId !== info.bridgeId) return null;
                return { info, ping };
            } catch { return null; }
        }));
        return found.filter((entry): entry is NonNullable<typeof entry> => entry !== null);
    }
    async function resolve(operationId?: string): Promise<BridgeInfo> {
        const target = operationId?.split(":")[0] ?? selected;
        if (target) {
            const info = (await candidates()).find(entry => identity(entry) === target);
            if (!info) throw new Error("The original or selected Revit bridge session is unavailable. Its outcome is unknown here; no action was sent to another instance. Use manage_revit_instances to select an available session.");
            return info;
        }
        const entries = await candidates();
        // Bind only a verified live session; a crash record must not prevent
        // startup discovery from recovering when Revit is launched later.
        if (entries.length === 1) {
            const ping = await probe(entries[0]);
            if (entries[0].bridgeId && ping.bridgeId !== entries[0].bridgeId)
                throw new Error("Revit discovery points to a different bridge generation. Refresh the instance list.");
            selected = identity(entries[0]); return entries[0];
        }
        const available = await live();
        if (available.length > 1) throw new Error("Several Revit instances are open. Use manage_revit_instances to list and select the intended bridge_id before calling model tools.");
        if (available.length === 1) { selected = identity(available[0].info); return available[0].info; }
        return readLegacy();
    }
    async function list() {
        return (await live()).map(({ info, ping }) => ({ bridge_id: identity(info), pid: info.pid ?? null,
            revit_version: info.revitVersion ?? null, addin_version: ping.addinVersion ?? null,
            selected: selected === identity(info), supports_operation_tracking: info.supportsOperationTracking === true }));
    }
    async function select(id: string) {
        if (!/^[0-9a-f]{32}$/.test(id)) throw new Error("bridge_id must be an exact session ID from manage_revit_instances.");
        const match = (await live()).find(entry => identity(entry.info) === id);
        if (!match) throw new Error("That bridge session is no longer reachable; selection was unchanged.");
        selected = id;
        return { bridge_id: id, pid: match.info.pid, addin_version: match.ping.addinVersion,
            instructions: "Selection applies to this Pi extension session. Read get_model_overview for a fresh exact document identity before editing. Operation receipts and identical retries are routed to their original bridge session." };
    }
    return { resolve, list, select };
}
