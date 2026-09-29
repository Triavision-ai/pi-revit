# manage_sheets

## Purpose and boundaries

Create a drawing sheet or update its name/number. Use `manage_sheet_placements` to place or move views and schedules; `get_elements` to inspect sheets; `open_view` to activate a committed sheet; and `delete_elements` for removal.

Contract: PI-Revit 0.5.0 source, [ManageSheets.cs](../../../../src/Revit/Tools/ManageSheets.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `6e201549acd231ea`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `create`, `update` |
| `sheet_id` | integer | no |  |  |
| `name` | string | no |  |  |
| `number` | string | no |  |  |
| `titleblock_type_id` | integer | no |  |  |
| `preview` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Placing views or schedules on a sheet | Tool: manage_sheet_placements |
| Revisions and revision clouds | Revit API: Revision.Create; RevisionCloud.Create. Check all its members in one call: `search_api_docs` with query `Revision.Create; RevisionCloud.Create`, then use `execute_csharp` within the requested scope. |
| Deleting sheets | Tool: delete_elements |
<!-- generated:contract:end -->

## Inputs and preconditions

| Input | Meaning |
| --- | --- |
| `action` | Required: `create` or `update`. |
| `name`, `number` | Both required nonempty strings for creation; individually optional for update. Revit validates text; sheet names may repeat. A number another sheet already uses is rejected before Revit is asked: the failed row carries `name_collision` with that sheet's ID. That sheet predates the call; do not renumber or edit it to free the number unless the user asks. |
| `sheet_id` | Required existing sheet ID for update. |
| `titleblock_type_id` | Creation only: loaded titleblock `FamilySymbol`. Omission creates a sheet without a titleblock. Rejected for update. |
| `preview` | Default `false`; commit-validates then rolls back the model changes. |
| `expected_document_id` | Required for all calls, including previews; exact current `project.documentId`. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Follow [execution rules](../execution-rules.md). Discover loaded titleblock types with `get_element_types`, category `OST_TitleBlocks`. To change an existing titleblock type, identify its instance and call `change_element_types`; do not pass a type override to sheet update.

## Example

The example intentionally creates a sheet without a titleblock; discover and add `titleblock_type_id` when the task needs one. The number must be available in the intended model.

```json
{
  "action": "create",
  "name": "Room documentation",
  "number": "A-901",
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. One step contains `before` where relevant, `sheet` (ID, unique ID, name, number, placeholder state), `created`, and `id_is_temporary`.

All properties are part of the same step, so a rejected number/name/titleblock rolls that step back. Preview-created sheet IDs are temporary and cannot be used for placements. Use the committed ID, verify name/number/titleblock, and follow [visual verification](../visual-verification.md) for the actual sheet layout.

Follow [operation recovery](../operation-recovery.md) after an uncertain outcome. A real creation after preview is a new operation. This tool neither saves the model nor exports the sheet; those actions need to be within the user's requested scope.
