---
name: pi-revit
description: Work with the open Autodesk Revit model through the Revit bridge tools (ping, get_model_overview, get_model_coordinates, get_mep_connections, get_elements, summarize_elements, manage_element_sets, get_element_details, get_element_types, get_linked_models, get_linked_elements, query_spatial_elements, measure_geometry, get_schedules, get_schedule_fields, manage_schedules, create_tags, get_element_relationships, manage_selection, open_view, set_parameters, transform_elements, delete_elements, change_element_types, manage_views, manage_sheets, manage_sheet_placements, search_api_docs, execute_csharp, capture_view, export_documents, get_model_health), select local sessions with manage_revit_instances, manage reusable code with manage_revit_scripts, retrieve saved results with read_revit_result, and check operation receipts with get_revit_operation. Use when the user asks about the Revit project, linked models, schedules, elements, parameters, selection, or wants to change, script, capture, or export the model.
---

# Revit

Work with the live Revit model. The bridge targets Revit 2025, 2026, and 2027; the 0.3.0 changes were tested live on Revit 2025. Bridge document tools require Revit running with a project open; `ping` and `search_api_docs` work without a document. `read_revit_result` reads an already saved result locally without contacting Revit. `get_revit_operation` contacts the running bridge without needing an open document or waiting for the model thread.

## Tool selection

| Task | Tool |
|------|------|
| Bridge alive? Which Revit version? | `ping` |
| List local Revit sessions or choose the intended instance | `manage_revit_instances` (native extension tool) |
| Find and activate specialist tools | `find_revit_tools` (local catalogue) |
| Orientation: project info, units, levels, grids, category counts | `get_model_overview` |
| Read base points, site/project locations, or map internal points to active shared coordinates | `get_model_coordinates` (advanced) |
| Inspect one host element's MEP connectors and their references | `get_mep_connections` (advanced) |
| List or count elements of ANY category (walls, doors, rooms, sheets, views, ...) | `get_elements` |
| Count a whole query scope by category, type, level, or parameter value | `summarize_elements` (advanced) |
| Retain and reread a temporary snapshot of matching host elements | `manage_element_sets` (advanced) |
| Read parameter VALUES, location, bounding box, materials of specific elements | `get_element_details` |
| List element types / family symbols; "used vs merely loaded" | `get_element_types` |
| List placed Revit links, load status, document identities, placement transforms | `get_linked_models` (advanced) |
| Query or count elements inside one loaded Revit link | `get_linked_elements` (advanced) |
| Find host elements whose bounding boxes intersect or fit inside a region | `query_spatial_elements` (advanced) |
| Measure explicit points or approximate separation of element bounding boxes | `measure_geometry` (advanced) |
| List schedules or read their fields and displayed cells | `get_schedules` (advanced) |
| Discover fields eligible for an existing schedule | `get_schedule_fields` (advanced) |
| Create or configure a regular schedule | `manage_schedules` (advanced) |
| Create element, room, space, or area tags in one view | `create_tags` (advanced) |
| Inspect an element's host, type, members, joined geometry, or dependents | `get_element_relationships` (advanced) |
| Read or change the user's selection; zoom; temporary isolate | `manage_selection` |
| Put a view or sheet on the user's screen (activate it) | `open_view` |
| Write or preview parameter values and renames; optional atomic batch | `set_parameters` |
| Move, copy, or rotate a selection together | `transform_elements` (advanced) |
| Preview or perform deletion, including Revit's reported deletion cascade | `delete_elements` (advanced) |
| Change element types with per-target results | `change_element_types` (advanced) |
| Create plans, isometric 3D views, or sections; duplicate or update views | `manage_views` (advanced) |
| Create, rename, or renumber drawing sheets | `manage_sheets` (advanced) |
| List, place, or move views and schedules on sheets | `manage_sheet_placements` (advanced) |
| Look up Revit API classes/members/signatures | `search_api_docs` |
| Other model tasks (create model elements, custom annotation, ...) | `execute_csharp` |
| Save, inspect, and run an exact reusable script version; inspect run history | `manage_revit_scripts` (native extension tool) |
| PNG snapshot of a view (visual QA) | `capture_view` (advanced) |
| PDF/DWG/PNG/IFC file export | `export_documents` (advanced) |
| Warnings / model quality audit | `get_model_health` (advanced) |
| Continue a saved large tool result | `read_revit_result` (local, no Revit call) |
| Check progress or recover a timed-out call's retained result | `get_revit_operation` (native extension tool, local bridge) |

