# get_schedule_fields

## Purpose and preconditions

Discover fields eligible for addition to one existing regular schedule. Activate with `find_revit_tools`; obtain a current schedule ID from `get_schedules` in the intended document. Templates and titleblock revision schedules are rejected. Calculated/combined-field authoring is outside this tool.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `f7cac60f69eaf4b3`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `schedule_id` | integer | yes |  |  |
| `name_filter` | string | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Calculated (formula) fields | Not offered by the Revit API (Revit 2025 API exposes ScheduleFieldType.Formula but no member to author a formula). Report this with the evidence checked. |
| Combined-parameter fields | Revit API: ScheduleDefinition.InsertCombinedParameterField. Check all its members in one call: `search_api_docs` with query `ScheduleDefinition.InsertCombinedParameterField`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

- Required `schedule_id`: positive integer ID of an existing schedule.
- Optional `name_filter`: case-insensitive substring of the localized field name.
- Optional `offset`: nonnegative, default 0. `limit`: 1–200, default 100; invalid ranges are rejected.
- Optional `expected_document_id`: exact document read guard. Optional `_operation_id`: previous exact identical-retry ID only.

## Example

Replace the illustrative ID with a schedule actually present in the intended model. Omit an English name filter initially if localization is uncertain.

```json
{ "schedule_id": 12345, "offset": 0, "limit": 100 }
```

## Results and verification

`fields` contains `parameter_id`, case-sensitive `field_type`, localized `name`, and `included`. Identity is the **pair** `parameter_id` + `field_type`; negative built-in parameter IDs are valid. `included` indicates whether that pair already appears in this schedule. Results sort by parameter ID then field type; continue `next_offset` until the necessary candidates are covered.

Pass discovered pairs unchanged to `manage_schedules.add_fields`. They are not the schedule-local `field_id` values needed for updating, sorting, or filtering existing fields. Read those from `get_schedules` after a committed addition. Preview-added fields and schedules disappear on rollback; their temporary IDs are unusable afterward.

When Count appears, preserve its discovered pair. `manage_schedules` also accepts an addition containing only `field_type: "Count"`; do not invent a parameter ID. An English name filter returning no results may be a localization mismatch, not lack of field support.

Inspect `total_count`, `returned_count`, and `next_offset`. Reading a separately saved result does not remove field paging; see [execution rules](../execution-rules.md). Discovery establishes eligibility for that schedule, not that a chosen field is meaningful for the user's report or visually laid out correctly.

## Effects and recovery

Read-only: no added fields, schedule changes, save, or export. Invalid schedule kind/ID or paging range fails. Correct the request from current discovery. See [operation recovery](../operation-recovery.md) for bridge and identity errors, and [visual verification](../visual-verification.md) after later visible schedule changes.

## Compatibility

Source reference: PI-Revit 0.5.0, [GetScheduleFields.cs](../../../../src/Revit/Tools/GetScheduleFields.cs), with public identity/retry inputs. Revit 2025–2027 bridge targets; no new live validation is claimed.
