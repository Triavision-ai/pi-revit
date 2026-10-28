# export_documents

## Purpose and boundaries

Export explicit sheets/views to PDF, DWG, or PNG, or export the model/one view's content to IFC. This tool writes files; IFC also commits model-side IFC GUID changes in a backend-owned transaction. There is no preview mode, no general file rollback, and no automatic Revit model save.

Contract: PI-Revit 0.5.0 source, [ExportDocuments.cs](../../../../src/Revit/Tools/ExportDocuments.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model, files. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `inspect_output`: open and inspect the produced file.
- **Contract hash:** `3d70767746f4f3e0`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `format` | string | yes |  | `pdf`, `dwg`, `png`, `ifc` |
| `ids` | array of integer | no |  |  |
| `output_dir` | string | no |  |  |
| `file_name_prefix` | string | no |  |  |
| `combine` | boolean | no |  |  |
| `png_pixel_width` | integer | no | `2048` |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Formats other than PDF, DWG, PNG and IFC | Revit API: Document.Export overloads (e.g. DXFExportOptions, NavisworksExportOptions); verify the installed exporter. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
| Printing to a physical printer | Revit API: PrintManager. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Exports and destinations must be within the user's requested scope. Read [execution rules](../execution-rules.md), discover target IDs with `get_elements`, and verify the intended model and view/sheet contents before delivery.

| Input | Meaning |
| --- | --- |
| `format` | Required: `pdf`, `dwg`, `png`, or `ifc`. |
| `ids` | PDF/DWG/PNG require 1–100 view/sheet IDs. IFC accepts at most one view ID, which must be a model view (floor, ceiling, structural or area plan, elevation, section, detail or 3D view; a sheet, schedule, legend or drafting view is refused because it would yield an empty IFC); omission exports the whole model. Templates are rejected; PNG also rejects schedules. |
| `output_dir` | Optional explicit destination, created if absent. Must be an absolute (fully qualified) path: a relative path would resolve against Revit's own working directory and is refused. Omit for the model-specific default described below; an empty or blank value is refused rather than treated as the default. |
| `file_name_prefix` | Optional base name, default document title; invalid filename characters are sanitized. Revit appends view/sheet suffixes for multi-file exports. |
| `combine` | PDF only: default true combines sheets/views into one PDF. False uses Revit's per-view naming rules. |
| `png_pixel_width` | PNG only: integer 64–8192, default 2048. Requested horizontal width; height follows the view aspect ratio, so portrait images may be taller. Supplying this option for another format is rejected before files are written. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

PNG uses a horizontal fit, with `png_pixel_width` controlling the requested width. For example, use `{"format":"png","ids":[23456],"png_pixel_width":3200}` for a presentation export, replacing the illustrative ID with a discovered view. The 8192-pixel bound is a tool resource limit, not a claim about Revit's maximum. Larger exports use more time and memory. The PNG result reports the requested `pngPixelWidth`; inspect the actual artifact to verify its dimensions. `capture_view` instead bounds the long edge for inspection.

DWG availability depends on the Revit installation. PDF and DWG are refused, before anything is written, when a target view (or a view placed on a target sheet) is in temporary hide/isolate mode, because Revit would otherwise stop on a modal dialog until a person clicks it; clear it with `manage_selection` action `clear` with `isolate_in_view: true` (it acts on the active view, so `open_view` the view first), then retry. PNG is not affected. Remaining export eligibility and format behavior are validated by Revit.

The default folder is `Documents\pi-revit\Models\<model title>--<model creation GUID>\exports`: Revit's creation GUID of the model, so the folder stays when the file is moved, and a Save As under a new name gets a new folder. A document without a creation GUID uses a hash of its saved path or cloud/server identity, and an unsaved one a session fallback. The GUID is not `project.documentId`. Use returned `outputDir` and file paths as authoritative; never reconstruct them from the title or opaque document ID. Existing title-only folders remain untouched.

## Example

Export only when requested. IDs `23456` and `23457` are illustrative committed sheet IDs; replace them with verified targets.

```json
{
  "format": "pdf",
  "ids": [23456, 23457],
  "combine": true,
  "file_name_prefix": "Room documentation"
}
```

## Results, recovery and verification

The result contains `format`, `outputDir`, `fileCount`, `files` with `path`/`fileSizeBytes`/`overwritten`, `companionFiles`, `overwrittenCount`, and `commitWarnings`. An existing file with the same name is replaced without asking; `overwritten: true` marks it. `fileCount` and `files` cover only files of the requested format; other new or changed files Revit wrote alongside (a DWG export adds `.pcp` plot setups and, for sheets, title-block images) are listed in `companionFiles` with the same fields. New/changed files are detected by comparing output-directory timestamps; inspect actual returned paths and expected artifacts, especially in a shared destination. Existing filenames may be overwritten.

A failed export may leave partial files; errors identify observed changed files where possible. IFC model rollback does not remove already-written files. Report IFC warnings and inspect actual effects. No files are automatically removed on export failure.

Follow [operation recovery](../operation-recovery.md) after timeout/cancellation; the 120-second call budget does not prove export stopped. Do not retry with a new ID until the original outcome and output files are understood.

Verify that each expected file exists, is usable, and represents the requested scope. For drawings, open/render the actual exported artifact and follow [visual verification](../visual-verification.md), including readability and page completeness. A path or nonzero file size is not sufficient visual proof. Exporting evidence does not authorize saving the model.
