# summarize_elements

## Purpose and preconditions

Count the whole matching host scope grouped by category, type name, level ID, or a single raw parameter value. Activate with `find_revit_tools`; use the intended active document. It does not traverse links and supports at most 10,000 matching elements. Use `get_elements.count_only` for a bare total that does not need grouping.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `27ddae42a59b01cb`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `query` | object | no |  |  |
| `group_by` | string | yes |  | `category`, `typeName`, `levelId`, `parameter` |
| `parameter` | string | no |  |  |
| `type_parameter` | boolean | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Linked-model contents | Tool: get_linked_elements |
| Scopes above 10,000 elements | Tool: narrow the query, or get_elements with count_only for a total |
<!-- generated:contract:end -->

## Public inputs

- Required `group_by`: exactly `category`, `typeName`, `levelId`, or `parameter`.
- Optional `query`: whole-scope [get_elements](get_elements.md) filters: `category`, `of_class`, `level`, `type_id`, `in_active_view`, and `filter`. Omission covers all host non-type elements subject to the cap.
- Query-level `offset`, `limit`, `count_only`, `fields`, `parameter_names`, and `include_type_parameters` are rejected, as are unknown query properties. Place identity/retry arguments at the outer level.
- `parameter` is required when `group_by` is `parameter`: display name, built-in name, or `guid:<GUID>`. Optional `type_parameter` (default false) chooses the type's parameter rather than the instance's.
- Optional outer `offset`: default 0. `limit`: 1–500, default 100; these page groups, not input elements.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact previous identical-retry ID only.

## Example

```json
{ "query": { "category": "OST_Walls" }, "group_by": "typeName", "limit": 100 }
```

## Results and verification

`total_elements` counts the entire matching scope; `total_groups` is independent of returned group count. Each `groups` row has `value`, `missing`, and `count`. Groups sort by descending count then an internal serialized key. Follow `next_offset` for remaining groups.

Parameter groups use exact raw values, including internal numeric units. A missing parameter (`missing: true`) is distinct from a present parameter whose value is null. Ambiguous display-name matches fail instead of choosing one parameter; use a discovered built-in identity or shared GUID. Names are localized. `typeName` groups by name, so different types with the same name can share a group; it is not a unique-type-ID inventory.

The query still interprets numeric **filter inputs** in explicit or document display units, while grouped numeric **output values** are raw internal units. Preserve this difference. Check warnings alongside an unexpected zero and confirm the filter scope. Summing counts from all groups should describe `total_elements`; a single group's page does not describe the full distribution.

For more than 10,000 matches, narrow the scope meaningfully and report any partitioning. Do not remove the cap by pretending a single page represents all elements. Any saved-response retrieval is separate from group paging; see [execution rules](../execution-rules.md).

## Effects and recovery

Read-only: no grouping changes in Revit, selection, save, or export. Oversized scope, invalid query fields, missing parameter input, or ambiguous identities fail. Correct the query rather than silently excluding problematic elements. Use [operation recovery](../operation-recovery.md) for document/bridge uncertainty.

## Compatibility

Source reference: PI-Revit 0.5.0, [SummarizeElements.cs](../../../../src/Revit/Tools/SummarizeElements.cs) and [ElementQueryScope.cs](../../../../src/Revit/Tools/ElementQueryScope.cs), with public identity/retry overlays. Revit 2025–2027 bridge targets; no new live validation is claimed.
