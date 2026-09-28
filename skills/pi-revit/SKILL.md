---
name: pi-revit
description: Inspect, edit, document, script, and export Autodesk Revit models using PI-Revit. Use for questions about the connected project, selecting Revit capabilities, or carrying out model and drawing workflows. Routes to focused tool manuals and execution guidance.
---

# PI-Revit

Choose the path that matches the user's request. This skill routes tasks; the always-present PI-Revit protocol states the capability, completion, evidence, identity and language rules for every tool. Executable schemas and bridge checks remain authoritative. Guidance does not grant permission for additional edits, exports, model saving, or deployment.

## Choose the task path

- **Explain or plan:** answer from relevant Revit knowledge and tool manuals. A general Revit explanation does not require an open model. `find_revit_tools` with `scope: "documentation"` can find packaged manuals without connecting to Revit. Connect only when the answer depends on the actual project. Do not turn a question into a modification.
- **Inspect:** select the intended session and read the requested model scope. Use `get_elements` for listings or `count_only: true` for a simple count. An audit does not authorize repairs, selection changes, or exports.
- **Modify or produce a deliverable:** establish model identity and scope, inspect the existing state, read the relevant manuals, then execute and verify requested work. Use preview and atomic behavior where they help validate a proposed change. A successful transaction does not save the Revit file.

## Essential rules

1. Before working against a model, read [execution rules](references/execution-rules.md). For several Revit sessions or a stale selection, use [manage_revit_instances](references/tools/manage_revit_instances.md). Select the intended model; do not substitute whichever document happens to be active.
2. Obtain `project.documentId` from `get_model_overview` before edits and pass it unchanged as `expected_document_id`, including previews. The same guard applies to export, view activation, selection changes, and every `manage_sheet_placements` action. Refresh after reopening the document, restarting Revit, or changing instances. A title is insufficient.
3. Discover dedicated capabilities with [find_revit_tools](references/tools/find_revit_tools.md) before custom code; search with English task words. Specialist tools begin inactive. Registration, activation, and reading a manual are separate actions: read the relevant returned manual, and inspect the activated public schema before calling the tool. Use the [tool index](references/tool-index.md) for all public tools and direct manual links; do not load every manual.
4. Each tool declares what it does not cover and what to use instead. When a tool or manual does not cover the request, follow that alternative. When nothing dedicated fits, verify the API members with [search_api_docs](references/tools/search_api_docs.md) and use [execute_csharp](references/tools/execute_csharp.md) within the requested scope. Report an operation as not possible only after that check, naming what you checked and whether the gap is a missing tool, the Revit API, or a user decision. <!-- inv:capability-claims-checked -->
5. Discover IDs and parameter identities from the model. Display names are localized; prefer discovered `BuiltInParameter` names or shared GUIDs where supported. Respect each tool's units and coordinate frame; raw measurements commonly use internal feet.
6. Check `committed`, `succeeded`, `proposed`, `failed`, warnings, and commit-validation status. Rolled-back proposals are not retained changes. Discard preview-created or replacement IDs after rollback; reuse only committed IDs.
7. For large responses, use [read_revit_result](references/tools/read_revit_result.md) and follow every continuation needed for the requested result. Saved-result fragments and the original tool's query pages are separate layers.
8. For a timeout, cancellation, uncertain outcome, or incomplete result, read [operation recovery](references/operation-recovery.md) before repeating an action. Check the original receipt. Only an identical request with its original `_operation_id` is a deduplicated retry.
9. For visible changes or graphical deliverables, read [visual verification](references/visual-verification.md), capture the actual result, and inspect the image. Correct only what breaks the request; once its requirements are verified, stop and report, offering further improvements as suggestions. Report blocked verification honestly. Evidence capture does not authorize saving the model.

## Read the detail needed for this task

| Need | Read |
| --- | --- |
| Choose among tools; inspect input/output and effects | [Tool index and individual manuals](references/tool-index.md) |
| Room plans/sections, tags, schedules, sheets | [Room documentation workflow](references/room-documentation.md) |
| Multi-step model audit, data review, and requested exports | [Model audit and export workflow](references/model-audit-export.md) |
| No dedicated tool covers the operation, or an unfamiliar Revit API member | The tool's declared alternative; else [search_api_docs](references/tools/search_api_docs.md), then [execute_csharp](references/tools/execute_csharp.md) |
| Save, inspect, or run a reusable script | [manage_revit_scripts](references/tools/manage_revit_scripts.md) |
| Bridge unavailable or version mismatch | [ping](references/tools/ping.md) and [operation recovery](references/operation-recovery.md) |

The bridge targets Revit 2025–2027. Manuals describe this package's contract; check the selected bridge's schema and version. `ping` and API search need no open document. Saved results and local script-library inspection work without Revit. General modeling standards and the future Revit subject library are separate from these tool contracts.
