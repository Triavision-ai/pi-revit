# Room documentation

Use this workflow to prepare a room plan or section, tags, a schedule, and a drawing sheet. Match the user's requested deliverables and model-saving instructions. Creation and placement calls change the open document; a tool commit does not save the Revit file.

## Establish the room and drawing resources

1. If needed, use `manage_revit_instances` to select the intended bridge. Call `get_model_overview` and retain its exact `project.documentId`. Pass it as `expected_document_id` throughout, including previews and placement listing. After a bridge restart or document reopen, select the intended session and refresh the overview.
2. Activate the required tools with `find_revit_tools`. Query rooms using `get_elements` with category `OST_Rooms`. Project room number/name parameters using built-in identities discovered through `get_element_details`. Follow query pages and distinguish missing values from empty ones. Select the intended room by its identity, not a name alone.
3. Read the room's level, location, bounds, and relevant parameters with `get_element_details`. Check that it is placed and suitable for documentation. Record units: detail coordinates are internal feet. A box is only an enclosure and does not establish the room's exact boundary or a valid tag location.
4. Discover existing views/levels with `get_elements`, view-family types using `get_element_types` with `of_class: "ViewFamilyType"`, and compatible room-tag/titleblock symbols with `get_element_types`. Reuse suitable loaded resources; do not invent type IDs.

## Build and check the views

5. Use `manage_views` to duplicate an appropriate plan, or create a plan with the room's `level_id` and a compatible `view_family_type_id`. Choose a clear name and supported scale/template. A new floor plan is not automatically cropped to the room. Confirm the actual view extent before describing it as a room drawing.
6. If a section is requested, use `create_section` with an explicit internal-coordinate origin, orthogonal view/up vectors, dimensions, and length unit. Derive the extent from the intended room and drawing requirements; do not assume its bounding-box faces are finished wall faces. Preview each proposed edit, inspect failures and `commit_validation_performed`, then apply the intended edit with a new operation ID. Preview-created IDs are temporary.
7. Read the committed view ID, then use `create_tags` with `kind: "room"`, a compatible plan view and loaded room-tag type. Place `head_position` at the room's level in internal coordinates using the specified unit. It always refers to the tag head, including when a leader is enabled. Omit `orientation` for room tags. Preview, then create the intended tags; check all per-target results. Use `atomic: true` when the requested tag batch must succeed together.

## Add the schedule and sheet

8. Reuse a suitable existing schedule or create a regular `OST_Rooms` schedule with `manage_schedules`. On the committed schedule, call `get_schedule_fields` to discover eligible `parameter_id`/`field_type` pairs for room number, name, area, and any requested data. Add those fields, then read `get_schedules` for actual schedule-local `field_id` values.
9. Configure headings, explicit-unit widths, itemization, sorting, and any room-number filter with `manage_schedules`. Supplied filter/sort arrays replace their entire lists. Use measured numeric filter units explicitly. Verify all row and column pages; grouped rows and totals are not element IDs. Do not reuse field IDs created only in a preview.
10. Create or update the sheet with `manage_sheets`, using the requested name/number and an optional loaded titleblock type. Preview first when assessing the proposed layout changes, then retain the committed sheet ID.
11. Place committed views and the schedule with `manage_sheet_placements`. Specify paper-space `[x,y,0]` coordinates and units; never multiply by view scale. A viewport position is its box center excluding its label, while a schedule position is its insertion point. Omit schedule rotation. Use `list` to inspect placements and `move` to refine them; previews produce no reusable placement IDs.

## Verify and deliver

12. Activate the sheet with `open_view`, capture it with `capture_view`, and open the returned PNG using Pi's image-capable read tool. Check tag legibility, view extent, scale, schedule contents, titleblock data, and overlaps. Numeric placement alone does not confirm visual layout quality.
13. If export is requested, export the committed sheet ID through `export_documents` and report the returned paths. Respect the user's file-saving instruction separately from export. Summarize committed views, tags, schedules, and sheets, along with remaining failures or warnings. If a call times out, check `get_revit_operation` and retry only the identical request with its original `_operation_id` until its outcome is known.