## Recipes

Read the matching reference when the user requests a complete workflow:

- [Room documentation](references/room-documentation.md): plan/section views, room tags, schedules, and sheet placement with previews and visual checks.
- [Model audit and export](references/model-audit-export.md): counts, health checks, reusable sets, parameter review, and traceable model exports.

Workflow guidance:

- Specialist tools start inactive. Use `find_revit_tools` with search words or exact `names` to activate matching tools, then call them normally. Browsing without a query lists the catalogue without activation. Activation preserves other Pi tools and runs no model operation.

- Call `get_model_overview` for the intended open model before changing it. Copy `project.documentId` unchanged into `expected_document_id` for all model writes and previews (`set_parameters`, `transform_elements`, `delete_elements`, `change_element_types`, `manage_views`, `manage_sheets`, `manage_sheet_placements`, `manage_schedules`, `create_tags`, `execute_csharp`), `export_documents`, `open_view`, and selection/zoom changes. `manage_sheet_placements` requires it even for `list`; its tool-level metadata allows writes. `manage_selection` also requires it whenever `isolate_in_view: true`, even with action `get`. Other pure reads may omit it; a supplied ID is always checked. A matching legacy `expected_document` title alone is insufficient.
- The ID belongs to one open document in one bridge session. Refresh it after closing/reopening the model or restarting Revit. If the guard rejects a call, activate the intended model and obtain its overview again; do not blindly substitute the currently active model's ID.
- `get_elements` is the listing/counting primitive (`count_only: true` for bare counts). It returns identity fields (id, name, category, typeName, levelId), with optional `parameter_names` projections for up to 20 parameter identities. Use `get_element_details` for full inspection. Prefer a `category` or `of_class` scope when filtering by a parameter's display name.
- The selection pipeline is `get_elements` -> ids -> `manage_selection` (action `set`); there is no inline filter on selection.
- `set_parameters` handles bulk parameter writes and renames (the `Name` parameter covers levels, views, sheets, types). Default batches can commit partial successes; `atomic: true` rolls the whole batch back if any update fails. Use `preview: true` to validate a proposed batch and roll back its model changes. Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed` before describing the outcome.
- Parameter display names are LOCALIZED: in a non-English Revit UI, `Mark`, `Comments`, and every other display name appear under their translated names. When a display-name lookup or `parameter_names` filter finds nothing, or the document may be non-English, use the language-independent `BuiltInParameter` enum name instead (e.g. `ALL_MODEL_MARK` for Mark, `ALL_MODEL_INSTANCE_COMMENTS` for Comments) — `set_parameters`, `get_element_details.parameter_names`, and `get_elements` filter rules all accept them, and `get_element_details` reports each parameter's `builtInParameter` name for discovery.
- Before writing `execute_csharp` code, verify unfamiliar classes/members with `search_api_docs` (works with no document open; first query builds the index and takes a few seconds). The top match carries its remarks, parameter docs, and returns inline, and every public API enum value is searchable — trust the result over guessing or web search; narrow the query to promote a different match into the top slot.
- `export_documents` defaults to `Documents\pi-revit\Models\<model title>--<identity hash>\exports`. Use its returned `outputDir` and file paths as authoritative; do not construct a destination from the title or opaque `project.documentId`. Saved paths and cloud/server identities determine the folder; unsaved/unavailable identities use a session fallback. Save As can select a new folder. Pass `output_dir` when the user requests a different destination. Existing title-only folders remain untouched.
- Large tool payloads return a `result_id`, `file_path`, and continuation instructions. Call `read_revit_result` with that ID and `offset: 0`, then follow `next_offset` until `has_more` is false. Concatenate `text` fragments in order; each fragment is not a standalone JSON result. Offsets count UTF-16 code units. IDs last for the current extension instance; after reload, use the returned absolute file path with `read` while the file exists. Retrieval does not replace the original query's pagination or remove its limits.

## Required visual proof

- After creating or changing sheets, drawings, views, tags, schedule layouts, geometry placement, or other visible results, capture the actual result with `capture_view` or an image export. An API success response alone does not establish visual correctness.
- Open the returned image with Pi's image-capable read tool and inspect its contents. Check that the requested result is visible, correctly positioned, readable, and free of unintended clipping or overlaps. Use before/after captures when needed to demonstrate movement, rotation, layout changes, or isolation.
- Frame the real Revit view before capture. A mostly blank image with a tiny drawing does not count as proof. Use a readable full-sheet image plus close-ups when necessary; place schedules on a sheet when a graphical capture of their layout is needed.
- Retain the pictures with the task's evidence and show or link them in the final result with short captions describing what was verified. Do not fabricate images or infer appearance from filenames, dimensions, or successful export alone.
- If image capture or inspection is unavailable, explicitly report visual verification as blocked or incomplete. Do not claim the visual work is fully verified. Capturing or exporting evidence does not authorize saving the Revit model.

## Revit instance selection

- `manage_revit_instances` defaults to `action: "list"` and returns reachable sessions with opaque `bridge_id`, process/version details, selection status, and operation-tracking support. Use `action: "select"` with the exact returned ID. Selection applies to this Pi extension session; it does not activate a Revit document.
- The first sole instance binds automatically. If several instances are available before binding, select explicitly before model calls. Once bound, the session never silently falls back. If the selected bridge closes or restarts, list again and explicitly select the intended available identity, even if only one remains. Selection refreshes the tool catalogue; check `tool_catalog_ready` and retry `ping` if discovery is pending. Read `get_model_overview` afterward for a fresh exact document ID.
- Current bridges publish separate discovery files in `%APPDATA%\RevitBridge\instances\<bridgeId>.json`; the legacy `bridge.json` file is also read. Older bridges without generation IDs receive opaque hash selectors. Copy selectors from the list unchanged. Multiple older bridges cannot all be discovered through the one legacy file; deploy the current add-in to each instance for independent discovery.
- `get_revit_operation` and identical retries with `_operation_id` always resolve the original bridge from the operation ID, regardless of the selected target for new calls. They do not change that selection. If the original session is unavailable, the request fails without redirecting it. Selecting a new session cannot recover an old session's lost receipts.

## Project coordinates and MEP connectors

- `get_model_coordinates` requires a project document and explicit length `unit`. It returns project/survey base points, active project location, site data, and project-location pages (`offset`/`limit`, default 50, maximum 100). Optional `points` contains up to 100 `[x,y,z]` positions in document internal axes. The active location's `GetProjectPosition` maps them to shared east/west, north/south, and elevation values. All returned lengths use the requested unit; angles and latitude/longitude use degrees. Paging applies to locations, not input points.
- This is Revit's active shared-coordinate mapping. Do not infer a GIS coordinate reference system, datum, or projected map units from site latitude/longitude or base-point values. The tool does not acquire, publish, or modify coordinates.
- `get_mep_connections` requires one host `element_id` and explicit length `unit`. It reads connectors on MEP curves (pipes, ducts, conduit, cable trays, and wires) and MEP family instances, returning internal-coordinate origins, sizes, unit-vector normals, flow direction, and available system identity. It does not traverse a network or linked contents.
- `physically_connected` reports Revit's `IsConnected`. The `references` collection comes from `AllRefs`, which can contain both physical and logical references; its count is not a physical connection count. Unsupported property reads return null with a field-specific reason in `unavailable`; do not treat unavailable as disconnected, zero size, or no flow. A null system can also mean no assigned system, so inspect its reason rather than assuming a read failed.
- Connector pages use `offset`/`limit`; reference pages use `reference_offset`/`reference_limit` independently for each returned connector. Both default to 50 and allow at most 100. Hold the connector page while following each needed `references.next_offset`, then advance the outer `next_offset`. Preserve the owning element ID together with `connector_id` when recording a connector identity.

## Spatial queries and measurements

- `query_spatial_elements` reads host elements using model axis-aligned bounding boxes. Supply region `min`/`max` vectors and explicit length `unit` in document internal axes. `relation: "intersects"` is the default; `"inside"` requires the entire element box to fit inside the region. Both include touching boundaries. Linked contents are not traversed.
- Its `query` accepts the whole-scope element filters, with no query-level paging or projections. Narrow it to at most 10,000 candidates before spatial testing; the cap applies even when few elements would match the region. Results sort by element ID and use outer `offset`/`limit` (default 100, maximum 200). Follow `next_offset` and inspect `candidate_count`, `without_bounds_count`, and warnings. Elements without a model box are counted separately and omitted from matches.
- `measure_geometry` with `mode: "point_distance"` requires explicit `point_a` and `point_b`. It returns exact Euclidean distance between those supplied points and signed `delta` from A to B, not a distance between element surfaces. `mode: "bounding_box_gap"` instead requires `element_a_id` and `element_b_id`, both in the host; missing boxes fail the call. Do not mix point inputs with element IDs or pass `clearance` in point mode.
- Both spatial tools require `unit` from `millimeters`, `centimeters`, `meters`, `feet`, or `inches`; it applies to all coordinate/distance inputs and outputs. Measurement's optional nonnegative `clearance` is only a box-proximity threshold. `box_gap_below_clearance` uses strict less-than, while `boxes_overlap_or_touch` includes contact.
- Bounding boxes may include nonphysical geometry. Their gap is only a lower bound on geometry separation; a zero gap or a threshold hit is a candidate for review, not a confirmed clash or physical clearance failure. Preserve the returned `method` and `approximate` labels in reports and do not turn box matches into verified collision claims.

## Parameter projections, summaries, and element sets

- `get_elements.parameter_names` accepts display names, built-in parameter names, or `guid:<GUID>`. Each projection reports `requested`, `isType`, `found`, `ambiguous`, and every matching parameter in `matches`. Missing and duplicate display-name matches are explicit; do not silently select one ambiguous match. `include_type_parameters: true` adds the same requested parameters from the type, where one exists. `count_only` returns no projections.
- Parameter `value` is the raw Revit value; measurable numeric values use internal units. `displayValue` is separately formatted for the document. This differs from numeric query filter inputs and `set_parameters` inputs, which use the supplied `unit` or the document's display units.
- `summarize_elements` and `manage_element_sets` creation accept a `query` containing category, class, level, type, active-view, and parameter-filter scope. The query always covers all matches: query-level paging, `count_only`, fields, and projections are rejected. Both tools require at most 10,000 matches; narrow larger scopes. An omitted query means all host instances, subject to this cap.
- `summarize_elements` groups by `category`, `typeName`, `levelId`, or `parameter`. Parameter grouping requires `parameter`; `type_parameter: true` uses the type instead of the instance. Groups use exact raw values, including internal numeric units, and distinguish a missing parameter from a present parameter with a null value. Ambiguous parameter names are rejected. The outer `offset`/`limit` pages groups only (default 100, maximum 500); `total_elements` covers the whole matching scope.
- `manage_element_sets` actions are `create`, `list`, `read`, and `forget`. Sets retain membership and element identities without changing the model or selection. Each expires 30 minutes after creation; reads do not extend its lifetime. At most 32 sets are retained across the bridge session. A set belongs to the exact open document and does not survive closing/reopening that document or restarting the bridge. `list` shows only sets for the active document.
- Read a set with its `set_id`, using `offset`/`limit` (default 100, maximum 1000). Membership stays fixed; filters are not rerun, but returned values are current. Optional `parameter_names` and `include_type_parameters` use the same projection contract as `get_elements`. Check `missing` for deleted or identity-changed members before passing returned IDs to another tool. Follow `next_offset`, which advances by `visited_count`, including missing members; `returned_count` can be smaller.

## Parameter previews and atomic batches

- `set_parameters` accepts 1–200 updates, each isolated in a subtransaction. `preview` and `atomic` are independent and default to false. Preview still requires the intended model's `expected_document_id`.
- A preview commits an eligible transaction to exercise Revit's commit checks, then rolls back its enclosing transaction group. If an atomic batch has a failed update, or no updates are accepted, it rolls back without attempting commit. `commit_validation_performed` distinguishes these cases; do not claim commit validation when it is false. A returned preview result confirms group rollback; on an error, inspect the reported cleanup outcome.
- `succeeded` contains only committed updates. Previewed or otherwise rolled-back accepted steps appear in `proposed`, with `updated: 0` and `committed: false`; they are not changes left in the model. Both lists include zero-based input `index` and observed per-step `before`/`after` values. Repeated writes to the same parameter form a sequence, so intermediate `after` values are not a final-model snapshot. Read the element again when final values matter, and inspect all `failed` entries and commit warnings.

## Transform, delete, and change types

- Activate these tools with `find_revit_tools`. All three use host-document IDs and require `expected_document_id`, including previews. Inspect the common `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed` fields. `updated` counts committed steps: a transform or deletion is one step for the whole selection, not one per element.
- `transform_elements` accepts 1–200 distinct `element_ids` and `action: "move"`, `"copy"`, or `"rotate"`. Supply `unit` explicitly: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`. Move/copy uses `translation: [x,y,z]`; rotation uses `axis_origin`, a nonzero dimensionless `axis_direction`, and signed right-hand `angle_degrees`. Coordinates use the document's internal origin and axes, not a view or shared-coordinate frame. Returned location snapshots use feet regardless of input units.
- Transform applies the entire selection in one step. A failure rolls back that step; move/rotate rejects pinned requested elements, and the tool never unpins anything. Constraints or hosting can affect other elements. Snapshots do not provide a complete audit of those dependent effects. In copy previews, `created_ids` are temporary and marked `created_ids_are_temporary`; never reuse them after rollback.
- `delete_elements` accepts 1–200 distinct requested IDs and deletes them together. Pinned requested elements are rejected. A preview returns the complete `deleted_ids` set reported by `Document.Delete`, including `dependent_ids`, then rolls back. More than 10,000 deleted IDs causes rollback. This list does not audit surviving elements that Revit may modify.
- Optionally pass the full preview `deleted_ids` as `expected_deleted_ids` on a later deletion. The tool compares sets before commit and rolls back if they differ. This checks the deletion membership, not every element property or dependent effect. Use a new operation ID for the actual deletion because its arguments differ from the preview; retain the original ID only for an identical retry.
- `change_element_types` accepts 1–200 `updates` containing `element_id`/`type_id` pairs, with each target appearing once. Invalid types and pinned targets fail individually. Default batches can commit other targets; `atomic: true` rolls all back on any failure. After a committed change, use `resulting_id` and `unique_id`, since Revit can replace an element. Replacement IDs in `proposed` are temporary after either preview or atomic rollback and must not be reused. Constraints can affect connected or hosted elements beyond the returned target snapshots.

