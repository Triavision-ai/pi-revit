# pi-revit workspace

This folder tree is the working area for Pi + Revit sessions. The pi-revit tools talk to the
Revit bridge add-in; Revit 2025, 2026, or 2027 must be running with a project open
(only `ping` and `search_api_docs` work without a document).

## File rules — where every file goes

Work sorts by model, automatically. The export tool files its output under the model it came
from — never a decision for you or the user to make:

```text
Documents\pi-revit\
├─ AGENTS.md            <- this file
├─ pi-revit.cmd         <- double-click launcher
└─ Models\
   └─ <model title>\    <- created automatically by the tools, one folder per Revit model
      ├─ model.txt      <- the model's identity (GUID + file path), written by the add-in
      ├─ exports\       <- export_documents output (its default)
      ├─ captures\      <- view snapshots worth keeping
      └─ scripts\       <- generated scripts and analysis for that model
```

1. **Let exports sort themselves**: call `export_documents` without `output_dir` — files land
   in `Models\<model title>\exports` automatically, keyed to the exported document. Pass
   `output_dir` only when the user names a different target.
2. **Anything else you produce about a model goes into that model's folder**: view captures the
   user wants to keep in `Models\<model title>\captures` (`capture_view` writes to temp — copy
   the PNG over; the model title comes from `get_model_overview`), scripts and analysis in
   `Models\<model title>\scripts`. Create these subfolders on first use.
3. **Never create files loose in the workspace root.** The root holds `AGENTS.md`,
   `pi-revit.cmd`, `Models\`, and Pi's own session data — nothing else, ever.

## Tool habits

- Start unfamiliar models with `get_model_overview`; use `get_elements` for any listing or
  counting; read parameter values with `get_element_details`.
- Before `execute_csharp`, verify unfamiliar API signatures with `search_api_docs`.
- Write tools (`set_parameters`, `execute_csharp`) change the real model — state clearly what
  was changed. `set_parameters` commits partial successes: always check its `failed` list.
  A failed `execute_csharp` rolls back entirely and reports the error.
