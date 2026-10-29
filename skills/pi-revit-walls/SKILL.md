---
name: pi-revit-walls
description: Plan, inspect, create, and verify Autodesk Revit walls with PI-Revit. Use for wall placement, location lines, level constraints, structural usage, room bounding, and joins in a connected project; route wall shapes and edits beyond the dedicated tool through checked Revit APIs.
---

# Walls with PI-Revit

Use this skill for wall-specific model work. For a general explanation, answer without connecting to Revit unless the question depends on the current model. For model work, read the shared [execution rules](../pi-revit/references/execution-rules.md); this sibling skill does not load them automatically. Discover the current tool and read its [create_walls manual](../pi-revit/references/tools/create_walls.md) before creation. The selected bridge's schema and contract compatibility take precedence over the packaged manual.

## Choose the operation

- **Inspect existing walls:** identify the intended Revit session and model, then use `get_model_overview`, `get_elements`, `get_element_details`, and relationships or geometry tools as needed. An inspection does not authorize editing walls.
- **Create straight, vertical Basic walls:** use `find_revit_tools` to activate `create_walls`. Discover the actual wall type with `get_element_types` (`of_class: "WallType"`) and levels from the target model; use their IDs rather than translated names or fixture values. Confirm the requested coordinate frame and length unit. The tool's XY endpoints are absolute document-internal coordinates in its explicit `unit`, without shared/survey coordinate conversion; their direction and `flip` determine the exterior side.
- **Choose wall placement:** choose exactly one top mode per row: unconnected `height` or `top_level_id` with optional `top_offset`. Specify `location_line` when a center, core, or finish plane must lie on the input XY line. Revit's location curve may then lie away from that requested plane; setting the location-line property alone does not move geometry. Apply base offset, room bounding, and end join allowance only from the user's design requirements or established model context. Use `structural_usage` or the legacy `structural` flag, never both. Width, layers, and materials belong to the selected wall type; this tool does not change them.
- **Beyond the dedicated tool:** curved, slanted, tapered, stacked, curtain, attached and profile-edited walls, wall openings, non-end geometry joins, and type/level creation follow the manual's declared alternatives. Check unfamiliar API signatures with `search_api_docs` before `execute_csharp`. Gate version-specific APIs by the selected Revit build; do not infer a capability from the missing dedicated tool alone.

## Verify and report

Use preview or atomic batches when they help check the requested change. A preview and an atomic failure can return `proposed` rows with temporary IDs; only `succeeded` rows of a committed batch are retained. Inspect `committed`, `failed`, `commit_validation_performed`, `commitWarnings`, `commitDiagnostics`, and `model_changes`. Compare committed rows' `actual` properties with the requested wall type, constraints, offsets, location line, structural usage, room bounding, and joins. An `actual_read_error` can follow a committed edit; reread that retained ID instead of assuming rollback. Reread retained IDs independently and inspect a capture when the visible alignment matters. A commit does not save the RVT file. For an uncertain outcome, follow the shared [operation recovery](../pi-revit/references/operation-recovery.md) before retrying.

The expanded `create_walls` behavior above was live checked in a disposable Revit 2025.4 project, including six location-line modes on an asymmetric multilayer type, preview, commit, and rollback cases. Revit 2026 and 2027 are package targets, not versions established by that test. Follow the selected bridge's current contract and verify the requested behavior in its version.
