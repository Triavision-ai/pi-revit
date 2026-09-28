# get_model_coordinates

## Purpose and preconditions

Read project/survey base points, active project location, site data, project-location pages, and optionally map supplied internal-axis points into the active shared-coordinate system. Activate with `find_revit_tools`. Requires the intended active **project** document; family documents are rejected.

This reports Revit's mapping, not an inferred GIS coordinate reference system. Site latitude/longitude and base-point values alone do not establish a datum, projection, EPSG code, or external map units.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `d8e8f92e2b035e4e`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `points` | array of array | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Identifying the datum, projection or EPSG code | User action: Confirm the coordinate reference system with the surveyor or project standard |
| Changing coordinates or acquiring shared coordinates | Revit API: ProjectLocation.SetProjectPosition; Document.AcquireCoordinates. Check all its members in one call: `search_api_docs` with query `ProjectLocation.SetProjectPosition; Document.AcquireCoordinates`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

- Required `unit`: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`.
- Optional `points`: up to 100 arrays of three finite numbers, expressed along document internal axes in the requested unit. Omit to inspect the coordinate setup without point conversion.
- Optional `offset`: default 0; `limit`: 1–100, default 50. Paging applies only to project locations, not points.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact previous operation ID for identical retry only.

## Example

This reads how the active project location maps the internal origin. It does not change or establish coordinates.

```json
{ "unit": "meters", "points": [[0, 0, 0]], "limit": 50 }
```

## Results and verification

The response includes `active_project_location`, project/survey base-point IDs and internal/shared positions, `site` name/latitude/longitude/time zone, location pages, and converted `points`. Each input point is mapped with the active location's `GetProjectPosition`, producing east/west, north/south, elevation, and angle.

All returned lengths use the requested unit. Angles and latitude/longitude use degrees. Direction labels and `point_input_coordinates: "document_internal"` are part of the meaning; do not mix them with sheet coordinates or assume the input is already shared coordinates.

Location rows sort by ID and include name, active flag, and the internal origin in that location's shared coordinates. Use `total_locations` and `next_offset` to complete their listing. Point results are not paged, and points always use the active location even while another location page is being read. Base-point entries can be null if unavailable.

Verify the active project location, requested units, and source of input points before interpreting the output. Use [execution rules](../execution-rules.md) for document identity and any saved-response continuation. This tool does not validate the survey's real-world correctness.

## Effects and recovery

Read-only: no acquire/publish coordinates, base-point movement, active-location changes, model save, or export. Family documents, invalid units/vectors, or more than 100 points fail. Resolve the intended project/frame rather than silently changing it. See [operation recovery](../operation-recovery.md) for identity/transport failures.

## Compatibility

Source reference: PI-Revit 0.4.0, [GetModelCoordinates.cs](../../../../src/Revit/Tools/GetModelCoordinates.cs), shared length/vector parsing, and public identity/retry inputs. Revit 2025–2027 bridge targets; source-reviewed, without new live validation.
