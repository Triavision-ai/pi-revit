# set_parameters

## Purpose and boundaries

Write parameters or rename elements in the active host document. Use `Name` to rename levels, views, sheets, types, and other elements that support `Element.Name`; the implementation falls back to that property when the Name parameter is missing, read-only, or rejects the write. That fallback rejects a name another object of the same kind already uses (views of one type, levels, grids, types, materials, filters), reporting `name_collision` with the existing object's ID. This tool does not change element types; use `change_element_types` for that.

Contract: PI-Revit 0.5.0 source, [SetParameters.cs](../../../../src/Revit/Tools/SetParameters.cs). The current public schema is authoritative. This page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `20ad3893ff59afa6`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |
| `updates` | array of object | yes |  |  |
| `expected_document` | string | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Changing an element's type | Tool: change_element_types |
| Element properties that are not parameters, such as pinned state | Revit API: Element.Pinned. Check all its members in one call: `search_api_docs` with query `Element.Pinned`, then use `execute_csharp` within the requested scope. |
| Moving or rotating elements | Tool: transform_elements |
| Family parameters, family types and formulas in a family document | Revit API: FamilyManager.Types; FamilyManager.NewType; FamilyManager.Set; FamilyManager.AddParameter; FamilyManager.SetFormula. Check all its members in one call: `search_api_docs` with query `FamilyManager.Types; FamilyManager.NewType; FamilyManager.Set; FamilyManager.AddParameter; FamilyManager.SetFormula`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md) before a write or preview. Inspect targets and parameters with `get_element_details` first.

| Input | Meaning |
| --- | --- |
| `updates` | Required array of 1–200 objects with `element_id`, `parameter`, `value`, and optional `unit`. Each update has its own subtransaction. |
| `parameter` | Localized display name, language-independent `BuiltInParameter` name, or `guid:<GUID>` for a shared parameter. Prefer an exact identity if display names are missing or ambiguous. |
| `value` | Matches the parameter storage type: text, number, integer, element ID integer, or boolean for yes/no. Empty text clears a string parameter. Renaming requires nonempty text. |
| `unit` | For measured numeric values, an explicit compatible unit such as `millimeters`, `feet`, `squareMeters`, or `degrees`. Omission uses this document's display units for the parameter. Omit units for unitless values. |
| `preview` | Default `false`. Commit-validates accepted changes, then rolls back the enclosing transaction group. |
| `atomic` | Default `false`: valid updates can commit despite other failures. `true` rolls the whole batch back if any update fails. |
| `expected_document_id` | Required for every call, including previews: exact current `project.documentId` from `get_model_overview`. |
| `expected_document` | Optional additional title check; never replaces exact identity. |
| `_operation_id` | Optional extension argument only for an identical retry of the original request. Omit for a new operation. |

Type parameters live on the element type: pass its ID, and assess all affected instances before changing a shared type. `get_element_details` reports `builtInParameter`; for example, `ALL_MODEL_MARK` and `ALL_MODEL_INSTANCE_COMMENTS` avoid translated display names. A display name that matches several parameters on the element fails that update and lists their exact identities (`BuiltInParameter` name or `guid:<GUID>`); nothing is written to an arbitrary one. <!-- inv:parameter-ambiguity --> Retry with one of the listed identities.

## Example

Illustrative ID `12345` must be replaced with a discovered host element; replace the document placeholder with the exact current identity. This previews a complete batch without retaining changes.

```json
{
  "expected_document_id": "<project.documentId>",
  "preview": true,
  "atomic": true,
  "updates": [
    { "element_id": 12345, "parameter": "ALL_MODEL_INSTANCE_COMMENTS", "value": "Reviewed" }
  ]
}
```

## Results, recovery and verification

Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. `updated` counts committed steps, not distinct elements. Entries contain zero-based input `index`, observed `before`/`after`, and `newDisplayValue`; numeric parameter snapshots use internal units with separately formatted display values. Repeated writes to one parameter form a sequence, so an intermediate `after` is not a final-model snapshot. A proposal is not a retained change.

An atomic rejection can report proposals without reaching commit validation. Revit commit errors can roll back the whole batch; warnings are auto-dismissed and returned for reporting. Reread affected parameters after a committed change. Follow [visual verification](../visual-verification.md) when the values affect visible results.

For an uncertain timeout or cancellation, follow [operation recovery](../operation-recovery.md) before any repeat. A real write following a preview is a new operation because the arguments changed. This tool does not save the model or authorize additional edits.
