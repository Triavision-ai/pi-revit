# create_walls

## Purpose and boundaries

Create 1–100 native **Basic, Curtain or Stacked** walls with existing wall types. Path shapes are a line, circular arc or elliptical arc in the document-internal XY plane. A fourth shape creates a wall from a closed, planar vertical polygon profile. Use `get_element_types` to discover `WallType` IDs and `get_model_overview` for levels. The tool creates model elements and does not save the RVT file. Native Revit validation decides whether each type, shape and cross-section combination is valid.

Contract: PI-Revit 0.7.0 candidate source, [CreateWalls.cs](../../../../src/Revit/Tools/CreateWalls.cs). Activate through `find_revit_tools`. The selected bridge's live schema is authoritative. Revit 2025.4 is the available live test host; Revit 2026–2027 are source targets and have not been runtime tested here.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `7998e225e38b5e2f`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `walls` | array of object | yes |  |  |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Wall attachments and editing an existing wall profile | Revit API: Wall.AddAttachment (Revit 2025.2+); Wall.CreateProfileSketch and SketchEditScope. Check all its members in one call: `search_api_docs` with query `Wall.AddAttachment; Wall.CreateProfileSketch; SketchEditScope`, then use `execute_csharp` within the requested scope. |
| Wall type or level creation and wall type layers, width or materials | Revit API: ElementType.Duplicate; Level.Create; HostObjAttributes.SetCompoundStructure. Check all its members in one call: `search_api_docs` with query `ElementType.Duplicate; Level.Create; HostObjAttributes.SetCompoundStructure`, then use `execute_csharp` within the requested scope. |
| Wall openings and non-end geometry joins | Revit API: Document.NewOpening; JoinGeometryUtils.JoinGeometry. Check all its members in one call: `search_api_docs` with query `Document.NewOpening; JoinGeometryUtils.JoinGeometry`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md) and bind the intended model with `get_model_overview`. Every row needs `wall_type_id` and `level_id`. The top-level `unit` applies to all coordinates and lengths; angles are always degrees.

| `shape` | Required geometry | Vertical extent |
| --- | --- | --- |
| `line` (default) | `start`, `end` as absolute document-internal `[x,y]` | Exactly one of `height` or `top_level_id` |
| `arc` | `center` `[x,y]`, `radius`, `start_angle_deg`, `end_angle_deg` | Same path-wall top modes |
| `ellipse` | `center` `[x,y]`, `x_radius`, `y_radius`, `start_angle_deg`, `end_angle_deg` | Same path-wall top modes |
| `profile` | `profile_vertices`: 3–100 ordered absolute document-internal `[x,y,z]` points; closing edge implicit | Vertex Z values define the height; omit height/top/base offsets |

Arc/ellipse angles are measured counterclockwise from +X; the end must exceed the start by less than 360°. The profile must form a closed, vertical planar boundary with non-short edges. For a path wall, `height` is positive unconnected height, or `top_level_id` is an existing upper level with optional `top_offset`. `base_offset` moves the wall base relative to `level_id`. The baseline Z is the base level's `ProjectElevation` in the document's internal coordinate frame, not its shared-datum display elevation. A top level must end above the physical base. Profile vertices already have absolute Z and do not use those top/base fields.

`cross_section` is `Vertical` (default), `SingleSlanted`, or `Tapered`. `SingleSlanted` accepts `slant_angle_deg`; `Tapered` accepts `exterior_angle_deg` and `interior_angle_deg`. Angles are between -89° and 89° and become radians in Revit parameters. Tapered walls require a wall type whose compound structure permits tapering; Revit rejects incompatible types per row. The profile shape is vertical only.

`location_line` defaults to `WallCenterline`; the other choices are `CoreCenterline`, `FinishFaceExterior`, `FinishFaceInterior`, `CoreExterior`, and `CoreInterior`. On line and arc paths, the selected plane follows the requested path. The tool uses `CompoundStructure.GetOffsetForLocationLine` for placement. A noncenter plane requires a usable compound structure, which common Curtain and Stacked types lack. Elliptical paths and vertical profiles require `WallCenterline`: Revit cannot assign an offset ellipse's Hermite spline as the wall location curve. `flip` reverses the requested exterior side; Revit can apply flip differently to Curtain and Stacked walls, so inspect `actual.exterior_normal`.

Optional `room_bounding`, `allow_join_start`, and `allow_join_end` set the instance's room behavior and the join permission at curve ends 0/1. `structural` is a legacy boolean; use it **or** `structural_usage` (`NonBearing`, `Bearing`, `Shear`, `Combined`). Width, layers and materials belong to the selected wall type and are not changed here. Use a duplicated type for type-level layer edits; altering an existing type can change all its instances.

Malformed coordinates, mutually exclusive fields and nonfinite values are rejected before editing. Missing/incompatible type or level IDs and Revit-specific failures fail within each row's subtransaction. Paths must exceed Revit's short-curve tolerance. Heights cannot exceed 30,000 internal feet.

`preview: true` validates a commit and rolls the batch back. `atomic: true` rolls the whole batch back if any row fails. The default permits partial success. A preview or atomic rollback may report `proposed` rows even though no wall was retained. An original `_operation_id` is only for recovering an identical uncertain request; consult [operation recovery](../operation-recovery.md) before any retry.

## Results and verification

`created` counts retained walls. `succeeded` rows contain committed `created_id`, `unique_id`, `requested_shape`, and `actual`, reread after commit and regeneration. A post-commit `actual_read_error` does not undo a retained wall. `actual` includes `wall_kind`, location-curve type, profile sketch ID when present, type/base/top IDs, height/offsets, curve length, type width, location-line mode, location-curve endpoints, flip and exterior normal, cross-section and angle parameters, structural usage, room bounding and both end joins. Numeric lengths and XYZ positions in `actual` are in **internal feet**; angle values are **radians**. For noncenterline alignment, location-curve endpoints differ from the requested path because they describe Revit's wall centerline.

`proposed` rows are rolled-back candidates whose IDs are temporary; they have no post-commit `actual`. `failed` rows have indexed reasons. Read `committed`, `commitWarnings`, `commitDiagnostics`, and `commit_validation_performed` before reporting. Reread committed IDs with `get_element_details` using language-independent `BuiltInParameter` identifiers such as `WALL_KEY_REF_PARAM`, `WALL_HEIGHT_TYPE`, `WALL_TOP_OFFSET`, `WALL_BASE_OFFSET`, `WALL_ATTR_ROOM_BOUNDING`, and `WALL_STRUCTURAL_USAGE_PARAM`. For placement, inspect the location/bounding box or capture the actual view. A successful transaction does not save the model.

Autodesk references: [wall location line](https://help.autodesk.com/cloudhelp/2023/ENU/Revit-ArchDesign/files/GUID-F26DB6DA-A0EC-424D-B656-3BDF47607F4F.htm), [wall instance properties](https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Model/files/GUID-ED2045E5-90BF-418F-AAD6-3BB1544F34F5.htm), [compound structures](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API/files/Revit_API_Developers_Guide/Revit_Geometric_Elements/Walls_Floors_Ceilings_Roofs_and_Openings/Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Walls_Floors_Ceilings_Roofs_and_Openings_CompoundStructure_html.html), [slanted walls](https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-A4FC49E9-37F3-440C-9726-89947F7E58D1.htm), [tapered walls](https://help.autodesk.com/cloudhelp/2022/ENU/Revit-ArchDesign/files/GUID-BC422403-C03D-4292-AD8B-D578EC0659D1.htm).
