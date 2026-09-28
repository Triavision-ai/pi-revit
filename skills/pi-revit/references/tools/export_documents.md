# export_documents

## Purpose and boundaries

Export explicit sheets/views to PDF, DWG, or PNG, or export the model/one view's content to IFC. This tool writes files; IFC also commits model-side IFC GUID changes in a backend-owned transaction. There is no preview mode, no general file rollback, and no automatic Revit model save.

Contract: PI-Revit 0.4.0 source, [ExportDocuments.cs](../../../../src/Revit/Tools/ExportDocuments.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model, files. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `inspect_output`: open and inspect the produced file.
- **Contract hash:** `09e7464757ff2e48`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `format` | string | yes |  | `pdf`, `dwg`, `png`, `ifc` |
| `ids` | array of integer | no |  |  |
| `output_dir` | string | no |  |  |
| `file_name_prefix` | string | no |  |  |
| `combine` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Formats other than PDF, DWG, PNG and IFC | Revit API: Document.Export overloads (e.g. DXFExportOptions, NavisworksExportOptions); verify the installed exporter. Check all its members in one call: `search_api_docs` with query `Document.Export; NavisworksExportOptions`, then use `execute_csharp` within the requested scope. |
| Printing to a physical printer | Revit API: PrintManager. Check all its members in one call: `search_api_docs` with query `PrintManager`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Exports and destinations must be within the user's requested scope. Read [execution rules](../execution-rules.md), discover target IDs with `get_elements`, and verify the intended model and view/sheet contents before delivery.

| Input | Meaning |
| --- | --- |
| `format` | Required: `pdf`, `dwg`, `png`, or `ifc`. |
| `ids` | PDF/DWG/PNG require 1–100 view/sheet IDs. IFC accepts at most one view ID; omission exports the whole model. Templates are rejected; PNG also rejects schedules. |
| `output_dir` | Optional explicit destination, created if absent. Omit for the model-specific default described below. |
| `file_name_prefix` | Optional base name, default document title; invalid filename characters are sanitized. Revit appends view/sheet suffixes for multi-file exports. |
| `combine` | PDF only: default true combines sheets/views into one PDF. False uses Revit's per-view naming rules. |
| `expected_document_id` | Required for every format, including PDF/PNG; use the intended model's exact current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

PNG uses a 2048-pixel horizontal fit; this differs from `capture_view`'s 1568-pixel long-edge cap. DWG availability depends on the Revit installation. Remaining export eligibility and format behavior are validated by Revit.

The default folder is `Documents\pi-revit\Models\<model title>--<identity hash>\exports`. Saved file paths or cloud/server identity determine the model folder; unsaved/unavailable identities use a session fallback. Save As can change the folder. The hash is not `project.documentId`. Use returned `outputDir` and file paths as authoritative; never reconstruct them from the title or opaque document ID. Existing title-only folders remain untouched.

## Example

Export only when requested. IDs `23456` and `23457` are illustrative committed sheet IDs; replace them with verified targets.

```json
{
  "format": "pdf",
  "ids": [23456, 23457],
  "combine": true,
  "file_name_prefix": "Room documentation",
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

The result contains `format`, `outputDir`, `fileCount`, `files` with `path`/`fileSizeBytes`, and `commitWarnings`. New/changed files are detected by comparing output-directory timestamps; inspect actual returned paths and expected artifacts, especially in a shared destination. Existing filenames may be overwritten.

A failed export may leave partial files; errors identify observed changed files where possible. IFC model rollback does not remove already-written files. Report IFC warnings and inspect actual effects. No files are automatically removed on export failure.

Follow [operation recovery](../operation-recovery.md) after timeout/cancellation; the 120-second call budget does not prove export stopped. Do not retry with a new ID until the original outcome and output files are understood.

Verify that each expected file exists, is usable, and represents the requested scope. For drawings, open/render the actual exported artifact and follow [visual verification](../visual-verification.md), including readability and page completeness. A path or nonzero file size is not sufficient visual proof. Exporting evidence does not authorize saving the model.
