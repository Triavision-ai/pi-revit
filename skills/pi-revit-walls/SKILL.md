---
name: pi-revit-walls
description: Plan, inspect, create, coordinate, and verify Autodesk Revit walls with PI-Revit. Use for Basic, Curtain, or Stacked walls; straight or curved paths; layers, core and materials; alignment, constraints, profiles and attachments; joins, openings, hosted inserts, phases, dimensions, and quantities.
---

# Walls with PI-Revit

Use this skill for wall-specific work. General wall questions need no model connection. For model work, read the shared [execution rules](../pi-revit/references/execution-rules.md). The selected bridge's live schema is authoritative; read its compatible manual for dedicated tool behavior. Autodesk documentation describes API routes and design concepts, not automatic tool support.

## Establish the design and model context

Identify the intended model, wall kind and type, path and coordinate frame, length unit, exterior side and location plane, base and top, cross section, phase or design option, and affected neighbors, rooms or inserts. Discover model-specific IDs. Use canonical `BuiltInCategory`, `BuiltInParameter`, `ForgeTypeId`, or GUID identifiers for code; localized names are display data, not selectors. Obtain thickness, layer materials and structural intent from the request or model, without assuming country-specific defaults.

| Need | Read |
| --- | --- |
| Basic, Curtain and Stacked systems; type edits; compound layers, core, materials and wrapping | [Wall systems](references/wall-systems.md) |
| Line, arc and other paths; alignment; base/top; slanted, tapered and profile shapes; attachment, joins and openings | [Wall geometry](references/wall-geometry.md) |
| Audits, committed-result checks, dimensions, material quantities and visual evidence | [Wall verification](references/wall-verification.md) |

## Choose execution from the current contract

1. Select the intended Revit session and start an unfamiliar model with `get_model_overview`; obtain a fresh overview before a write, preview, export or view change. Inspect the existing model and use `find_revit_tools` to discover dedicated tools. Read the returned manual paths and live schemas, especially [create_walls](../pi-revit/references/tools/create_walls.md).
2. Use a dedicated tool for a requested operation when its selected schema and manual cover it. Discover `WallType` and level IDs from the target model; use explicit units and the tool's coordinate contract. Batch compatible rows, preview when useful, and choose atomic behavior deliberately. Keep type-level edits separate from instance placement.
3. For a wall feature the dedicated tool does not cover, check the installed API signatures with `search_api_docs` before `execute_csharp`. Autodesk routes include `Wall.Create` with a curve or elevation profile, `Wall.CrossSection`, profile sketches, wall attachments, `CurtainGrid`, stacked members, `WallUtils`, openings, and hosted family instances. Check Revit-build availability and API preconditions for each route.
4. Read every result fully; for a `result_id`, join all `read_revit_result` fragments in offset order. Distinguish committed `succeeded` rows from rolled-back `proposed` IDs and `failed` rows, inspect warnings and `model_changes`, then independently reread retained elements. Capture a relevant view when shape or alignment requires visual confirmation. A committed transaction does not save the RVT file. Follow [operation recovery](../pi-revit/references/operation-recovery.md) before retrying an uncertain outcome.

## Evidence boundary

The earlier expanded `create_walls` implementation was live checked in disposable Revit 2025.4 for straight vertical Basic walls, six location-line modes, constraints, preview, commit and rollback. A separate rolled-back 2025.4 API probe accepted line, circular arc, elliptical arc and vertical profile creation with existing Basic, Curtain and Stacked types, and single-slanted line/arc cases. Its fixture types rejected tapered creation, which requires a suitable type; those probes do not establish dedicated tool coverage or persistent edits. Revit 2026 and 2027 remain untested by this evidence. Recheck the selected bridge's contract and the requested behavior rather than extrapolating from these cases.
