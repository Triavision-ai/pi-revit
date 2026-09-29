# get_elements

## Purpose and preconditions

List or count host-document instances of any category, including views, sheets, rooms, and levels. Use returned IDs for further inspection or host operations. This core tool requires an active document in the intended session. It excludes element types; use `get_element_types` for those. It does not traverse linked model contents.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `638881fcadf80086`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `category` | string | no |  |  |
| `of_class` | string | no |  |  |
| `filter` | object | no |  |  |
| `level` | string | no |  |  |
| `type_id` | integer | no |  |  |
| `in_active_view` | boolean | no |  |  |
| `count_only` | boolean | no |  |  |
| `parameter_names` | array of string | no |  |  |
| `include_type_parameters` | boolean | no |  |  |
| `fields` | array of string | no |  | `id`, `name`, `category`, `typeName`, `levelId` |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Elements inside linked models | Tool: get_linked_elements |
| Grouped counts or statistics over a whole scope | Tool: summarize_elements |
| Spatial containment or intersection | Tool: query_spatial_elements |
<!-- generated:contract:end -->

## Public inputs

All inputs are optional:

- Scope: `category` (display name or `BuiltInCategory` such as `OST_Walls`), `of_class` (Revit class name), `level` (name or ID represented as a string in the public schema), `type_id` (integer), and `in_active_view` (default false).
- `filter`: `{ "match": "all" | "any", "rules": [...] }`, default AND. Each rule requires `param` and `op`; `value` and `unit` depend on the operation. Parameters accept localized display names, built-in enum names, or `guid:<GUID>`.
- Rule `op`: `equals`, `not_equals`, `greater`, `greater_or_equal`, `less`, `less_or_equal`, `contains`, `is_empty`, `is_not_empty`, or `regex`. Empty checks do not need `value`; regex takes a pattern. Numeric comparison values use the supplied compatible `unit`, or the document's display units for that parameter.
- `count_only` (default false) returns only count information. `fields` selects identity fields `id`, `name`, `category`, `typeName`, `levelId`; `id` is always included, and all five are the default.
- `parameter_names`: up to 20 requested parameter identities. `include_type_parameters` (default false) also projects those identities from the type. Count-only mode ignores projections.
- `offset`: default 0. `limit`: 1–1000, default 200.
- `expected_document_id`: optional exact identity guard for this read. `_operation_id`: optional exact identical-retry ID. See [execution rules](../execution-rules.md) and [operation recovery](../operation-recovery.md).

## Examples

Count all host walls without transferring rows:

```json
{ "category": "OST_Walls", "count_only": true }
```

Inspect a small parameter projection on matching doors:

```json
{
  "category": "OST_Doors",
  "filter": { "rules": [{ "param": "ALL_MODEL_MARK", "op": "is_not_empty" }] },
  "parameter_names": ["ALL_MODEL_MARK", "ALL_MODEL_INSTANCE_COMMENTS"],
  "limit": 100
}
```

## Results and verification

Listing returns `total_count`, `returned_count`, `elements`, `offset`, `has_more`, and `next_offset`; follow every page required by the task. `total_count` covers all matches, not the page. Count-only results have `count_only: true` and no rows. A separately saved result has its own text-fragment continuation; reading that cannot recover element pages never requested.

Each parameter projection reports `requested`, `isType`, `found`, `ambiguous`, and `matches`. Preserve every match instead of silently choosing a duplicate name. Rows of special or system-owned objects carry a `traits` object, such as `titleblock_revision_schedule`, `placeholder_sheet`, `view_template`, `dependent_view_of`, `group_id`, `design_option_id` or `pinned`; ordinary elements have none. <!-- inv:special-objects-flagged --> Raw `value` uses Revit storage/internal numeric units; `displayValue` is formatted separately, and a reported `unit` names the display unit. This differs from numeric filter inputs.

Prefer built-in or shared GUID identities for stable filtering. Display-name rules and regex can scan the narrowed scope; a display name need not identify one globally uniform parameter. Narrow by category/class where possible. An unexpected zero plus a missing-parameter warning may be a localization problem, not absence of elements. Inspect details and use the discovered built-in identity. `is_empty` also matches elements that lack the parameter entirely. When the named parameter was not found on the probed elements, its warning is kept even though matches exist, because the matches may be elements without the parameter. <!-- inv:missing-not-silent --> A display name that matches several parameters on one element fails the query with their exact identities rather than silently choosing one; use one of those identities. <!-- inv:parameter-ambiguity -->

Verify scope before reporting counts. `in_active_view` is visibility-based and needs a suitable active view. Host IDs from this tool do not authorize changing the selection: use `manage_selection` separately within the requested scope.

## Effects and recovery

Read-only; no selection, model edit, save, or export. Invalid categories/classes/levels, malformed filters, or excessive projections fail. Recheck names and parameter identities rather than broadening a failed query silently. Query results are live reads, not retained snapshots; use `manage_element_sets` when fixed membership is needed. Follow shared recovery for identity and transport failures.

## Compatibility

Source reference: PI-Revit 0.5.0, [GetElements.cs](../../../../src/Revit/Tools/GetElements.cs) and [GetElementDetails.cs](../../../../src/Revit/Tools/GetElementDetails.cs) projection helper, plus public identity/retry inputs. Supported bridge targets are Revit 2025–2027; this is source review, not live validation.