## Views, sheets, and placements

- Activate `manage_views`, `manage_sheets`, and `manage_sheet_placements` with `find_revit_tools`. They require `expected_document_id` and support `preview: true` for edits. Each edit is one step using the common transaction result fields. Created preview IDs are temporary (`id_is_temporary: true`); do not reuse them. Use `delete_elements` for removal and `open_view` to activate a committed view or sheet.
- `manage_views` supports `create_plan`, `create_3d`, `create_section`, `duplicate`, and `update`. Discover compatible `view_family_type_id` values with `get_element_types` using `of_class: "ViewFamilyType"`; use `get_elements` for levels and existing views. Plan creation also requires `level_id`; 3D creation is isometric. Duplicate/update requires `view_id`. Duplication options are `duplicate` (default), `with_detailing`, and `dependent`, subject to the view's supported options.
- A section requires `unit`, `origin`, nonzero orthogonal `viewing_direction` and `up` vectors, and positive `width`, `height`, and `depth`. Its origin and dimensions use the explicit length unit in the document's internal coordinate frame; directions are dimensionless. Width/height extend symmetrically about the origin, with depth extending along the viewing direction. This is not a sheet-coordinate operation.
- View edits can apply `name`, `scale` (1–24000), and compatible `view_template_id` together. Use template ID `-1` only when intentionally removing a template. An incompatible template or template-controlled scale fails the step; the tool does not remove a template automatically to change scale.
- `manage_sheets` creation requires `name` and `number`; optional `titleblock_type_id` selects a loaded titleblock `FamilySymbol`, and omission creates a sheet without one. Discover titleblock types with `get_element_types` and category `OST_TitleBlocks`. Update requires `sheet_id` and accepts name/number; it rejects `titleblock_type_id`. Use `change_element_types` on an existing titleblock instance to change its type.
- `manage_sheet_placements` `list` requires `sheet_id` and returns viewports and schedule instances with `offset`/`limit` (default 100, maximum 200) and `next_offset`. Listing does not edit the model, but the tool is classified as write-capable, so the exact document guard applies to every action.
- Placement `place` requires `sheet_id`, `view_id`, `position: [x,y,0]`, and explicit `unit`. `move` uses `placement_id`, position, and unit; pinned placements are rejected. Coordinates are paper-space sheet coordinates: never multiply them by view scale. A viewport's position is its box center excluding the label; a schedule's position is its insertion point. Returned positions always use feet and identify `position_kind`.
- Optional viewport `rotation` is `none`, `clockwise`, or `counterclockwise`. Omit rotation entirely for schedules, including when no rotation is intended. Revit validates placement eligibility; placeholder sheets cannot receive content, and a view already placed elsewhere may be rejected.

