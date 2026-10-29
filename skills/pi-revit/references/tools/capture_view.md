# capture_view

## Purpose and boundaries

Export a short-lived PNG (kept 7 days) of a graphical Revit view or sheet for visual inspection. The response contains a file path and metadata, never image data. Open the returned `filePath` with Pi's image-capable read tool to see the result. Capturing alone does not perform visual verification.

Contract: PI-Revit 0.7.0 source, [CaptureView.cs](../../../../src/Revit/Tools/CaptureView.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** files. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `inspect_output`: open and inspect the produced file.
- **Contract hash:** `3b1b2f4fbc01bf23`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `view_id` | integer | no |  |  |
| `max_dimension` | integer | no | `1568` |  |
| `scope` | string | no | `"entire_view"` | `entire_view`, `visible_region` |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Image contents or visual judgement (returns a file path only) | Tool: read (open the returned PNG) |
| Deliverable PDF, DWG or IFC files | Tool: export_documents |
<!-- generated:contract:end -->

## Inputs and preconditions

| Input | Meaning |
| --- | --- |
| `view_id` | Optional existing graphical view/sheet ID; defaults to the active view. Use an explicit committed ID for reproducible evidence. |
| `max_dimension` | Optional integer 256–4096, default 1568. Maximum long image edge. Use a smaller capture for a quick look; frame a close-up and increase resolution when details need it. |
| `scope` | `entire_view` (default) exports the whole requested view. `visible_region` exports the current UI zoom of the active view only. If `view_id` is supplied it must match the active view; the tool does not activate views. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Revit must have an open document. Explicit view templates and schedules are rejected; use `get_schedules` for schedule data or capture a sheet hosting the schedule when its layout matters. Discovery uses `get_elements`; an ID from a rolled-back preview is invalid.

Follow [execution rules](../execution-rules.md) and [visual verification](../visual-verification.md). For a focused capture, open the view, wait until activation is reflected by `get_model_overview` or another read, zoom with `manage_selection`, then call `capture_view` with `scope: "visible_region"` and the active `view_id`. An `open_view` activation is queued; a same-batch capture can show the previous view. For an overview, use `entire_view` and an explicit view ID.

## Example

ID `23456` is illustrative; replace it with the intended committed view/sheet ID.

```json
{
  "view_id": 23456
}
```

## Results, effects and verification

The result contains `filePath`, `viewId`, `viewName`, `width`, `height`, `maxDimension`, `scope`, and `fileSizeBytes`. Images are fitted to the requested maximum long edge (default 1568 pixels). For example, `{"view_id":23456,"max_dimension":1000}` requests an economical overview; `{"view_id":23456,"scope":"visible_region","max_dimension":2000}` requests the current UI close-up when that view is active. Replace the illustrative ID with the intended committed view. The 4096-pixel bound is a tool resource limit, not a Revit maximum. This is Revit's image export, not a screenshot of UI chrome or a proof of selection highlighting.

The tool writes files and performs no model transaction or model save. Captures normally live in the workspace temp folder, `Documents\pi-revit\temp\captures`, with a temporary-directory fallback. Later captures best-effort prune owned PNGs older than 7 days. Retain required evidence in the task's evidence location before relying on long-term availability.

Open the image and inspect visibility, positioning, readability, clipping, and overlaps. A blank or tiny drawing is insufficient proof. Retain before/after images when useful, and report verification as incomplete if inspection is unavailable. Do not infer appearance from dimensions or filenames.

The call budget is 120 seconds. Follow [operation recovery](../operation-recovery.md) for uncertain outcomes; an identical retry may return the original capture result, not a fresh image of later model changes. Capturing evidence does not authorize saving the Revit model.
