# Changelog

All notable changes to pi-revit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); version headers use
`## [x.y.z] - YYYY-MM-DD` so tooling (and Pi's changelog parser format) can read them.

Every published version gets an entry with **Added** / **Changed** / **Fixed** sections
describing what the user will notice — not internal refactors.

## [0.2.9] - 2026-07-21

### Added
- Self-healing tool discovery: when pi starts before Revit, the extension now keeps
  retrying tool discovery in the background (every 15s) and also re-discovers on a
  successful `ping` — no more sessions stuck with only `ping` registered until a fresh
  pi start. When tools arrive mid-session, `ping`'s result says so.
- `set_parameters` and `execute_csharp` accept an optional `expected_document` (the model
  title): if the active document differs — e.g. the user switched models mid-session —
  the write fails cleanly instead of landing in the wrong model.
- `get_element_details.parameter_names` now also matches language-independent
  BuiltInParameter enum names (e.g. `ALL_MODEL_MARK`), so filtering works in non-English
  Revit UIs where display names are localized.

### Fixed
- `get_element_details` no longer reports a misleading "0 params" when a
  `parameter_names` filter simply matched nothing — it now reports "N of M params
  matched parameter_names" so localization misses are visible. (This explains the
  earlier "0 params vs 38 params" reports: different filter arguments, not flaky reads.)
- `get_elements`: a display-name filter rule in an **unscoped** query (no category /
  of_class) is no longer promoted to a pinned collector filter based on a 50-element
  probe — it stays on the per-element post-scan path, so categories beyond the probe
  window can't be silently dropped when the same parameter name maps to different ids.

### Changed
- SKILL.md: guidance on localized parameter names (prefer BuiltInParameter enum names)
  and on using `expected_document` for long sessions / multiple open models.
- README: new "Safety model" section stating explicitly what the add-in enforces and
  that write-confirmation UX is a client-side decision.

Requires redeploying the Revit add-in (`scripts\deploy.ps1` with Revit closed, then
restart Revit).

## [0.2.8] - 2026-07-21

### Fixed
- `search_api_docs`: overload-targeted queries no longer fail on comma spacing.
  `Wall.Create(Document,Curve` (no space) and `Wall.Create( Document, Curve` now match the
  same overloads as `Wall.Create(Document, Curve` — the query's spacing around commas and
  parentheses is normalized to the rendered signature style before matching.

Requires redeploying the Revit add-in (`scripts\deploy.ps1` with Revit closed, then restart
Revit).

## [0.2.7] - 2026-07-21

### Added
- Update announcements: after a pi-revit update, the next pi session shows a one-time
  "What's new in pi-revit" note listing the changelog entries for every version since the
  last one announced — the same mechanism pi uses for its own updates. The last-announced
  version is remembered in `%APPDATA%\pi-revit\state.json` (kept outside the package
  folder so npm updates can't erase it). Fresh installs stay silent.

Extension-only change: no Revit add-in redeploy or Revit restart needed.

## [0.2.6] - 2026-07-20

### Fixed
- Write transactions (`set_parameters`, `execute_csharp`, and the temporary-isolate
  branch of `manage_selection`) now register a failures preprocessor. Previously Revit
  handled commit failures interactively: warnings popped the transient toast and spammed
  the journal, and an error-severity failure showed the modal resolution dialog, blocking
  the bridge until a human clicked. Now warnings are auto-dismissed and reported back
  (`commitWarnings` in the result, e.g. duplicate Mark values), and errors roll the
  transaction back with the actual Revit failure text in the error message.

### Changed
- `set_parameters` tool description tells the model to relay `commitWarnings` to the user.

Requires redeploying the Revit add-in (`scripts\deploy.ps1` + Revit restart).

## [0.2.5] - 2026-07-20

### Fixed
- `execute_csharp` dialog guard no longer answers every Revit popup with OK. On some
  dialogs OK is the destructive choice (e.g. "Delete Element(s)"), so a script could
  silently delete dimensions or constraints and still report success. Unrecognized
  dialogs are now answered dismissively (Cancel, then Close, then No; OK only as the
  last resort so Revit can never hang behind a popup), a small allowlist keeps OK for
  dialogs that are safe to confirm, and `suppressedDialogs` now reports which answer
  was given (e.g. `TaskDialog_… (answered Cancel)`).

### Changed
- The `execute_csharp` tool description tells the model that confirmation prompts may be
  cancelled and to check `suppressedDialogs` when a result looks incomplete.

Requires redeploying the Revit add-in (`scripts\deploy.ps1` + Revit restart) — `ping`
warns on a version mismatch until then.

## [0.2.4] - 2026-07-20

### Added
- This changelog. It ships inside the npm package and gets a `## [x.y.z]` entry with
  **Added / Changed / Fixed** sections for every release, so updates report what actually
  changed instead of just a version number.

## [0.2.3] - 2026-07-20

### Changed
- `search_api_docs`: results with the same short name in different namespaces are now
  disambiguated with their full namespace, so `Category` vs internal schedule types
  can't be confused.

### Fixed
- Removed a hardcoded example from the overload note that could mislead the model into
  copying a signature that didn't apply.

## [0.2.2] - 2026-07-20

### Added
- `search_api_docs`: exception documentation ("Throws:") is now shown for matched members.
- Overload-targeted queries: a query containing `(` matches against rendered signatures.

### Changed
- Overloads now rank simplest-first (fewest parameters), so the common form appears on top.

## [0.2.1] - 2026-07-16

### Added
- Extension/add-in version handshake: `ping` reports both versions and warns when the
  npm extension and the installed Revit add-in are out of sync (partial-update detection).

## [0.2.0] - 2026-07-15

### Changed
- SKILL.md: documents the top-match inline docs behaviour and the automatic
  `Models\<title>\exports` output location so the agent uses them without prompting.

## [0.1.9] - 2026-07-15

### Added
- `search_api_docs` indexes every Revit API enum and surfaces `<remarks>` documentation.

### Changed
- The top match's full documentation is placed directly in the tool's text output
  (where model attention is strongest) instead of only in the structured payload.

## [0.1.8] - 2026-07-13

### Added
- Automatic per-model output sorting: exports, captures, and scripts land under
  `Models\<model title>\` in the workspace, keyed to the source document.

## [0.1.7] - 2026-07-13

### Fixed
- npm-install uninstall flow no longer blocks itself on its own installation folder.

## [0.1.6] - 2026-07-13

### Changed
- The global launcher installs next to the user's permanent pi under npx.

## [0.1.5] - 2026-07-13

### Fixed
- Uninstall of the npm-installed Pi package.
- Workspace paths containing non-ASCII characters.

First version published to npm.

## [0.1.0] - 2026-06-17

Initial public release: Revit bridge add-in (Revit 2025/2026/2027) plus Pi extension with
`ping`, `get_model_overview`, `get_elements`, `get_element_details`, `set_parameters`,
`manage_selection`, `capture_view`, `export_documents`, `execute_csharp`, and
`search_api_docs`.