## Schedule authoring and tags

- `manage_schedules` creates or configures a regular schedule as one atomic step. Creation requires `category` and `name`; `area_scheme_id` is available for area schedules. Configure requires `schedule_id`; category and area scheme are creation-only. Use `is_itemized` to control whether instances remain separate. Templates, revision/embedded schedules, and calculated/combined-field authoring are outside this tool.
- Discover eligible additions with `get_schedule_fields` on an existing schedule: use optional localized `name_filter` and `offset`/`limit` (default 100, maximum 200). Each field is identified by the pair `parameter_id` and case-sensitive `field_type`; negative built-in parameter IDs are valid. `included` reports existing pairs. These are not the schedule-local `field_id` values returned by `get_schedules`.
- `add_fields` uses the eligible pair and supports heading, hidden state, and width. When Count appears in field discovery, pass its returned `parameter_id` and `field_type` unchanged. Count also supports `{ "field_type": "Count" }` without a parameter ID. A supplied pair must be eligible for that schedule; do not guess its parameter ID. `update_fields`, `sort_fields`, and `filters` instead use actual schedule-local `field_id` values from `get_schedules`; never guess them from column positions or parameter IDs. Read newly committed fields before configuring their sort/filter rules. A preview's new schedule and added field IDs are temporary and cannot be reused.
- Add/update arrays each allow 50 fields. Width changes require an explicit length `unit` and apply to both grid and sheet widths. Supplied `sort_fields` (maximum 4) or `filters` (maximum 8) replace the entire corresponding list; omission preserves it, and `[]` clears it. Sort entries support `descending` and `show_header`.
- Filters require `field_id`, `comparison` (`equals`, `not_equals`, `contains`, `greater_than`, or `less_than`), `value_type` (`string`, `number`, `integer`, or `element_id`), and matching `value`. Measured `number` values require an explicit compatible `unit`; omit units for unitless numbers. Use `get_schedules` field `spec_type_id`, `can_filter_value`, and `can_filter_substring` to inspect capabilities. Its numeric filter values use internal units and its comparison names are Revit enum names, not the write schema's comparison strings. Returned grid/sheet widths use feet.
- `create_tags` requires `kind` (`element`, `room`, `space`, or `area`), one `view_id`, loaded tag `FamilySymbol` `tag_type_id`, explicit length `unit`, and 1–100 targets containing `element_id` and `head_position: [x,y,z]`. Positions use the document's internal coordinates and always describe the tag head, including with `leader: true`; returned head positions use feet. Targets must belong to the host document; linked targets and face/subelement references are unsupported.
- Element tags accept `orientation: "horizontal"` (default) or `"vertical"`; omit orientation for spatial tags. Room/space/area tags require a compatible plan view and positions at the spatial element's level. Templates, perspective views, and unlocked 3D views cannot host these independent tags. Discover a compatible loaded tag type before creating tags.
- Schedule edits and tag creation require exact document targeting and support commit-validated preview rollback. Tag batches default to per-target partial success; `atomic: true` rolls all back on any failure. Inspect all common transaction fields. All tag IDs in `proposed` are temporary after preview or atomic rollback; only reuse IDs from committed `succeeded` entries.

