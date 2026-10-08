# get_model_overview

## Purpose and preconditions

Orient work on the active model: project metadata, document identity, display units, levels, grids, major category counts, and totals. This core tool requires a reachable selected Revit session with an active document. Select the intended session first; the tool does not switch documents or instances.

Call it for the intended model before model changes: pi-revit binds the following changes to that model. Pure explanation tasks do not need a model overview. See [execution rules](../execution-rules.md).

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `b8ec894975d2efe0`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Switching the active document or Revit session | Tool: manage_revit_instances |
<!-- generated:contract:end -->

## Public inputs

There are no tool-specific inputs.

- Optional `_operation_id`: exact previous operation ID for an identical retry; omit for a new overview.

## Example

```json
{}
```

## Results and verification

- `project` includes title, opaque `documentId` and its short form `shortDocumentId` (both for clients that call the bridge directly), project name/number/client/address, file path, Revit version, and display units. `documentId` is an opaque identity of the open document that pi-revit uses automatically; it is not a persistent project ID. Call the overview again after a model is closed and reopened or after a bridge restart.
- `project.documentKind` is `project` or `family`. For a family, `project.family` gives its category (localized name and `builtInCategory`), current type, type names and parameters (name, instance or type, formula). Project-only tools refuse a family document before running; edit family types, parameters and formulas through `FamilyManager` with `execute_csharp`.
- `levels` includes IDs, names, raw `elevation_ft` in internal feet, and formatted `elevation_display`. `grids` includes IDs and names.
- `counts` covers the tool's predefined major categories, not every possible category. Category count exceptions are represented as zero; use a focused `get_elements` query to investigate a surprising count.
- `totals.elements` counts non-type host elements; `totals.views` excludes templates and sheets; sheets, levels, and grids also have totals. These are different populations and should not be added together.

No query pagination is exposed. If the extension saves a large response, finish its separate `read_revit_result` continuation. Confirm project metadata before a write, export, or UI change. An overview does not establish visual quality or a complete model audit.

## Effects and recovery

Read-only: no model edit, document activation, selection change, save, or export. No active document produces an immediate error. When a later change is refused because another model is active, the model was reopened, or Revit restarted, call this tool again and ask the user if the active model is not the intended one. See [operation recovery](../operation-recovery.md) for bridge/timeout failures.

## Compatibility

Source reference: PI-Revit 0.5.0, [GetModelOverview.cs](../../../../src/Revit/Tools/GetModelOverview.cs), plus registry document identity and extension retry inputs. Source-reviewed for the 2025–2027 bridge targets; no new live validation is claimed.
