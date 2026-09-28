# get_schedules

## Purpose and preconditions

List host schedules, or inspect one schedule's fields, sorting/filtering, and formatted body cells. Activate with `find_revit_tools`; use the intended active document. The list excludes templates and titleblock revision schedules. Reading an explicit schedule rejects templates. This is a data read, not a graphical capture.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `518da1bdb73e1de9`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `schedule_id` | integer | no |  |  |
| `name_filter` | string | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `column_offset` | integer | no |  |  |
| `column_limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Titleblock revision schedules and schedule templates | Revit API: ViewSchedule.IsTitleblockRevisionSchedule; ViewSchedule.GetTableData. Check all its members in one call: `search_api_docs` with query `ViewSchedule.IsTitleblockRevisionSchedule; ViewSchedule.GetTableData`, then use `execute_csharp` within the requested scope. |
| Graphical layout of a schedule on a sheet | Tool: capture_view |
<!-- generated:contract:end -->

## Public inputs

- Optional `schedule_id`: omit to list; supply a discovered integer ID to inspect one schedule.
- Optional `name_filter`: case-insensitive substring used for the list.
- Optional `offset`: list offset or body-row offset, default 0. `limit`: 1–200, default 50.
- Optional `column_offset`: default 0; `column_limit`: 1–50, default 50. These apply to the body read.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact previous ID for an identical retry only.

## Examples

```json
{ "name_filter": "Room", "limit": 50 }
```

Replace this illustrative number with a schedule ID from the list:

```json
{ "schedule_id": 12345, "offset": 0, "limit": 50, "column_offset": 0, "column_limit": 50 }
```

## Results and verification

The list returns schedule identity, category, itemization, field count, and the usual `total_count`/`returned_count`/`has_more`/`next_offset` fields.

A body read returns all `fields`, current `sort_fields` and `filters`, plus a page of `rows`. Field metadata includes schedule-local `field_id`, `parameter_id`, case-sensitive `field_type`, localized name/heading, hidden state, specification ID, grid/sheet widths in feet, and filter capabilities. Use the schedule-local `field_id` for configuring existing fields/sorts/filters. The eligible parameter/type pair from `get_schedule_fields` serves a different purpose: adding fields.

Cells use formatted Revit text and may represent headings, grouped rows, or totals. Neither a row index nor a cell value establishes an element ID. Hidden field definitions do not map one-to-one to displayed columns. Read `row_index` and `first_column_index`; do not guess index origins.

Body reads have two independent continuations: `next_offset` for rows and `next_column_offset` for columns. Read every column page at one row offset before advancing rows, then reset column offset. `total_rows`, `total_columns`, `returned_rows`, and `returned_columns` expose coverage. A saved large response has an additional text-fragment continuation; see [execution rules](../execution-rules.md).

Returned filter comparison names are Revit enum names, not the input strings accepted by `manage_schedules`; measured numeric filter values are internal units. Do not copy them into edit arguments without the required translation and explicit unit. Verify committed schedule edits by rereading fields/rules and, for visible layout, following [visual verification](../visual-verification.md), usually with the schedule placed on a sheet.

## Effects and recovery

Read-only: no schedule creation, modification, view activation, save, or export. An invalid schedule ID or template read fails. Re-discover after deletion or preview rollback; preview-created schedule/field IDs are temporary. Use [operation recovery](../operation-recovery.md) for identity or transport uncertainty.

## Compatibility

Source reference: PI-Revit 0.4.0, [GetSchedules.cs](../../../../src/Revit/Tools/GetSchedules.cs), plus public identity/retry inputs. Revit 2025–2027 bridge targets; source review does not claim new live validation.
