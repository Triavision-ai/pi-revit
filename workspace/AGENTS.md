# pi-revit workspace

This folder tree is the working area for Pi + Revit sessions. The pi-revit tools talk to the
Revit bridge add-in; Revit 2025, 2026, or 2027 must be running with a project open
(only `ping` and `search_api_docs` work without a document).

## File rules — where every file goes

The workspace layout is fixed:

```text
Documents\pi-revit\
├─ AGENTS.md          <- this file
├─ pi-revit.cmd       <- double-click launcher
└─ Projects\
   └─ <project>\      <- one folder per Revit project (`pi-revit <project>` creates it)
      ├─ AGENTS.md    <- that project's knowledge
      ├─ exports\     <- export_documents output (the default inside a project)
      ├─ captures\    <- view snapshots worth keeping
      └─ scripts\     <- generated scripts and analysis code
```

1. **Never create files in the workspace root.** The root contains `AGENTS.md`, `pi-revit.cmd`,
   and `Projects\` — nothing else, ever.
2. **If the session starts at the workspace root**, first pick a project: list `Projects\` and
   ask which one (or create `Projects\<name>` for a new one), then work inside that folder.
3. **Everything you produce goes inside the current project folder**: exports in `exports\`,
   view captures the user wants to keep in `captures\` (`capture_view` writes to temp — copy
   the PNG over), scripts and analysis in `scripts\`. Create these subfolders on first use.
4. **Keep per-project knowledge** (naming conventions, known model quirks, decisions) in the
   project folder's own `AGENTS.md` so every session starts informed.

## Tool habits

- Start unfamiliar models with `get_model_overview`; use `get_elements` for any listing or
  counting; read parameter values with `get_element_details`.
- Before `execute_csharp`, verify unfamiliar API signatures with `search_api_docs`.
- Write tools (`set_parameters`, `execute_csharp`) change the real model — state clearly what
  was changed. `set_parameters` commits partial successes: always check its `failed` list.
  A failed `execute_csharp` rolls back entirely and reports the error.
