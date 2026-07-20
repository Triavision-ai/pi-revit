# Changelog

All notable changes to pi-revit are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/); version headers use
`## [x.y.z] - YYYY-MM-DD` so tooling (and Pi's changelog parser format) can read them.

Every published version gets an entry with **Added** / **Changed** / **Fixed** sections
describing what the user will notice — not internal refactors.

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
