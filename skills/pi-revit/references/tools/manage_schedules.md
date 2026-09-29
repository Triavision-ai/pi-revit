# manage_schedules

## Purpose and boundaries

Create or configure a regular schedule as one atomic model-edit step. Templates, titleblock revision schedules, embedded schedules, and calculated/combined-field authoring are outside this tool. Use `get_schedules` to inspect definitions and displayed cells, `get_schedule_fields` to discover eligible additions, and `manage_sheet_placements` for sheet layout.

Contract: PI-Revit 0.5.0 source, [ManageSchedules.cs](../../../../src/Revit/Tools/ManageSchedules.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `d713a59aeac1e65c`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `create`, `configure` |
| `schedule_id` | integer | no |  |  |
| `category` | string | no |  |  |
| `name` | string | no |  |  |
| `area_scheme_id` | integer | no |  |  |
| `is_itemized` | boolean | no |  |  |
| `preview` | boolean | no |  |  |
| `add_fields` | array of object | no |  |  |
| `update_fields` | array of object | no |  |  |
| `sort_fields` | array of object | no |  |  |
| `filters` | array of object | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Calculated (formula) fields | Not offered by the Revit API (Revit 2025 API exposes ScheduleFieldType.Formula but no member to author a formula). Report this with the evidence checked. |
| Combined-parameter fields | Revit API: ScheduleDefinition.InsertCombinedParameterField. Check all its members in one call: `search_api_docs` with query `ScheduleDefinition.InsertCombinedParameterField`, then use `execute_csharp` within the requested scope. |
| Schedule templates, titleblock revision schedules and embedded schedules | Revit API: ViewSchedule and ScheduleDefinition members; verify with search_api_docs. Check all its members in one call: `search_api_docs` with query `ViewSchedule; ScheduleDefinition`, then use `execute_csharp` within the requested scope. |
| Placing a schedule on a sheet | Tool: manage_sheet_placements |
<!-- generated:contract:end -->

## Inputs and preconditions

Follow [execution rules](../execution-rules.md). There are two different field identities: eligible additions use `parameter_id` plus case-sensitive `field_type`; existing schedule fields use schedule-local `field_id`. Never infer either from a column position.

| Input | Meaning |
| --- | --- |
| `action` | Required: `create` or `configure`. |
| `category`, `name` | Both required for creation. Category is a supported category name/enum; optional nonempty `name` can rename during configure. A name another schedule already uses is rejected with `name_collision` and that schedule's ID; do not edit or rename the existing schedule unless the user asks. |
| `area_scheme_id` | Creation-only option for area schedules. Category and area scheme are rejected during configure. |
| `schedule_id` | Required existing regular schedule ID for configure. |
| `is_itemized` | Optional boolean controlling whether instances remain separate. |
| `add_fields` | At most 50 objects with required `field_type`, usually `parameter_id`, and optional `heading`, `hidden`, `width`, `unit`. |
| `update_fields` | At most 50 objects with required actual `field_id`, and optional `heading`, `hidden`, `width`, `unit`. Duplicate update IDs are rejected. |
| `sort_fields` | At most 4 objects with `field_id`, optional `descending` and `show_header` (both default false). Replaces the whole sort list when supplied. |
| `filters` | At most 8 objects, described below. Replaces the whole filter list when supplied. |
| `preview` | Default `false`; commit-validates then rolls back model changes. |
| `expected_document_id` | Required for create/configure, including previews; exact current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Omit `sort_fields` or `filters` to preserve the existing list; `[]` explicitly clears it. A positive field `width` requires a length `unit` (`millimeters`, `centimeters`, `meters`, `feet`, or `inches`) and updates grid and sheet widths together.

For additions, pass a returned eligible `parameter_id`/`field_type` pair unchanged, including negative built-in parameter IDs. Count also supports `{ "field_type": "Count" }` without a parameter ID. If a Count pair is supplied it must be eligible; never guess its ID. Discover on a committed schedule, add fields, then reread the committed fields before writing sort/filter rules. Newly created schedule IDs and added field IDs from previews cannot be reused.

Each filter requires `field_id`, `comparison` (`equals`, `not_equals`, `contains`, `greater_than`, `less_than`), `value_type` (`string`, `number`, `integer`, `element_id`), and a matching `value`. Measured `number` filters require an explicit unit compatible with the field specification, such as `meters` or `squareMeters`; omit units for unitless numbers. Use `get_schedules` metadata `spec_type_id`, `can_filter_value`, and `can_filter_substring` to inspect capabilities. Read results express numeric filter values in internal units and comparisons as Revit enum names, not the write strings above.

## Example

Create a preview with a Count field; this does not guess any parameter ID. Use a valid unique name for the intended document.

```json
{
  "action": "create",
  "category": "OST_Walls",
  "name": "Wall count review",
  "is_itemized": false,
  "add_fields": [{ "field_type": "Count", "heading": "Count", "width": 25, "unit": "millimeters" }],
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. The single step returns schedule ID/unique ID/name, `created`, `id_is_temporary`, `added_field_ids`, `added_field_ids_are_temporary`, itemization, and field/filter/sort counts. Any rejected property rolls back this whole step.

Read the committed definition and all required body row/column pages through `get_schedules`. Returned grid/sheet widths use feet. Body rows may be headings, groups, or totals; their row indices are not element IDs. To check layout, place the committed schedule on a sheet within the task scope and follow [visual verification](../visual-verification.md); standalone schedules cannot be captured by `capture_view`.

Follow [operation recovery](../operation-recovery.md) for uncertain outcomes. A real edit after preview needs a new operation ID. This tool does not save or export the model.
