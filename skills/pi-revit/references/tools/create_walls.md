# create_walls

## Purpose and boundaries

Create a short batch of straight, vertical **basic** walls in a project document. Use `get_element_types` to discover a `WallType` ID and `get_elements` or `get_model_overview` for a level ID. This tool creates model elements and does not save the RVT file. For curved, slanted, stacked, attached or profile-edited walls, verify the relevant Revit API with `search_api_docs` and use `execute_csharp` within the user's scope.

Contract: PI-Revit 0.7.0 source, [CreateWalls.cs](../../../../src/Revit/Tools/CreateWalls.cs). Activate through `find_revit_tools`. The live schema and selected bridge version are authoritative.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `cf321b3ef7da7d0f`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `walls` | array of object | yes |  |  |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Curved, slanted, stacked, profile-edited and attached walls | Revit API: Wall.Create; Wall.CrossSection; Wall.AddAttachment. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
| Wall type or level creation | Revit API: ElementType.Duplicate; Level.Create. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
| Joining and opening placement | Revit API: JoinGeometryUtils.JoinGeometry; Document.NewOpening. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md) and bind the intended model with `get_model_overview`. `walls` contains 1–100 rows, each with `start` and `end` as absolute document internal XY coordinates, `wall_type_id`, `level_id`, and positive unconnected `height`. `unit` applies to all coordinates, heights and optional `base_offset`. Optional `flip` and `structural` default to false. The baseline is placed at the selected level; Revit applies `base_offset` separately. All input shapes, units and finite values are checked before any edit. A row with a missing or incompatible model type or level fails inside its own subtransaction.

`preview: true` validates a commit and rolls the batch back. `atomic: true` rolls the whole batch back if any row fails. The default permits partial success. An original `_operation_id` is only for recovering an identical uncertain request; consult [operation recovery](../operation-recovery.md) before any retry.

## Results and verification

`created` counts retained walls. `succeeded` rows contain committed `created_id` and `unique_id`; `proposed` rows are rolled-back candidates whose IDs are temporary; `failed` rows have indexed reasons. Read `committed`, `commitWarnings`, `commitDiagnostics`, and `commit_validation_performed` before reporting. Reread committed IDs with `get_element_details` or `get_elements`, then capture the requested visible result when visual verification matters. A successful transaction does not save the model.