## Links, schedules, and relationships

- Start linked queries with `get_linked_models`. It lists direct link instances, including unloaded links, with `link_instance_id`, `loaded`, and `linked_document_id`. Pass the returned linked identity unchanged as `expected_linked_document_id` to `get_linked_elements`; use the host overview's ID for `expected_document_id`. Refresh link discovery after a link is unloaded or reloaded. Nested links are not traversed.
- `get_linked_elements` accepts the same category, class, level, type, parameter filters, `count_only`, and `offset`/`limit` as `get_elements`, but does not support `in_active_view`. Level and type IDs belong to the linked document. Preserve each row's full `reference`, including the link instance: two placements of the same linked model can have different host coordinates. Linked element IDs must not be passed to host selection or write tools.
- With `include_bounds: true`, `get_linked_elements` returns `host_bounds` in internal feet, or null where no bounding box exists. These are axis-aligned host bounds calculated from all eight transformed box corners, not exact element geometry. Link transform origins are also in feet; the basis vectors describe orientation and scale.
- `get_schedules` without `schedule_id` lists schedules, optionally narrowed by `name_filter`; templates and titleblock revision schedules are excluded from this list. Supply a returned ID to read field definitions, specification and width metadata, filter capabilities, current sort/filter rules, and formatted body cells. Body rows may include headings, grouped entries, and totals: neither a row index nor a cell value establishes an element ID. Hidden field definitions do not correspond one-to-one to displayed columns.
- Schedule body reads have two independent continuations: `next_offset` for rows and `next_column_offset` for columns. Read all column pages at each row offset before advancing the rows. List/body pages default to 50 entries (maximum 200); column pages default to 50 (maximum 50). Use returned row and column indices rather than guessing their origin.
- `get_element_relationships` reads one host element. Choose `relationships` to narrow the result; each relationship has its own count and `next_offset`, using the supplied `offset`/`limit` (default 100, maximum 200). `host`, `parent`, and `subcomponents` describe family instances; `members` describes groups or assemblies. Links are not traversed. Logical `dependents` are not a complete prediction of what deletion would remove. In a family document, request specific kinds that exclude `joined`.
- Follow each tool's returned pagination separately from `read_revit_result`. Link-instance pages default to 100 (maximum 1000); linked-element pages default to 200 (maximum 1000).

