import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { randomUUID } from "node:crypto";
import path from "node:path";
import os from "node:os";
import { createScriptLibrary, hash, kinds } from "./script-store.js";
import { stringChoice } from "./string-choice.js";

// The storage half (versions and run records on disk) lives in script-store.ts.
export { createScriptLibrary } from "./script-store.js";

interface Receipt { operation_id: string; bridge_id: string; }
type Result = { content: { type: "text"; text: string }[]; details?: unknown };
type Runner = (args: Record<string, unknown>, signal: AbortSignal | undefined, prepared: (receipt: Receipt) => Promise<void>) => Promise<Result>;

export function registerScriptLibrary(pi: ExtensionAPI, run: Runner, render: (value: unknown) => Promise<Result>,
    documentIdentity: () => string = () => {
        throw new Error("Call get_model_overview for the intended model first.");
    }) {
    const library = createScriptLibrary(path.join(process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming"), "pi-revit", "scripts"));
    pi.registerTool({
        name: "manage_revit_scripts", label: "Manage Revit Scripts",
        description: "Save, list, read, run and inspect local reusable C# scripts. Saving never executes code. Versions are immutable content hashes; read a version before explicitly running that exact hash. Every declared input is required; extra inputs are rejected. input_types validates top-level JSON kinds only; scripts validate nested contents and domain rules. Runs use execute_csharp with separate inputs JsonElement on the model of the latest get_model_overview, with operation receipts. Scripts have the same unrestricted model/UI/file/external effects and transaction behavior as execute_csharp. Local history records version, document, input hash and receipt, not input values or results. After interruption, inspect get_revit_operation before retrying with the same _operation_id and identical inputs. No automatic runs or model saving.",
        parameters: Type.Object({
            action: stringChoice(["list", "save", "read", "run", "history"] as const),
            name: Type.Optional(Type.String()), version: Type.Optional(Type.String()), description: Type.Optional(Type.String({ maxLength: 2000 })),
            code: Type.Optional(Type.String({ minLength: 1, maxLength: 100000 })),
            input_types: Type.Optional(Type.Record(Type.String(), stringChoice(kinds))),
            inputs: Type.Optional(Type.Record(Type.String(), Type.Unknown())), _operation_id: Type.Optional(Type.String()),
            offset: Type.Optional(Type.Integer({ minimum: 0 })), limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 100 })),
        }),
        executionMode: "sequential",
        async execute(_id, args, signal) {
            const offset = Math.max(0, args.offset ?? 0), limit = Math.max(1, Math.min(100, args.limit ?? 20));
            if (args.action === "save") return render(await library.save(args));
            if (args.action === "list") return render(await library.list(args.name, offset, limit));
            if (args.action === "history") return render(await library.readHistory(args.name, offset, limit));
            if (args.action !== "read" && args.action !== "run") throw new Error("Unknown script library action.");
            const saved = await library.read(args.name, args.version);
            if (args.action === "read") return render(saved);
            const expectedDocumentId = documentIdentity();
            const inputs = library.validateInputs(saved, args.inputs ?? {});
            const record: Record<string, unknown> = { run_id: randomUUID(), name: saved.name, version: saved.version, started_at: new Date().toISOString(), expected_document_id: expectedDocumentId, inputs_hash: hash(inputs), state: "prepared" };
            let dispatched = false;
            try {
                const result = await run({ code: saved.code, inputs, expected_document_id: expectedDocumentId, ...(args._operation_id !== undefined ? { _operation_id: args._operation_id } : {}) }, signal, async receipt => {
                    Object.assign(record, receipt); await library.writeHistory(record); dispatched = true;
                });
                Object.assign(record, { state: "response_received", completed_at: new Date().toISOString() });
                try { await library.writeHistory(record); }
                catch { result.content.push({ type: "text", text: "The script response was received, but the final local history update failed. Use the operation receipt for its outcome." }); }
                return result;
            } catch (error) {
                if (dispatched) {
                    Object.assign(record, { state: "outcome_unconfirmed", completed_at: new Date().toISOString() });
                    try { await library.writeHistory(record); } catch { /* Preserve the original error and operation ID. */ }
                }
                throw error;
            }
        },
    });
}
