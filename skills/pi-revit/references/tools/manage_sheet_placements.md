# manage_sheet_placements

## Purpose and boundaries

List, place, or move viewports and schedule instances on a drawing sheet. This tool positions existing committed views; it does not create the source view or schedule. Coordinates are paper-space sheet coordinates, never multiplied by view scale.

Contract: PI-Revit 0.5.0 source, [ManageSheetPlacements.cs](../../../../src/Revit/Tools/ManageSheetPlacements.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `capture`: capture the visible result and inspect the image.
- **Contract hash:** `1d9fd57c25d68159`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `list`, `place`, `move` |
| `sheet_id` | integer | no |  |  |
| `view_id` | integer | no |  |  |
| `placement_id` | integer | no |  |  |
| `position` | array of number | no |  |  |
| `unit` | string | no |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `rotation` | string | no |  | `none`, `clockwise`, `counterclockwise` |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `preview` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Creating the source view or schedule | Tool: manage_views; manage_schedules |
| Rotating schedule instances | Revit API: ScheduleSheetInstance.Rotation. Check all its members in one call: `search_api_docs` with query `ScheduleSheetInstance.Rotation`, then use `execute_csharp` within the requested scope. |
| Changing the titleblock | Tool: change_element_types on the titleblock instance |
<!-- generated:contract:end -->

## Inputs and preconditions

Follow [execution rules](../execution-rules.md); discover the intended sheet, source view/schedule, and current placements first.

| Input | Meaning |
| --- | --- |
| `action` | Required: `list`, `place`, or `move`. |
| `sheet_id` | Required for list/place. Optional for move as an extra check that the placement belongs to that sheet. |
| `view_id` | Required for place: committed view or schedule ID. |
| `placement_id` | Required for move: existing viewport or schedule-instance ID, not its source view ID. |
| `position`, `unit` | Required for place/move: finite `[x,y,0]` in explicit `millimeters`, `centimeters`, `meters`, `feet`, or `inches`. |
| `rotation` | Optional viewport-only `none`, `clockwise`, or `counterclockwise`. Omit entirely for schedules, including when no rotation is intended. |
| `offset`, `limit` | List paging: nonnegative offset; limit default 100, range 1–200. |
| `preview` | Edit default `false`; commit-validates then rolls back. List does not perform a preview/edit. |
| `expected_document_id` | Required for every action, including `list`, because the tool is write-capable. Copy the intended model's current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

A viewport position is its box center excluding the label; a schedule position is its insertion point. Z must be zero. Pinned placements cannot be moved. Placeholder sheets cannot receive content. Revit validates view/schedule eligibility and can reject a view already placed elsewhere.

## Example

Illustrative sheet and source view IDs must be replaced with actual committed IDs. This previews placement at paper-space coordinates.

```json
{
  "action": "place",
  "sheet_id": 23456,
  "view_id": 34567,
  "position": [200, 150, 0],
  "unit": "millimeters",
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

List returns `total_count`, `counts`, `returned_count`, `next_offset`, and `placements`; follow all pages. `counts` covers the whole sheet: `viewports`, `schedules`, and `titleblock_revision_schedules`. A titleblock revision schedule is part of the titleblock family, not content anyone placed, so it has its own `kind: "titleblock_revision_schedule"` and cannot be moved. A sheet with no views or schedules placed has zero `viewports` and `schedules`, even if its titleblock shows a revision schedule. <!-- inv:special-objects-flagged --> Placement descriptions include IDs, `kind`, sheet/source IDs, position, `position_kind`, and `traits` where applicable. Returned positions always use feet; viewport rotation returns Revit enum names.

Edits return common transaction fields: `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, `commit_validation_performed`. The step contains `before`, `placement`, `created`, and `id_is_temporary`. Each edit is one step; failure rolls it back. New placement IDs from preview are temporary and must not be reused.

Reread placements and follow [visual verification](../visual-verification.md): inspect the actual sheet, labels, schedule extents, margins, and overlaps. Position values alone do not establish a readable layout. Follow [operation recovery](../operation-recovery.md) before repeating an uncertain placement. This does not save or export the model.
