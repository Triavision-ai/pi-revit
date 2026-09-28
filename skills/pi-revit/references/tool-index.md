# PI-Revit tool index

Use this index to choose a capability, then read only the relevant manual. The tables below are generated from the tool contracts and the documentation manifest. They list every public tool in this package, but that inventory is not a limit on future tools. The current public schema determines accepted inputs; a manual is explanatory guidance.

Use [find_revit_tools](tools/find_revit_tools.md) to search with English task words; translate a request written in another language first. With its default `scope: "available"`, it finds registered capabilities and activates returned tools. `scope: "documentation"` returns packaged manuals without contacting Revit or activating tools. Results carry each tool's declared **limits with alternatives**, its **verification** method and its **contract compatibility** with the selected bridge. A manual's presence does not establish that a selected bridge advertises the tool.

A tool that does not cover a request is not evidence that the request is impossible. <!-- inv:capability-claims-checked --> Follow its declared alternative. When nothing dedicated fits, check [search_api_docs](tools/search_api_docs.md), then use [execute_csharp](tools/execute_csharp.md) within the requested scope.

Read [execution rules](execution-rules.md) for model work, [operation recovery](operation-recovery.md) for uncertain outcomes, and [visual verification](visual-verification.md) for visible changes. Bridge tools include the transport-added `_operation_id`. Document guards and action requirements can be added beyond the tool class's own declared inputs. Use the final registered schema and manual together.

<!-- generated:tool-index:start (npm run generate:contracts; do not edit this block) -->

## Connection, discovery, and local utilities

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [find_revit_tools](tools/find_revit_tools.md) | Discover tools, activate capabilities, or find packaged documentation. | native | none |
| [get_revit_operation](tools/get_revit_operation.md) | Inspect an original operation receipt after uncertain results. | native |  |
| [manage_revit_instances](tools/manage_revit_instances.md) | List and select local Revit bridge sessions. | native | reread |
| [manage_revit_scripts](tools/manage_revit_scripts.md) | Save, inspect and run exact versions of reusable scripts. | native | reread |
| [ping](tools/ping.md) | Check Revit bridge availability and versions. | native |  |
| [read_revit_result](tools/read_revit_result.md) | Read saved large results in bounded fragments. | native |  |

## Model inspection and review

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [get_element_details](tools/get_element_details.md) | Inspect element parameters, geometry bounds and identity. | core |  |
| [get_element_relationships](tools/get_element_relationships.md) | Inspect hosts, joined elements, members and dependents. | advanced |  |
| [get_element_types](tools/get_element_types.md) | Discover available element types and family symbols. | core |  |
| [get_elements](tools/get_elements.md) | Query, count and filter elements and project parameter values. | core |  |
| [get_model_health](tools/get_model_health.md) | Inspect model warnings and health information. | advanced |  |
| [get_model_overview](tools/get_model_overview.md) | Inspect project identity, units, levels and category counts. | core |  |
| [manage_element_sets](tools/manage_element_sets.md) | Retain query membership and reread current member values. | advanced | none |
| [summarize_elements](tools/summarize_elements.md) | Count a whole element query grouped by type, category, level or parameter. | advanced |  |

## Links, coordinates, and spatial analysis

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [get_linked_elements](tools/get_linked_elements.md) | Query elements inside a selected linked document. | advanced |  |
| [get_linked_models](tools/get_linked_models.md) | Discover loaded and unloaded Revit link instances. | advanced |  |
| [get_model_coordinates](tools/get_model_coordinates.md) | Read base points, project locations and shared-coordinate mappings. | advanced |  |
| [measure_geometry](tools/measure_geometry.md) | Measure point distance or approximate element bounding-box gaps. | advanced |  |
| [query_spatial_elements](tools/query_spatial_elements.md) | Query elements by bounding-box intersection or containment. | advanced |  |

## Selection and editing

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [change_element_types](tools/change_element_types.md) | Change element types with partial or atomic outcomes. | advanced | reread |
| [delete_elements](tools/delete_elements.md) | Preview or perform deletion including dependent elements. | advanced | reread |
| [manage_selection](tools/manage_selection.md) | Read or change selection, zoom and temporary isolation. | core | reread |
| [open_view](tools/open_view.md) | Activate a Revit view or sheet. | core | none |
| [set_parameters](tools/set_parameters.md) | Edit or preview parameter values with optional atomic rollback. | core | reread |
| [transform_elements](tools/transform_elements.md) | Move, copy or rotate elements with preview rollback. | advanced | reread |

## Drawings, schedules, and delivery

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [capture_view](tools/capture_view.md) | Capture a view as an image for visual verification. | advanced | inspect_output |
| [create_tags](tools/create_tags.md) | Create host element, room, space and area tags. | advanced | capture |
| [export_documents](tools/export_documents.md) | Export sheets, views or the model to PDF, DWG, PNG or IFC. | advanced | inspect_output |
| [get_schedule_fields](tools/get_schedule_fields.md) | Discover eligible parameter and field-type pairs. | advanced |  |
| [get_schedules](tools/get_schedules.md) | Inspect schedule fields, sorting, filters and table cells. | advanced |  |
| [manage_schedules](tools/manage_schedules.md) | Create and configure schedules, fields, filters and sorting. | advanced | reread |
| [manage_sheet_placements](tools/manage_sheet_placements.md) | List, place and move viewports and schedules on sheets. | advanced | capture |
| [manage_sheets](tools/manage_sheets.md) | Create, rename and renumber sheets. | advanced | reread |
| [manage_views](tools/manage_views.md) | Create, duplicate and update plans, sections and 3D views. | advanced | capture |

## API and custom execution

| Manual | Use | Tier | Verify |
| --- | --- | --- | --- |
| [execute_csharp](tools/execute_csharp.md) | Execute custom synchronous C# in Revit. | core | reread |
| [search_api_docs](tools/search_api_docs.md) | Search installed Revit API classes, methods, signatures and enums. | core |  |

## Workflows and shared guidance

| Guide | Kind | Use |
| --- | --- | --- |
| [execution-rules](execution-rules.md) | reference | Shared rules for model targeting, document identity, parameters, units and transactions. |
| [operation-recovery](operation-recovery.md) | reference | Recover after timeouts, cancellations and uncertain outcomes using operation receipts. |
| [visual-verification](visual-verification.md) | reference | Capture and inspect visible results and exported deliverables, then stop when the request is verified. |
| [tool-index](tool-index.md) | reference | Every public tool grouped by task, with manual links. |
| [room-documentation](room-documentation.md) | workflow | Room plans, sections, tags, schedules and sheets as one documentation workflow. |
| [model-audit-export](model-audit-export.md) | workflow | Multi-step model audit, data review and requested exports. |
<!-- generated:tool-index:end -->
