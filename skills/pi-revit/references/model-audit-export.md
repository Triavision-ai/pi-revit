# Model audit and export

Use this workflow to establish model counts and data quality, inspect findings, and produce requested exports with a record of the model and scope used. An audit does not imply permission to repair the model or save its Revit file.

## Establish an auditable scope

1. Select the intended Revit session with `manage_revit_instances` when needed, then call `get_model_overview`. Record the returned model identity, title, available persistent path/identity, Revit/add-in versions, and the inspection time. The opaque document ID identifies this open session; it is not a permanent project identifier or an export-folder key.
2. Activate `summarize_elements`, `manage_element_sets`, `get_model_health`, and the required inspection/export tools with `find_revit_tools`. State the categories, levels, views, and parameter rules being audited. Pass `expected_document_id` on reads as well when keeping the audit tied to one model matters.
3. Use `get_elements` for raw counts and parameter projections. Up to 20 `parameter_names` can be requested per row, with optional type parameters. Report missing and ambiguous matches separately from empty or null values. Prefer built-in identities or shared GUIDs for stable parameter targeting. Raw measurable values use internal units; retain `displayValue` and unit context for human-readable findings.
4. Use `summarize_elements` for counts by category, type, level, or exact parameter value. The query covers all matches and rejects query-level paging. Narrow scopes above 10,000 candidates; outer pages contain groups, not partial element counts. Record each query and avoid adding overlapping scopes as though they were disjoint.

## Review health and retain findings

5. Call `get_model_health` for warning groups and model structure. Inspect truncation flags: it returns at most 100 warning groups and 20 element examples per group. A truncated example list is not the complete affected set, and a successful health read does not certify every model-quality rule.
6. For a reusable filtered scope, create a set with `manage_element_sets`. Record its query, `set_id`, count, and expiry. Sets retain at most 10,000 members, expire 30 minutes after creation, and belong to the exact open document/bridge session. They store membership, not frozen parameter values. Repeated reads show current values and report deleted or identity-changed members in `missing`; follow `next_offset` using visited membership, not only returned rows.
7. Inspect specific findings with `get_element_details` and `get_element_relationships`. For coordinate checks, use `get_model_coordinates` with explicit units and record the active project location; its shared mapping does not identify a GIS reference system. For MEP checks, use `get_mep_connections`, complete both connector/reference page dimensions, and distinguish `IsConnected` from physical/logical reference counts and unavailable values. Spatial tools may narrow review candidates, but axis-aligned boxes do not prove clashes or clearance failures. Preserve each tool's method, units, limits, warnings, and approximation status in the findings.
8. Retrieve oversized payloads with `read_revit_result` and follow every continuation. This recovers the current tool payload; it does not expand the original query page or remove warning/projection limits.

## Apply only requested corrections

9. If corrections are authorized, use `set_parameters` with exact document identity, explicit numeric input units, and `preview: true` to inspect proposed before/after values. Use `atomic: true` where the batch must succeed together. Check `commit_validation_performed`, failures, and warnings before applying the intended edit as a new operation. Only committed `succeeded` entries represent retained changes; proposed entries were rolled back.
10. Reread the affected members and repeat relevant counts/health checks after changes. Distinguish a changed query membership from changed values within the original set. Preserve before/after evidence when reporting the correction; no call should save the Revit file unless the user requested that separately.

## Export and record what was produced

11. Discover and inspect the intended sheet/view IDs before `export_documents`. PDF/DWG/PNG require 1–100 explicit IDs. IFC exports the whole model by default or can use one supplied view; record that distinction. PDF combines outputs by default. Export has file effects, and IFC can also write model metadata inside its transaction; it is not a rollback-only preview.
12. Omit `output_dir` to use the identity-derived model destination unless the user requests another location. Treat returned `outputDir` and `files[].path` as authoritative. Do not reconstruct a folder from model title or session ID. Use the verified model folder for accompanying audit notes according to workspace rules; never trigger an unnecessary export just to discover a folder.
13. Record export time, format, requested IDs/scope, model identity evidence, operation ID, returned paths and sizes, and commit warnings. These identify what the export call reported. Directory-change detection can be confused by unrelated simultaneous writers, so avoid sharing the output folder with concurrent work.
14. Open or inspect the produced artifact as appropriate to the deliverable. Existence and file size do not establish drawing quality or IFC schema/geometry validity. If export fails, report observed partial files; rollback does not remove them. For a timeout, inspect its original operation receipt before retrying with the same `_operation_id` and identical arguments. State remaining uncertainty explicitly rather than claiming an unverified export succeeded.