## execute_csharp playbook

- Globals: `doc` (Document), `uidoc` (UIDocument), `uiapp` (UIApplication), `inputs` (`System.Text.Json.JsonElement`), and `Dump(value)` to record intermediates into the result's `dumps[]`. Pass an optional JSON `inputs` object separately from `code`; omission gives an empty object. Read values with `inputs.GetProperty(...)` and validate them in the script. Inputs are not interpolated into source and may contain at most 100,000 JSON characters.
- The script runs inside one backend-owned transaction. Do not start another transaction on `doc` (sub-transactions are allowed). The tool checks commit status and attempts rollback on script failure; read its actual outcome instead of assuming rollback succeeded. Result projection can fail after a successful commit and report `returnValueError`. Filesystem and UI effects are separate from model rollback.
- Scripts must be fully synchronous: `await`/`async` is rejected at compile time; never block on `Task.Result`/`.Wait()`.
- Return primitives, strings, or anonymous objects/lists; raw Revit API objects are projected to compact shapes (Element -> `{id,name,category,typeName,levelId}`, ElementId -> number, XYZ -> `{x,y,z}`).
- Lengths are internal units (decimal feet) — convert with `UnitUtils.ConvertToInternalUnits`/`ConvertFromInternalUnits`.
- Common pitfalls: call `FamilySymbol.Activate()` before `NewFamilyInstance`; use collector-level filtering (`OfCategory`/`OfClass`/`WhereElementIsNotElementType`) and bounded loops. The budget is 120s and Revit cannot be interrupted mid-script. The dialog guard attempts dismissive responses and reports `suppressedDialogs`; it cannot guarantee handling every modal dialog.
- `capture_view` returns a `filePath` to a temp PNG, never image data — open it with the read tool to actually see it.

