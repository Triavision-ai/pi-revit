# pi-revit workspace

This folder tree is the working area for Pi + Revit sessions. The pi-revit tools talk to the
Revit bridge add-in, targeting Revit 2025, 2026, and 2027. This file owns workspace
and output conventions. The `pi-revit` skill and focused tool manuals own current
operating guidance. `find_revit_tools` with `scope: "documentation"` returns manual
paths even while Revit is closed. Most bridge tools require a project open; `ping`
and `search_api_docs` need the bridge but no document. `read_revit_result` reads
saved local results without contacting Revit.

## File rules — where every file goes

Work sorts by model, automatically. The export tool files its output under the model it came
from; it is not a decision for you or the user to make:

```text
Documents\pi-revit\
├─ AGENTS.md            <- this file
├─ pi-revit.cmd         <- double-click launcher
└─ Models\
   └─ <model title>--<identity hash>\  <- selected automatically for the exported document
      ├─ model.txt      <- document identity/path marker written by the add-in
      ├─ exports\       <- export_documents output (its default)
      ├─ captures\      <- view snapshots worth keeping
      └─ scripts\       <- generated scripts and analysis for that model
```

1. **Let exports sort themselves**: call `export_documents` without `output_dir` — files land
   in an identity-derived model folder automatically. Treat the returned `outputDir`
   and file paths as authoritative; do not derive the hash from the model title or
   opaque `project.documentId`. Save As can change the destination. Existing title-only
   folders remain untouched. Pass
   `output_dir` only when the user names a different target.
2. **Anything else you produce about a model goes into that model's folder**: view captures the
   user wants to keep in that verified model folder's `captures` subfolder
   (`capture_view` writes to temp), and scripts and analysis in its `scripts`
   subfolder. For a default export, the model folder is the parent of returned
   `outputDir`. If no folder has been established, identify it from existing model
   markers or choose an explicit destination for the task; do not trigger an
   unnecessary export or guess a title-only folder. Create subfolders on first use.
3. **Never create files loose in the workspace root.** <!-- inv:workspace-root-clean --> The root holds `AGENTS.md`,
   `pi-revit.cmd`, `Models\`, and Pi's own session data — nothing else, ever.

## Tool habits

- Distinguish explanations, inspections, and requested changes. General questions
  need not connect to a model. An audit alone does not authorize repairs or exports.
- For model work, use the skill's execution rules, select the intended session,
  and start unfamiliar models with `get_model_overview`. Discover dedicated tools
  and read their returned manual paths and current schemas before using custom code.
- Copy the intended model's `project.documentId` unchanged into `expected_document_id`
  whenever required by the schema/runtime, including writes, previews, export,
  view/selection changes and every `manage_sheet_placements` action. Selection
  isolation also requires it with action `get`. Supplied IDs on reads are checked.
  A title alone is insufficient. On rejection verify the intended target before
  refreshing the ID; reopening a document or restarting Revit invalidates old IDs.
- Before `execute_csharp`, verify unfamiliar API signatures with `search_api_docs`.
- Report actual retained changes. `set_parameters` defaults to partial success;
  previews roll back, and atomic failures roll back the batch. Read the manual and
  distinguish `succeeded`, `proposed`, and `failed`; discard rolled-back IDs.
  Inspect `commitWarnings` and the actual transaction outcome. A failed
  `execute_csharp` attempts rollback; do not assume rollback succeeded. A
  `returnValueError` can follow a committed edit. Earlier UI actions and created
  files can persist after failure. Read reported effects before retrying.
- When a result returns `result_id`, use `read_revit_result` from offset zero and
  follow `next_offset` until `has_more` is false. Join its `text` fragments in order;
  offsets count UTF-16 code units. The returned absolute file path remains a
  fallback for `read` after extension reload while the file exists. Tool query
  pagination and limits still apply separately.

## Upgrade notes

After a package/add-in upgrade, deploy the matching add-in with Revit closed, restart Revit,
and start a fresh Pi session to load the new tool schemas. Obtain a new overview
before changing a document. Setup preserves existing `AGENTS.md`; older workspaces
need updated exact-ID, saved-result, identity-derived folder and guidance-routing
rules merged into their existing instructions. These are upgrade steps, not
actions to perform during an ordinary model question or documentation check.
