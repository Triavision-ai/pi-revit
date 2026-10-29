# place_family_instances

## Purpose and boundaries

Place a short batch of loaded, unhosted, one-level-based family symbols at explicit points in a project document, using Revit's `StructuralType.NonStructural` placement mode. Discover symbol IDs with `get_element_types` and level IDs with `get_elements` or `get_model_overview`. Hosted, face-based, work-plane, adaptive, curve-driven and structurally classified placements require a verified API-specific path. This tool does not load families, create types, set parameters or save the model.

Contract: PI-Revit 0.7.0 source, [PlaceFamilyInstances.cs](../../../../src/Revit/Tools/PlaceFamilyInstances.cs). Activate through `find_revit_tools`. The live schema and selected bridge version are authoritative.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `77ad9d8dfd581c32`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `instances` | array of object | yes |  |  |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Hosted, face-based, work-plane, adaptive, curve-driven and structural placement | Revit API: Family.FamilyPlacementType; Document.Create.NewFamilyInstance. Check all its members in one call: `search_api_docs` with query `Family.FamilyPlacementType; Document.Create.NewFamilyInstance`, then use `execute_csharp` within the requested scope. |
| Loading missing families or creating types | Revit API: Document.LoadFamily; FamilySymbol.Duplicate. Check all its members in one call: `search_api_docs` with query `Document.LoadFamily; FamilySymbol.Duplicate`, then use `execute_csharp` within the requested scope. |
| Setting instance parameters after creation | Tool: set_parameters |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md) and bind the intended model with `get_model_overview`. `instances` contains 1–100 rows, each with a loaded `family_symbol_id`, `level_id`, and absolute document internal XYZ `point`. `unit` applies to every point. All input shapes, units and finite values are checked before editing. A row with a missing symbol, missing level or unsupported family placement kind fails in its own subtransaction. An inactive valid symbol is activated in the same transaction as placement.

`preview: true` commit-validates then rolls back. `atomic: true` rolls back the whole batch if any row fails. The default permits partial success. An original `_operation_id` is only for recovering an identical uncertain request; consult [operation recovery](../operation-recovery.md) before any retry.

## Results and verification

`created` counts retained instances. `succeeded` rows contain committed `created_id`, `unique_id` and `activated_symbol`. `proposed` IDs are temporary and must be discarded. Inspect `failed`, `committed`, `commitWarnings`, `commitDiagnostics`, and `commit_validation_performed`. Reread committed IDs with `get_element_details` or `get_elements`; capture the visible result when needed. A successful transaction does not save the model.
