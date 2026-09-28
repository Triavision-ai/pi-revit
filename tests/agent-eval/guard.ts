// Evaluation guard, loaded with -e next to the extension under test. It records every
// tool call and result and blocks only actions outside the scenario's policy; it never
// rewrites inputs. Policies:
//   explain: model writes, UI changes, exports and custom code are blocked.
//   read:    model writes, UI changes and exports are blocked; custom code is allowed and audited.
//   write:   changes allowed on the fixture; saving, closing/opening documents, exports,
//            printing, processes and file deletion inside custom code are blocked.
// Every policy: only the fixture's exact document identity; no credential-file reads;
// the user's persistent script library is not changed.
import { appendFileSync } from "node:fs";
import path from "node:path";

const WRITE_TOOLS = new Set(["set_parameters", "transform_elements", "delete_elements", "change_element_types",
  "create_tags", "manage_views", "manage_sheets", "manage_schedules", "export_documents"]);
const SENSITIVE = [/[\\/]\.pi[\\/]agent[\\/]auth\.json$/i, /[\\/]RevitBridge[\\/]bridge\.json$/i, /[\\/]RevitBridge[\\/]instances[\\/]/i];
const CSHARP_FORBIDDEN = /\.\s*Save(As)?\s*\(|\.\s*Close\s*\(|SynchronizeWithCentral|OpenAndActivateDocument|OpenDocumentFile|Process\s*\.\s*Start|File\s*\.\s*(Delete|Move|WriteAll)|Directory\s*\.\s*Delete|\.\s*Export\s*\(|\.\s*Print\s*\(/;

export default function evalGuard(pi: any) {
  const audit = process.env.PI_EVAL_AUDIT!;
  const policy = process.env.PI_EVAL_POLICY!;
  const fixtureId = process.env.PI_EVAL_DOCUMENT_ID!;
  if (!audit || !policy || !fixtureId) throw new Error("evaluation guard misconfigured");
  const record = (value: object) => appendFileSync(audit, JSON.stringify({ at: new Date().toISOString(), ...value }) + "\n", "utf8");

  pi.on("session_start", (_e: unknown, ctx: any) => record({ event: "session_start", policy,
    model: ctx.model ? { id: ctx.model.id, provider: ctx.model.provider } : null,
    thinking: typeof pi.getThinkingLevel === "function" ? pi.getThinkingLevel() : null, active_tools: pi.getActiveTools() }));

  pi.on("tool_call", (event: any) => {
    const name: string = event.toolName;
    const input = event.input ?? {};
    let reason: string | null = null;
    if (typeof input.expected_document_id === "string" && input.expected_document_id !== fixtureId)
      reason = "Evaluation guard: only the disposable fixture's exact document identity is permitted.";
    else if (["read", "grep", "find", "ls"].includes(name)) {
      const target = String(input.path ?? input.file_path ?? "").replace(/^@/, "");
      if (target && SENSITIVE.some(pattern => pattern.test(path.resolve(target)))) reason = "Evaluation guard: credential and bridge-token files are not readable.";
    } else if (name === "manage_revit_scripts" && ["save", "run"].includes(input.action))
      reason = "Evaluation guard: the user's persistent script library is not changed or executed.";
    else if (name === "manage_revit_instances" && input.action === "select" && policy === "explain")
      reason = "Evaluation guard: explanation scenarios do not switch Revit sessions.";
    else if (policy === "explain" || policy === "read") {
      const uiChange = name === "open_view" || (name === "manage_selection" && ((input.action ?? "get") !== "get" || input.isolate_in_view === true || input.zoom === true));
      const placementEdit = name === "manage_sheet_placements" && input.action !== "list";
      if (WRITE_TOOLS.has(name) || uiChange || placementEdit) reason = `Evaluation guard: ${policy} scenarios block model changes, UI changes and exports.`;
      else if (policy === "explain" && (name === "execute_csharp" || name === "capture_view")) reason = "Evaluation guard: explanation scenarios block script execution and captures.";
    } else if (policy === "write") {
      if (name === "execute_csharp" && CSHARP_FORBIDDEN.test(String(input.code ?? "")))
        reason = "Evaluation guard: saving, closing/opening documents, exports, printing, processes and file deletion are blocked.";
      if (name === "export_documents") reason = "Evaluation guard: exports are outside evaluation scenarios.";
    }
    record({ event: "tool_call", id: event.toolCallId, tool: name, input, allowed: reason === null, ...(reason ? { reason } : {}) });
    if (reason) return { block: true, reason };
  });

  pi.on("tool_result", (event: any) => {
    const text = Array.isArray(event.content) ? event.content.filter((c: any) => c?.type === "text").map((c: any) => c.text).join("\n") : "";
    // Manuals quote the note wording; only Revit tool results carry real notes.
    const revitResult = !["read", "grep", "find", "ls"].includes(event.toolName);
    record({ event: "tool_result", id: event.toolCallId, tool: event.toolName, is_error: event.isError === true, text_chars: text.length, text_head: text.slice(0, 6000),
      completion_check: revitResult && /PI-Revit completion check: /.test(text), scope_note: revitResult && /PI-Revit scope note: /.test(text),
      // Bridge-reported state only: custom code may print these words itself.
      inherited_state: revitResult && event.toolName !== "execute_csharp" ? /"inherited_state"/.test(text) : /"new_views"/.test(text) });
  });
}
