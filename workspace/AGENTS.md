# pi-revit workspace

This folder tree is the working area for Pi + Revit sessions. The pi-revit tools talk to the
Revit bridge add-in; Revit 2025 must be running with a project open (only `ping` and
`search_api_docs` work without a document).

## Conventions

- One subfolder per Revit project under `Projects\`; start Pi from inside the project folder so
  sessions, notes, and outputs stay scoped to that project.
- Save exports, captured view PNGs, scripts, and analysis results inside the current project
  folder (e.g. an `exports\` subfolder) — not in temp, not in the model's directory.
- Keep per-project knowledge (naming conventions, known model quirks, decisions) in the project
  folder's own `AGENTS.md` so every session starts informed.

## Tool habits

- Start unfamiliar models with `get_model_overview`; use `get_elements` for any listing or
  counting; read parameter values with `get_element_details`.
- Before `execute_csharp`, verify unfamiliar API signatures with `search_api_docs`.
- Write tools (`set_parameters`, `execute_csharp`) change the real model — state clearly what
  was changed. `set_parameters` commits partial successes: always check its `failed` list.
  A failed `execute_csharp` rolls back entirely and reports the error.
