# Changelog

All notable changes to pi-revit are documented here, newest first.
Format: one `## [x.y.z] - YYYY-MM-DD` section per released version with
`### New Features` / `### Added` / `### Changed` / `### Fixed` subsections,
written for end users (what changed in *their* workflow, not internals).

## [0.2.3] - 2026-07-20

### Fixed

- Fixed `search_api_docs` results so same-named members from different namespaces are labeled with their full container path, instead of rendering as identical lines.
- Removed a hardcoded overload-targeting example from the search results note; the example is now cut from the actual displayed signature.

## [0.2.2] - 2026-07-20

### New Features

- **Overload-aware API docs search** — `search_api_docs` now ranks same-named overloads simplest-first (so `Wall.Create` leads with its simplest form, not XML file order), shows exception documentation for the top match, and supports targeting a specific overload by continuing the query past a parenthesis with its parameter types, e.g. `Wall.Create(Document, Curve`.

## [0.2.1] - 2026-07-16

### Added

- Added a version handshake between the Pi extension and the Revit add-in: after `pi update --extensions`, a mismatch (extension updated but Revit still running the old add-in) is reported once per session with the exact command to complete the update.

## [0.2.0] - 2026-07-15

### Changed

- The pi-revit skill now teaches agents that `search_api_docs` answers with remarks, parameter, and return docs on the top match (no more falling back to web search for signatures), and that `export_documents` files output under `Models\<model title>\exports` automatically.

## [0.1.9] - 2026-07-15

### New Features

- **Richer API docs search** — the index now covers every public Revit API enum value, surfaces Autodesk's remarks, and prints the top match's full documentation (remarks, parameters, returns) directly in the search result text.

## [0.1.8] - 2026-07-13

### Added

- Session files now sort deterministically into per-project folders, and model output (exports, captures, scripts) is filed automatically under `Models\<model title>` in the workspace.

## [0.1.7] - 2026-07-13

### Fixed

- Fixed the npm-install uninstall flow blocking itself on its own folder.

## [0.1.6] - 2026-07-13

### Fixed

- The global launcher is now installed next to the user's permanent Pi installation under npx, instead of a temporary path.

## [0.1.5] - 2026-07-13

### Fixed

- Fixed uninstall of the npm-installed Pi package.
- Fixed workspace paths containing non-ASCII characters.

## [0.1.4] - 2026-06-18

### Fixed

- Windows install docs now use `npx.cmd`, which works from any shell.

## [0.1.3] - 2026-06-17

### Fixed

- Fixed the npx installer on Windows.

## [0.1.2] - 2026-06-17

### Fixed

- Normalized the npm package manifest.

## [0.1.1] - 2026-06-17

### Added

- One-command npm installer (`npx pi-revit`) and install documentation.

## [0.1.0] - 2026-06-17

### New Features

- **First public npm release** — the Revit bridge add-in (Revit 2025/2026/2027) plus the Pi extension with the full toolset: `ping`, `get_model_overview`, `get_elements`, `get_element_details`, `get_element_types`, `manage_selection`, `set_parameters`, `search_api_docs`, `execute_csharp`, `capture_view`, `export_documents`, and `get_model_health`.
