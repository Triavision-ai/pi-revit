# measure_geometry

## Purpose and preconditions

Measure exact distance between explicitly supplied points or approximate separation of two host elements' model bounding boxes. Activate with `find_revit_tools`; both modes require an active document. Linked contents are not traversed. Choose a mode that answers the question without overstating what was measured.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `34cc9bf1c26888bd`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `mode` | string | yes |  | `point_distance`, `bounding_box_gap` |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `point_a` | array of number | no |  |  |
| `point_b` | array of number | no |  |  |
| `element_a_id` | integer | no |  |  |
| `element_b_id` | integer | no |  |  |
| `clearance` | number | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Exact solid-to-solid distance or clash detection | Revit API: ElementIntersectsElementFilter; ElementIntersectsSolidFilter; ReferenceIntersector. Check all its members in one call: `search_api_docs` with query `ElementIntersectsElementFilter; ElementIntersectsSolidFilter; ReferenceIntersector`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

- Required `mode`: `point_distance` or `bounding_box_gap`.
- Required `unit`: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`; applies to point inputs, clearance threshold, and all returned coordinates/distances.
- Point mode requires `point_a` and `point_b`, each three finite coordinates in document internal axes. Omit both element IDs and `clearance` entirely.
- Box mode requires positive integer `element_a_id` and `element_b_id` in the host; omit point arguments. Optional `clearance` is a finite nonnegative box-proximity threshold in the selected unit.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact previous ID for identical retry only.

## Examples

The supplied points are illustrative; the result measures those points, not any implied element surfaces.

```json
{ "mode": "point_distance", "unit": "meters", "point_a": [0, 0, 0], "point_b": [3, 4, 0] }
```

Replace these illustrative IDs with two discovered host elements:

```json
{ "mode": "bounding_box_gap", "unit": "millimeters", "element_a_id": 12345, "element_b_id": 12346, "clearance": 100 }
```

## Results and verification

Point mode returns `approximate: false`, exact Euclidean `distance`, signed `delta` from A to B, both input points, coordinate frame, and unit. Exactness applies to the given points, not to how those points were chosen.

Box mode returns `approximate: true`, `distance`, nonnegative `axis_gaps`, element identities/bounds, `boxes_overlap_or_touch`, and the optional threshold classification. `box_gap_below_clearance` uses strict less-than; equality does not pass it. `boxes_overlap_or_touch` includes contact. There is no pagination.

A box gap is only a lower bound on physical geometry separation; boxes can include nonphysical geometry. Zero gap means enclosing boxes touch/overlap, not that solids clash. A threshold hit identifies a candidate for further review, not a verified clearance violation. Preserve the result's method and approximation labels. A missing model box fails the whole measurement rather than treating it as zero distance.

Verify units, frame, element identities, and intended measurement meaning. Use exact geometry investigation for claims requiring actual surfaces. See [execution rules](../execution-rules.md) for document/result handling and [visual verification](../visual-verification.md) if the wider task produces visible changes.

## Effects and recovery

Read-only: no geometry edits, dimensions, markers, UI changes, save, or export. Mixed mode arguments, invalid numbers/units, missing elements, or absent boxes fail. Reinspect inputs rather than substituting another mode without explaining its meaning. Follow [operation recovery](../operation-recovery.md) for bridge/identity uncertainty.

## Compatibility

Source reference: PI-Revit 0.4.0, [MeasureGeometry.cs](../../../../src/Revit/Tools/MeasureGeometry.cs), shared length/vector parsing, and public identity/retry inputs. Revit 2025–2027 bridge targets; no new live validation is claimed.
