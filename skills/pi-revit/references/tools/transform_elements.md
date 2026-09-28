# transform_elements

## Purpose and boundaries

Move, copy, or rotate explicit host elements together in one model-edit step. It does not accept linked targets or perform collision analysis. Constrained or hosted dependents may also move; returned target snapshots are not a complete dependent-change audit.

Contract: PI-Revit 0.4.0 source, [TransformElements.cs](../../../../src/Revit/Tools/TransformElements.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `538f5336f11a299e`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `move`, `copy`, `rotate` |
| `element_ids` | array of integer | yes |  |  |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `translation` | array of number | no |  |  |
| `axis_origin` | array of number | no |  |  |
| `axis_direction` | array of number | no |  |  |
| `angle_degrees` | number | no |  |  |
| `preview` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Mirroring | Revit API: ElementTransformUtils.MirrorElements. Check all its members in one call: `search_api_docs` with query `ElementTransformUtils.MirrorElements`, then use `execute_csharp` within the requested scope. |
| Pinned elements (move and rotate reject them) | User action: Confirm unpinning (Element.Pinned) first |
| Elements inside linked models | User action: Edit the linked model itself |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md), inspect targets, and confirm the requested scope first.

| Input | Meaning |
| --- | --- |
| `action` | Required: `move`, `copy`, or `rotate`. |
| `element_ids` | Required: 1–200 distinct positive host IDs. |
| `unit` | Required: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`. |
| `translation` | Required for move/copy: finite `[x,y,z]` displacement in `unit`. |
| `axis_origin` | Required for rotate: finite `[x,y,z]` position in `unit`, relative to the document internal origin. |
| `axis_direction` | Required for rotate: nonzero dimensionless direction `[x,y,z]`; normalized internally. |
| `angle_degrees` | Required for rotate: finite signed angle using the right-hand rule. |
| `preview` | Default `false`; commit-validates then rolls back all model changes. |
| `expected_document_id` | Required for every call, including preview. Exact current model identity from the overview. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Coordinates follow the document's internal origin and axes, not sheet coordinates or shared coordinates. All requested elements form one step: the whole selection succeeds or rolls back. Move and rotate reject pinned targets; copy does not pre-reject pinned sources, but Revit still validates it. The tool never automatically unpins elements.

## Example

Illustrative IDs must be replaced with discovered targets. This previews a 250 mm move along internal X.

```json
{
  "action": "move",
  "element_ids": [12345, 12346],
  "unit": "millimeters",
  "translation": [250, 0, 0],
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Inspect common fields `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. Snapshots contain requested/created element identities, type, and point or curve-endpoint locations in feet; they do not include complete geometry. `created_ids` comes from copy and can include elements Revit creates with the copied selection. A copy also reports `inherited_state`: per copy, what it carried over from its source, such as Mark, Comments, group or design-option membership. Per-view hiding and overrides are keyed by element ID, so copies do not inherit them. Check carried values against the request; a copied Mark usually needs a new value.

Copy preview IDs are temporary (`created_ids_are_temporary`) and must never be reused after rollback. Use committed results, then reread targets and inspect affected dependents. A real operation after a preview has changed arguments and needs a new operation ID.

Follow [operation recovery](../operation-recovery.md) for uncertain outcomes and [visual verification](../visual-verification.md) for before/after positions and orientation. A successful API step alone does not establish correct placement. This tool does not save the model.
