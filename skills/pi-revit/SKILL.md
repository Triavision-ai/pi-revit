---
name: pi-revit
description: Work with the open Autodesk Revit model through the Revit bridge tools (ping, get_model_overview, get_elements, get_element_details, get_element_types, manage_selection, open_view, set_parameters, search_api_docs, execute_csharp, capture_view, export_documents, get_model_health) and retrieve saved results with read_revit_result. Use when the user asks about the Revit project, its elements, parameters, selection, or wants to change, script, capture, or export the model.
---

# Revit

Work with the live Revit model. The bridge targets Revit 2025, 2026, and 2027; the 0.3.0 changes were tested live on Revit 2025. Bridge document tools require Revit running with a project open; `ping` and `search_api_docs` work without a document. `read_revit_result` reads an already saved result locally without contacting Revit.

## Tool selection

| Task | Tool |
|------|------|
| Bridge alive? Which Revit version? | `ping` |
| Orientation: project info, units, levels, grids, category counts | `get_model_overview` |
| List or count elements of ANY category (walls, doors, rooms, sheets, views, ...) | `get_elements` |
| Read parameter VALUES, location, bounding box, materials of specific elements | `get_element_details` |
| List element types / family symbols; "used vs merely loaded" | `get_element_types` |
| Read or change the user's selection; zoom; temporary isolate | `manage_selection` |
| Put a view or sheet on the user's screen (activate it) | `open_view` |
| Write parameter values; rename anything (levels, views, sheets, types) | `set_parameters` |
| Look up Revit API classes/members/signatures | `search_api_docs` |
| Everything else (create, delete, move, views, sheets, tagging, ...) | `execute_csharp` |
| PNG snapshot of a view (visual QA) | `capture_view` (advanced) |
| PDF/DWG/PNG/IFC file export | `export_documents` (advanced) |
| Warnings / model quality audit | `get_model_health` (advanced) |
| Continue a saved large tool result | `read_revit_result` (local, no Revit call) |

Workflow guidance:

- Call `get_model_overview` for the intended open model before changing it. Copy `project.documentId` unchanged into `expected_document_id` for `set_parameters`, `execute_csharp`, `export_documents`, `open_view`, and selection/zoom changes. `manage_selection` also requires it whenever `isolate_in_view: true`, even with action `get`. Pure reads may omit it; a supplied ID is always checked. A matching legacy `expected_document` title alone is insufficient.
- The ID belongs to one open document in one bridge session. Refresh it after closing/reopening the model or restarting Revit. If the guard rejects a call, activate the intended model and obtain its overview again; do not blindly substitute the currently active model's ID.
- `get_elements` is the listing/counting primitive (`count_only: true` for bare counts). It returns identity fields only (id, name, category, typeName, levelId); read parameter values with `get_element_details`. Prefer a `category` or `of_class` scope when filtering by a parameter's display name.
- The selection pipeline is `get_elements` -> ids -> `manage_selection` (action `set`); there is no inline filter on selection.
- `set_parameters` handles bulk parameter writes and renames (the `Name` parameter covers levels, views, sheets, types). It can commit a partially successful batch: inspect every failed update, `commitWarnings`, and the reported transaction outcome before describing what changed.
- Parameter display names are LOCALIZED: in a non-English Revit UI, `Mark`, `Comments`, and every other display name appear under their translated names. When a display-name lookup or `parameter_names` filter finds nothing, or the document may be non-English, use the language-independent `BuiltInParameter` enum name instead (e.g. `ALL_MODEL_MARK` for Mark, `ALL_MODEL_INSTANCE_COMMENTS` for Comments) — `set_parameters`, `get_element_details.parameter_names`, and `get_elements` filter rules all accept them, and `get_element_details` reports each parameter's `builtInParameter` name for discovery.
- Before writing `execute_csharp` code, verify unfamiliar classes/members with `search_api_docs` (works with no document open; first query builds the index and takes a few seconds). The top match carries its remarks, parameter docs, and returns inline, and every public API enum value is searchable — trust the result over guessing or web search; narrow the query to promote a different match into the top slot.
- `export_documents` defaults to `Documents\pi-revit\Models\<model title>--<identity hash>\exports`. Use its returned `outputDir` and file paths as authoritative; do not construct a destination from the title or opaque `project.documentId`. Saved paths and cloud/server identities determine the folder; unsaved/unavailable identities use a session fallback. Save As can select a new folder. Pass `output_dir` when the user requests a different destination. Existing title-only folders remain untouched.
- Large tool payloads return a `result_id`, `file_path`, and continuation instructions. Call `read_revit_result` with that ID and `offset: 0`, then follow `next_offset` until `has_more` is false. Concatenate `text` fragments in order; each fragment is not a standalone JSON result. Offsets count UTF-16 code units. IDs last for the current extension instance; after reload, use the returned absolute file path with `read` while the file exists. Retrieval does not replace the original query's pagination or remove its limits.

