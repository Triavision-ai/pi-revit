---
name: pi-revit
description: Work with the open Autodesk Revit model through the Revit bridge tools (ping, get_model_overview, get_elements, get_element_details, get_element_types, manage_selection, open_view, set_parameters, search_api_docs, execute_csharp, capture_view, export_documents, get_model_health). Use when the user asks about the Revit project, its elements, parameters, selection, or wants to change, script, capture, or export the model.
---

# Revit

Work with the live Revit model. The tools call a headless bridge add-in inside Revit (2025, 2026, or 2027); Revit must be running with a project open (only `ping` and `search_api_docs` work without a document).

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

Workflow guidance:

- Call `get_model_overview` first when starting work on an unfamiliar model — one call returns project metadata, units, levels, grids, and category counts.
- `get_elements` is the listing/counting primitive (`count_only: true` for bare counts). It returns identity fields only (id, name, category, typeName, levelId); read parameter values with `get_element_details`. Prefer a `category` or `of_class` scope when filtering by a parameter's display name.
- The selection pipeline is `get_elements` -> ids -> `manage_selection` (action `set`); there is no inline filter on selection.
- `set_parameters` is the home for bulk parameter writes AND renames (the `Name` parameter covers levels, views, sheets, types). One transaction per batch; per-element failures are reported. Pass `expected_document` (the model title) when several models are open or the session is long — it makes the write fail cleanly instead of landing in a different active document.
- Parameter display names are LOCALIZED: in a non-English Revit UI, `Mark` is `Kennzeichen` (German), `マーク` (Japanese), etc. When a display-name lookup or `parameter_names` filter finds nothing, or the document may be non-English, use the language-independent `BuiltInParameter` enum name instead (e.g. `ALL_MODEL_MARK` for Mark, `ALL_MODEL_INSTANCE_COMMENTS` for Comments) — `set_parameters`, `get_element_details.parameter_names`, and `get_elements` filter rules all accept them, and `get_element_details` reports each parameter's `builtInParameter` name for discovery.
- Before writing `execute_csharp` code, verify unfamiliar classes/members with `search_api_docs` (works with no document open; first query builds the index and takes a few seconds). The top match carries its remarks, parameter docs, and returns inline, and every public API enum value is searchable — trust the result over guessing or web search; narrow the query to promote a different match into the top slot.
- `export_documents` files its output under `Documents\pi-revit\Models\<model title>\exports` automatically when `output_dir` is omitted — keyed to the exported document, so it lands right even across many models. Pass `output_dir` only when the user names a different target.

## execute_csharp playbook

- Globals: `doc` (Document), `uidoc` (UIDocument), `uiapp` (UIApplication), and `Dump(value)` to record intermediates into the result's `dumps[]`.
- The transaction is automatic: the whole script runs inside ONE backend-owned transaction — committed on success, rolled back on any exception. Do not open your own `Transaction` (sub-transactions are fine).
- Scripts must be fully synchronous: `await`/`async` is rejected at compile time; never block on `Task.Result`/`.Wait()`.
- Return primitives, strings, or anonymous objects/lists; raw Revit API objects are projected to compact shapes (Element -> `{id,name,category,typeName,levelId}`, ElementId -> number, XYZ -> `{x,y,z}`).
- Lengths are internal units (decimal feet) — convert with `UnitUtils.ConvertToInternalUnits`/`ConvertFromInternalUnits`.
- Common pitfalls: call `FamilySymbol.Activate()` before `NewFamilyInstance`; use collector-level filtering (`OfCategory`/`OfClass`/`WhereElementIsNotElementType`) and bounded loops — the budget is 120s and Revit cannot be interrupted mid-script; modal dialogs are auto-dismissed and reported in `suppressedDialogs`.
- `capture_view` returns a `filePath` to a temp PNG, never image data — open it with the read tool to actually see it.

## Failure modes

- **Bridge not reachable** ("Revit bridge is not available" / "Could not reach the Revit bridge"): Revit is not running or the add-in did not load. Ask the user to start Revit, then retry `ping`.
- **HTTP 409 / "No active Revit document is open."** (`hasActiveDocument: false`): Revit is running but no project is open. Ask the user to open a project, then retry. This fails immediately; do not wait or retry blindly.
- **Timeout** ("Revit did not answer within Ns", 30s default / 120s for execute_csharp, capture_view, export_documents): Revit is busy or showing a modal dialog. An already-started tool still runs to completion in Revit — verify model state (e.g. `get_elements`) before re-issuing a write.
- **Cancelled**: same caveat — the bridge cannot abort queued or running work, so verify model state before retrying a write tool.
