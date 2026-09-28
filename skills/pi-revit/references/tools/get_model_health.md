# get_model_health

## Purpose and preconditions

Review Revit document warnings and basic model structure, for example before and after an authorized bulk change. Activate this advanced tool with `find_revit_tools`. It requires the intended active document. This is a bounded warning/structure report, not a complete compliance, geometry, or performance audit.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `b8ec894975d2efe0`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Resolving warnings (report only) | Revit API: Document.GetWarnings; resolve with dedicated edit tools or custom code within scope. Check all its members in one call: `search_api_docs` with query `Document.GetWarnings`, then use `execute_csharp` within the requested scope. |
| Geometry, performance or standards-compliance audits | Revit API: Custom inspection; verify members with search_api_docs. Check with `search_api_docs`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

There are no tool-specific inputs or paging controls. Optional `expected_document_id` binds the read to the exact known document. Optional `_operation_id` is only for retrying an identical earlier operation. See [execution rules](../execution-rules.md).

## Example

```json
{}
```

## Results and verification

`warnings.total` counts warning occurrences. `groupCount` counts descriptions; `groups` returns the top 100 groups by occurrence count. Each group reports description, severity from the first occurrence, occurrence `count`, distinct failing `elementCount`, and up to 20 failing element IDs/names. Inspect `groupsTruncated` and each group's `elementsTruncated` before claiming completeness.

There is no continuation for the omitted groups or elements. `read_revit_result` can retrieve a large saved response, but cannot remove these tool-level caps. Report truncation and use a separately authorized targeted inspection if full detail is needed.

The report also gives worksharing status/user worksets, phase and design-option names/counts, in-place family count, and total host non-type element count. These totals describe different populations. Missing warning details can be represented by fallback descriptions, null severity/name, or empty failing-element lists.

Compare equivalent snapshots of the same document. A reduction in warnings does not establish that an edit preserved the design, and zero warnings does not establish complete model quality. Read relevant elements and verify the actual requested outcome.

## Effects and recovery

Read-only: no warning resolution, deletion, selection change, save, or export. Investigation does not authorize fixing warnings. Follow [operation recovery](../operation-recovery.md) for no-document, identity, and transport errors.

## Compatibility

Source reference: PI-Revit 0.4.0, [GetModelHealth.cs](../../../../src/Revit/Tools/GetModelHealth.cs), plus public identity/retry inputs. Revit 2025–2027 are bridge targets; no new live validation is claimed.
