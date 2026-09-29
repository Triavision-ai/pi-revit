# query_spatial_elements

## Purpose and preconditions

Find host elements whose model axis-aligned bounding boxes intersect or fit inside a specified region. Activate with `find_revit_tools`; use the intended active document. This is an approximate candidate query, not a solid-intersection or clash detector, and it does not traverse linked contents.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `0018676c125da56c`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `query` | object | no |  |  |
| `min` | array of number | yes |  |  |
| `max` | array of number | yes |  |  |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `relation` | string | no |  | `intersects`, `inside` |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Solid-intersection clash detection | Revit API: ElementIntersectsElementFilter; ElementIntersectsSolidFilter. Check all its members in one call: `search_api_docs` with query `ElementIntersectsElementFilter; ElementIntersectsSolidFilter`, then use `execute_csharp` within the requested scope. |
| Elements inside linked models | Tool: get_linked_elements with host_bounds |
<!-- generated:contract:end -->

## Public inputs

- Required `min` and `max`: arrays of three finite numbers along document internal axes. Every minimum coordinate must be less than or equal to its maximum.
- Required `unit`: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`. This covers region coordinates and output lengths.
- Optional `relation`: `intersects` (default) or `inside`. Both include touching boundaries; inside requires the entire element box to fit in the region.
- Optional `query`: whole-scope `get_elements` filters (`category`, `of_class`, `level`, `type_id`, `in_active_view`, `filter`). No query-level paging, count-only mode, fields, or projections are accepted.
- Optional outer `offset`: default 0; `limit`: 1–200, default 100. Pages spatial matches.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact previous operation ID for identical retry only.

## Example

This illustrative region is in internal document axes. Replace it with bounds established for the requested part of the intended model; it is not a shared/GIS region.

```json
{
  "query": { "category": "OST_Walls" },
  "min": [0, 0, 0],
  "max": [10, 10, 3],
  "unit": "meters",
  "relation": "intersects",
  "limit": 100
}
```

## Results and verification

The candidate query is capped at 10,000 elements **before** spatial testing, even if few boxes intersect. Narrow the category/level/type/filter scope first. `candidate_count` is distinct from spatial `total_count`; `without_bounds_count` counts candidates omitted because no model bounding box exists. Preserve warnings.

The result states `method: "axis_aligned_bounding_boxes"`, `approximate: true`, `coordinate_system: "document_internal"`, selected unit/relation, and the normalized region. Element rows include IDs, unique identities, names/categories, and bounds in the requested unit. Results sort by ID; follow `next_offset` for remaining matches.

Bounds can include nonphysical geometry. Intersection or containment of enclosing boxes does not establish a physical collision, exact shape containment, or clearance failure. Report candidates with the returned approximation/method labels, then inspect actual geometry if the task requires stronger conclusions. [Visual verification](../visual-verification.md) can support review but does not by itself turn box testing into exact geometry computation.

Check coordinate frame, unit, candidate coverage, absent bounds, and page coverage before reporting. Any saved-response continuation is separate; see [execution rules](../execution-rules.md).

## Effects and recovery

Read-only: no model or UI edits, save, or export. Invalid units/vectors, reversed region bounds, unsupported query fields, or too many candidates fail. Correct the region/scope; do not reinterpret coordinates silently. Use [operation recovery](../operation-recovery.md) for bridge/identity failures.

## Compatibility

Source reference: PI-Revit 0.5.0, [QuerySpatialElements.cs](../../../../src/Revit/Tools/QuerySpatialElements.cs), shared whole-query scope, and identity/retry inputs. Revit 2025–2027 bridge targets; source-reviewed without new live validation.