## Reusable scripts

- `manage_revit_scripts` supports `save`, `list`, `read`, `run`, and `history`. The local library is `%APPDATA%\pi-revit\scripts`, with immutable versions and run records. Save requires `name`, `description`, `code`, and `input_types`, and never executes the code. Names contain 1–64 lowercase letters, digits, underscores, or hyphens, starting with a letter. The returned 64-character SHA-256 `version` identifies the complete saved definition, including source, description, and input declarations.
- Read the exact `name`/`version` to inspect its code before running it; there is no implicit latest version. Run requires that exact saved hash, `expected_document_id`, and matching `inputs`. Saved content is integrity-checked before use. Library runs require a bridge with operation receipts; saving/reading/listing/history do not require a model.
- `input_types` declares at most 40 named required inputs of kind `string`, `number`, `integer`, `boolean`, `object`, or `array`. Extra inputs are rejected. This validates top-level JSON kinds only; scripts must validate nested contents, units, ranges, element identities, and other domain rules. `inputs` reaches the script as a separate `JsonElement`, not substituted source text.
- A library run has the same unrestricted model/UI/file/external effects and one-transaction behavior as `execute_csharp`, including its synchronous execution rule and timeout. There is no library preview mode or automatic model saving. Inspect the code's intended effects and honor the user's save instructions; saving a script definition is not saving the model.
- `list` and `history` support optional exact-name filtering and outer `offset`/`limit` (default 20, maximum 100). History records script version, document identity, input hash, timestamps, and the operation/bridge receipt identifiers, not raw input values or results. `prepared` is not proof of execution, `response_received` is not proof of a successful model edit, and `outcome_unconfirmed` needs receipt inspection. If interrupted, check `get_revit_operation` before retrying the same version, document, and inputs with the original `_operation_id`.

For example, save a numeric echo/check definition with `manage_revit_scripts`:

```json
{
  "action": "save",
  "name": "check_number",
  "description": "Echo a supplied number and report whether it is nonnegative.",
  "code": "var value = inputs.GetProperty(\"value\").GetDouble(); return new { value, nonnegative = value >= 0 };",
  "input_types": { "value": "number" }
}
```

