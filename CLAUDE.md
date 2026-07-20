# pi-revit — development guide

This repo is the pi-revit package: a Revit bridge add-in (`src/Revit`, C#) plus the Pi
extension (`extensions/pi-revit/index.ts`) and skill (`skills/pi-revit/SKILL.md`) that
expose it to coding agents. `workspace/AGENTS.md` is the *runtime* guidance shipped to
end users' Pi workspaces — do not confuse the two. (Root `/AGENTS.md` is gitignored and
user-local.)

## Release rule: every version bump updates CHANGELOG.md

Any session that bumps `version` in `package.json` MUST add a matching entry at the TOP
of `CHANGELOG.md`, in this exact shape (it is the same format Pi's own changelog parser
reads — `## [x.y.z]` headers delimit entries):

```markdown
## [x.y.z] - YYYY-MM-DD

### New Features

- **Headline feature** — one sentence a Revit user understands.

### Fixed

- What was broken, in terms of user-visible behavior.
```

Rules:

- Use only the subsections you need, from: `### New Features`, `### Added`,
  `### Changed`, `### Fixed`, `### Removed`.
- Write for the end user (what changed in their workflow), not for the diff.
  "search_api_docs now ranks overloads simplest-first", not "refactored Score()".
- One entry per released version — never edit a published version's entry except to
  fix factual errors.
- npm ships `CHANGELOG.md` automatically (npm always includes CHANGELOG files);
  no manifest change is needed.

## Other conventions

- Version bumps are their own commit ("Bump version to x.y.z") after the feature
  commits, mirroring the existing history.
- The extension discovers tool schemas from the bridge at runtime (`GET /tools`), so
  tool descriptions live in the C# `Describe()` output — never duplicate them in
  `index.ts`.
- The skill (`skills/pi-revit/SKILL.md`) must be kept in sync with actual tool
  behavior; when a release changes what a tool returns or where output lands, check
  whether the skill's guidance still holds.
