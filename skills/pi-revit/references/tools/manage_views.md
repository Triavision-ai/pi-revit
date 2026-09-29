# manage_views

## Purpose and boundaries

Create plan, isometric 3D, or section views; duplicate a view; or update its name, scale, and template. Each call is one model-edit step. Use `get_elements` to query views, `open_view` to activate a committed view, and `delete_elements` to remove one.

Contract: PI-Revit 0.5.0 source, [ManageViews.cs](../../../../src/Revit/Tools/ManageViews.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `capture`: capture the visible result and inspect the image.
- **Contract hash:** `a8633ed43c6fb869`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `create_plan`, `create_3d`, `create_section`, `duplicate`, `update` |
| `view_id` | integer | no |  |  |
| `view_family_type_id` | integer | no |  |  |
| `level_id` | integer | no |  |  |
| `duplicate_option` | string | no |  | `duplicate`, `with_detailing`, `dependent` |
| `name` | string | no |  |  |
| `scale` | integer | no |  |  |
| `view_template_id` | integer | no |  |  |
| `unit` | string | no |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `origin` | array of number | no |  |  |
| `viewing_direction` | array of number | no |  |  |
| `up` | array of number | no |  |  |
| `width` | number | no |  |  |
| `height` | number | no |  |  |
| `depth` | number | no |  |  |
| `preview` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Perspective (camera) 3D views | Revit API: View3D.CreatePerspective; ViewOrientation3D. Check all its members in one call: `search_api_docs` with query `View3D.CreatePerspective; ViewOrientation3D`, then use `execute_csharp` within the requested scope. |
| Elevation, drafting and legend views | Revit API: ElevationMarker.CreateElevationMarker; ViewDrafting.Create. Check all its members in one call: `search_api_docs` with query `ElevationMarker.CreateElevationMarker; ViewDrafting.Create`, then use `execute_csharp` within the requested scope. |
| View visibility and graphics (hiding, overrides, crop regions) | Revit API: View.SetCategoryHidden; View.HideElements; View.SetElementOverrides; View.CropBox. Check all its members in one call: `search_api_docs` with query `View.SetCategoryHidden; View.HideElements; View.SetElementOverrides; View.CropBox`, then use `execute_csharp` within the requested scope. |
| Deleting views | Tool: delete_elements |
<!-- generated:contract:end -->

## Inputs and preconditions

Follow [execution rules](../execution-rules.md). Discover compatible `ViewFamilyType` IDs using `get_element_types` with `of_class: "ViewFamilyType"`; find level and existing view IDs with `get_elements`.

| Input | Meaning |
| --- | --- |
| `action` | Required: `create_plan`, `create_3d`, `create_section`, `duplicate`, or `update`. |
| `view_family_type_id` | Compatible positive type ID, required for creation actions. A 3D view is isometric. |
| `level_id` | Required positive level ID for `create_plan`. |
| `view_id` | Required positive existing view ID for duplicate/update. |
| `duplicate_option` | `duplicate` (default), `with_detailing`, or `dependent`; the view must support the selected option. |
| `name` | Optional nonempty view name, applied in the same step. A name another view of the same type already uses is rejected: the failed row carries `name_collision` with the existing view's ID, and nothing is renamed or created. |
| `scale` | Optional integer 1–24000; rejected when unavailable or controlled by the view template. |
| `view_template_id` | Optional compatible template ID, or `-1` to intentionally remove a template. The tool does not remove a template implicitly to change scale. |
| `unit`, `origin`, `viewing_direction`, `up`, `width`, `height`, `depth` | Required for a section; details below. |
| `preview` | Default `false`; commit-validates then rolls back the model changes. |
| `expected_document_id` | Required for all calls, including previews; exact current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

A section uses document internal coordinates. `unit` is `millimeters`, `centimeters`, `meters`, `feet`, or `inches`; it applies to finite `[x,y,z]` `origin` and all dimensions. `viewing_direction` and `up` are nonzero, orthogonal dimensionless vectors. Width/height extend symmetrically around the origin; depth extends along the viewing direction. Dimensions must be positive and exceed 0.000001 feet after conversion. These are model coordinates, not sheet placement coordinates.

## Example

IDs `34567` and `45678` are illustrative; discover the compatible plan type and level first.

```json
{
  "action": "create_plan",
  "view_family_type_id": 34567,
  "level_id": 45678,
  "name": "Room review plan",
  "scale": 50,
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. The step returns `before` where relevant, a `view` description (ID, unique ID, name, type, template, scale), `created`, and `id_is_temporary`.

A duplicate carries its source's visibility and graphics. Its row reports `inherited_state`: `derived_from`, `template`, `hidden_categories` (`OST_` identity and localized name), `hidden_elements` (count, sample IDs, whether the bounded scan completed), `filters` with their visibility, element and category override counts, and display settings. Compare it with the request before reporting: unhide what the request asks to show (View.UnhideElements, View.SetCategoryHidden through the API route), or disclose what stays hidden. The result's `model_changes` lists what the call added and modified.

A `name_collision` means the named view existed before this call. Do not edit, rename, reuse or delete it to get past the collision; ask the user, or choose a distinct name and report the collision.

Created/duplicated preview IDs are temporary; never use them in a later tag, placement, or capture call. Commit first and use the committed ID. A failure in name, template, scale, or creation rolls back the single step. Read back the committed view settings and use [visual verification](../visual-verification.md) to inspect framing, orientation, crop, and readability.

Follow [operation recovery](../operation-recovery.md) for an uncertain outcome. A real creation after a preview uses a new operation ID. Creating a view does not activate it or save the model.