Read the returned version and inspect its source:

```json
{ "action": "read", "name": "check_number", "version": "<exact version from save>" }
```

Then explicitly run that version against the intended open model, replacing both placeholders with the actual returned identities:

```json
{
  "action": "run",
  "name": "check_number",
  "version": "<exact version from save>",
  "expected_document_id": "<project.documentId from the current overview>",
  "inputs": { "value": 12.5 }
}
```

This example's source only returns the supplied value and a comparison; it performs no model edit or save. The run still uses the normal script transaction and operation receipt.

## Operation receipts and retries

- With a supporting bridge, every bridge tool call automatically receives an operation ID, included in its response or request error. Use `get_revit_operation` with `operation_id` to inspect it without queueing a model action. Receipts are held by the bridge, not the Pi session; the original bridge must remain reachable. Receipt reads and identical retries route to that original session even if another instance is now selected.
- States are `queued`, `running`, `succeeded`, `failed`, `expired_before_start`, `result_unavailable`, or `unknown`. `expired_before_start` confirms no tool action began. `failed` and `result_unavailable` do not establish rollback; inspect the result and actual effects. `succeeded` describes call completion, not a committed model edit: a preview can succeed with `committed: false` and rolled-back `proposed` changes.
- Retry only an identical request with the exact `_operation_id` added to its original tool arguments. The bridge waits for or returns that operation's response without executing it again. Preserve the original tool, document identity, and every argument; a mismatch is rejected. Omitting `_operation_id` creates a new operation. Do not use a new ID to bypass an unresolved earlier outcome.
- Full results are bounded to 128 completed receipts and 32 MiB in total. Eviction leaves the ID and outcome reserved as a receipt record for the rest of that bridge session, with `result_available: false`; retrying cannot recreate the evicted result or rerun the action. At 10,000 records, new tracked calls are rejected while existing receipts remain queryable.
- Restarting the bridge clears receipts and changes its identity. An old operation ID cannot be replayed in the new session. An unavailable original bridge or an `unknown` receipt is not proof that the operation never ran. Changing selection does not redirect old receipts or retries. Inspect the original bridge/model before issuing a new edit. An older bridge without tracking provides no receipt guarantee.

## Failure modes

- **Identity rejected**: no tool action was performed. Verify the intended active model, refresh `project.documentId`, and pass `expected_document_id` unchanged.
- **Several instances / selected session unavailable**: use `manage_revit_instances` to list and explicitly select the intended current bridge, then refresh its model overview. Calls are not redirected automatically. For an old operation receipt, restore access to its original bridge or inspect the original model; choosing another session does not establish the old outcome.
- **Partial UI/export effects**: selection or zoom can already have changed when isolation fails. Export errors can leave incomplete files, and IFC commit warnings are returned. Inspect the reported effects and output paths; model rollback does not remove files or reverse earlier UI actions.
- **Large-result save failed**: Revit may already have completed the operation. Check `get_revit_operation` using its operation ID to recover a retained bridge result. Verify its effects before issuing a new write.
- **Bridge not reachable** ("Revit bridge is not available" / "Could not reach the Revit bridge"): Revit is not running or the add-in did not load. Ask the user to start Revit, then retry `ping`.
- **HTTP 409 / "No active Revit document is open."** (`hasActiveDocument: false`): Revit is running but no project is open. Ask the user to open a project, then retry. This fails immediately; do not wait or retry blindly.
- **Timeout** ("Revit did not answer within Ns", 30s default / 120s for execute_csharp, capture_view, export_documents): Revit is busy or showing a modal dialog. An already-started tool can still complete. Check `get_revit_operation`; reuse the exact `_operation_id` and original arguments for an identical retry. If no receipt is available, verify model state before a new write.
- **Cancelled**: client cancellation does not cancel bridge work. Check the operation receipt and follow the same retry rules; a queued call may still start before its deadline, and running work cannot be interrupted.

## Upgrading from 0.2.x

Version 0.3.0 requires exact document IDs for the operations above. Update the Pi package and deploy the matching add-in with Revit closed, then restart Revit and start a fresh Pi session so the new schemas and instructions are loaded. Obtain a fresh overview before writes. `ping` reports an installed/loaded version mismatch. Setup preserves existing workspace `AGENTS.md`; merge these targeting, result-reading, and folder rules into an older workspace's instructions when needed.
