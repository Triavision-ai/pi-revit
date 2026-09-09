# pi-revit workspace

This folder tree is the working area for Pi + Revit sessions. The pi-revit tools talk to the
Revit bridge add-in, targeting Revit 2025, 2026, and 2027. The 0.3.0 changes were
tested live on Revit 2025. Bridge document tools require a project open; `ping` and
`search_api_docs` work without a document. `read_revit_result` reads saved local
results without contacting Revit.

## File rules — where every file goes

Work sorts by model, automatically. The export tool files its output under the model it came
from — never a decision for you or the user to make:

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
3. **Never create files loose in the workspace root.** The root holds `AGENTS.md`,
   `pi-revit.cmd`, `Models\`, and Pi's own session data — nothing else, ever.

## Tool habits

- Start unfamiliar models with `get_model_overview`; use `get_elements` for any listing or
  counting; read parameter values with `get_element_details`.
- Copy the intended model's `project.documentId` unchanged into `expected_document_id`
  for `set_parameters`, `execute_csharp`, `export_documents`, `open_view`, and
  selection/zoom changes. Any `manage_selection` call with `isolate_in_view: true`
  also requires it, including action `get`. Pure reads may omit it; supplied IDs
  are checked. Legacy `expected_document` titles alone do not satisfy the guard.
  After a rejection, verify the intended model before refreshing its ID. Closing
  and reopening a model or restarting Revit invalidates earlier IDs.
- Before `execute_csharp`, verify unfamiliar API signatures with `search_api_docs`.
- Write tools (`set_parameters`, `execute_csharp`) change the real model — state clearly what
  was changed. `set_parameters` commits partial successes: always check its `failed` list.
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

After installing 0.3.0, deploy the matching add-in with Revit closed, restart Revit,
and start a fresh Pi session to load the new tool schemas. Obtain a new overview
before changing a document. Setup preserves existing `AGENTS.md`; older workspaces
need these exact-ID, saved-result, and identity-derived folder rules merged into
their existing instructions.
