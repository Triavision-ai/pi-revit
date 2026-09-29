# capture_view

## Purpose and boundaries

Export a temporary PNG of a graphical Revit view or sheet for visual inspection. The response contains a file path and metadata, never image data. Open the returned `filePath` with Pi's image-capable read tool to see the result. Capturing alone does not perform visual verification.

Contract: PI-Revit 0.5.0 source, [CaptureView.cs](../../../../src/Revit/Tools/CaptureView.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** files. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `inspect_output`: open and inspect the produced file.
- **Contract hash:** `f8989e7bb68815d2`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `view_id` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

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
| `expected_document_id` | Optional exact current overview identity; always checked if supplied. Supplying it binds the capture to the intended model. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Revit must have an open document. Explicit view templates and schedules are rejected; use `get_schedules` for schedule data or capture a sheet hosting the schedule when its layout matters. Discovery uses `get_elements`; an ID from a rolled-back preview is invalid.

Follow [execution rules](../execution-rules.md) and [visual verification](../visual-verification.md). Frame the actual view so the requested content is readable. An `open_view` activation is queued; a same-batch capture based on active view can show the previous view. Use the explicit target ID in a subsequent call.

## Example

ID `23456` is illustrative; replace it with the intended committed view/sheet ID.

```json
{
  "view_id": 23456,
  "expected_document_id": "<project.documentId>"
}
```

## Results, effects and verification

The result contains `filePath`, `viewId`, `viewName`, `width`, `height`, and `fileSizeBytes`. Images are fitted to a maximum 1568-pixel long edge; a later export/readable close-up may be needed to inspect dense sheets. This is Revit's image export, not a screenshot of UI chrome or a proof of selection highlighting.

The tool writes files and performs no model transaction or model save. Captures normally live in `%LOCALAPPDATA%\pi-revit\captures` with a temporary-directory fallback. Later captures best-effort prune owned PNGs older than 24 hours. Retain required evidence in the task's evidence location before relying on long-term availability.

Open the image and inspect visibility, positioning, readability, clipping, and overlaps. A blank or tiny drawing is insufficient proof. Retain before/after images when useful, and report verification as incomplete if inspection is unavailable. Do not infer appearance from dimensions or filenames.

The call budget is 120 seconds. Follow [operation recovery](../operation-recovery.md) for uncertain outcomes; an identical retry may return the original capture result, not a fresh image of later model changes. Capturing evidence does not authorize saving the Revit model.