## execute_csharp playbook

- Globals: `doc` (Document), `uidoc` (UIDocument), `uiapp` (UIApplication), and `Dump(value)` to record intermediates into the result's `dumps[]`.
- The script runs inside one backend-owned transaction. Do not start another transaction on `doc` (sub-transactions are allowed). The tool checks commit status and attempts rollback on script failure; read its actual outcome instead of assuming rollback succeeded. Result projection can fail after a successful commit and report `returnValueError`. Filesystem and UI effects are separate from model rollback.
- Scripts must be fully synchronous: `await`/`async` is rejected at compile time; never block on `Task.Result`/`.Wait()`.
- Return primitives, strings, or anonymous objects/lists; raw Revit API objects are projected to compact shapes (Element -> `{id,name,category,typeName,levelId}`, ElementId -> number, XYZ -> `{x,y,z}`).
- Lengths are internal units (decimal feet) — convert with `UnitUtils.ConvertToInternalUnits`/`ConvertFromInternalUnits`.
- Common pitfalls: call `FamilySymbol.Activate()` before `NewFamilyInstance`; use collector-level filtering (`OfCategory`/`OfClass`/`WhereElementIsNotElementType`) and bounded loops. The budget is 120s and Revit cannot be interrupted mid-script. The dialog guard attempts dismissive responses and reports `suppressedDialogs`; it cannot guarantee handling every modal dialog.
- `capture_view` returns a `filePath` to a temp PNG, never image data — open it with the read tool to actually see it.

## Failure modes

- **Identity rejected**: no tool action was performed. Verify the intended active model, refresh `project.documentId`, and pass `expected_document_id` unchanged.
- **Partial UI/export effects**: selection or zoom can already have changed when isolation fails. Export errors can leave incomplete files, and IFC commit warnings are returned. Inspect the reported effects and output paths; model rollback does not remove files or reverse earlier UI actions.
- **Large-result save failed**: Revit may already have completed the operation. Verify its effects before retrying a write.
- **Bridge not reachable** ("Revit bridge is not available" / "Could not reach the Revit bridge"): Revit is not running or the add-in did not load. Ask the user to start Revit, then retry `ping`.
- **HTTP 409 / "No active Revit document is open."** (`hasActiveDocument: false`): Revit is running but no project is open. Ask the user to open a project, then retry. This fails immediately; do not wait or retry blindly.
- **Timeout** ("Revit did not answer within Ns", 30s default / 120s for execute_csharp, capture_view, export_documents): Revit is busy or showing a modal dialog. An already-started tool still runs to completion in Revit — verify model state (e.g. `get_elements`) before re-issuing a write.
- **Cancelled**: same caveat — the bridge cannot abort queued or running work, so verify model state before retrying a write tool.

## Upgrading from 0.2.x

Version 0.3.0 requires exact document IDs for the operations above. Update the Pi package and deploy the matching add-in with Revit closed, then restart Revit and start a fresh Pi session so the new schemas and instructions are loaded. Obtain a fresh overview before writes. `ping` reports an installed/loaded version mismatch. Setup preserves existing workspace `AGENTS.md`; merge these targeting, result-reading, and folder rules into an older workspace's instructions when needed.
