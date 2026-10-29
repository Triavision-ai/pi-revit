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
2. Use `create_walls` for supported placement with existing Basic, Curtain or Stacked types: line, circular arc, elliptical arc or vertical profile. Discover `WallType` and level IDs from the target model; use explicit units and the tool's coordinate contract. Arc and ellipse input angles are XY polar bearings; the tool converts ellipse bearings to Revit parameters. Path walls need one of `height` or `top_level_id`; a profile defines its own absolute XYZ elevation. **Vertical** Basic line and arc walls can use six location-line planes at the base. Ellipse, profile, slanted and tapered walls require `WallCenterline` in the dedicated tool; another physical-plane alignment needs a separately verified workflow. Curtain grid count requests require the type's `Fixed Number` layout. Cross-section, structural use, room bounding and end joins remain type- and shape-dependent. Batch compatible rows, preview when useful, and choose atomic behavior deliberately. Keep type-level edits separate from instance placement.
3. For a wall feature the dedicated tool does not cover, check the installed API signatures with `search_api_docs` before `execute_csharp`. Autodesk routes include `Wall.Create` with a curve or elevation profile, `Wall.CrossSection`, profile sketches, wall attachments, `CurtainGrid`, stacked members, `WallUtils`, openings, and hosted family instances. Check Revit-build availability and API preconditions for each route.
4. Read every result fully; for a `result_id`, join all `read_revit_result` fragments in offset order. Distinguish committed `succeeded` rows from rolled-back `proposed` IDs and `failed` rows, inspect warnings and `model_changes`, then independently reread retained elements. Capture a relevant view when shape or alignment requires visual confirmation. A committed transaction does not save the RVT file. Follow [operation recovery](../pi-revit/references/operation-recovery.md) before retrying an uncertain outcome.

## Evidence boundary

In disposable Revit 2025.4, the expanded dedicated tool previewed 50 mixed Basic/Curtain/Stacked and line/arc/ellipse/profile rows with 50 `proposed`, zero failed, and a validated rollback. It committed 12 rows covering each of the three kinds and four shapes; all returned `actual` readbacks, and independent element reads checked their parameters, locations and bounds. Separate atomic and partial batches verified rollback versus retained successes. A duplicated Basic type with a variable layer allowed tapered line and arc walls with instance face angles; the tested nonvariable types rejected tapering. Later focused tests verified a 45° ellipse endpoint bearing with unequal radii and Curtain grid counts under `Fixed Number` (four vertical lines and five panels on an arc; four vertical and two horizontal lines with fifteen panels on a line). A `None` layout produced zero grid lines and one panel despite a displayed Number. Earlier tests covered six location-line modes on a straight Basic wall. These cases do not prove every type/shape pairing or Revit 2026–2027 behavior. Recheck the selected bridge's contract and verify the requested result.
