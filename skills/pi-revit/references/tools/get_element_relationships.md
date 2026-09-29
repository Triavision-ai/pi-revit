# get_element_relationships

## Purpose and preconditions

Inspect relationships of one known host element. Activate with `find_revit_tools`; discover the ID in the intended active document. Links are not traversed. Logical dependents are not a complete prediction of deletion effects; use an authorized `delete_elements` preview for its Revit-reported deletion cascade.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `c597f56ba5735470`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `element_id` | integer | yes |  |  |
| `relationships` | array of string | no |  | `type`, `level`, `owner_view`, `host`, `parent`, `subcomponents`, `group`, `assembly`, `members`, `joined`, `dependents` |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| A complete prediction of deletion effects | Tool: delete_elements with preview |
<!-- generated:contract:end -->

## Public inputs

- Required `element_id`: integer host element ID.
- Optional `relationships`: array chosen from `type`, `level`, `owner_view`, `host`, `parent`, `subcomponents`, `group`, `assembly`, `members`, `joined`, and `dependents`. Omission requests all; names are case-sensitive.
- Optional `offset`: default 0; `limit`: 1–200, default 100. The same window is applied independently to every requested relationship.
- Optional `expected_document_id`: exact read guard. Optional `_operation_id`: exact ID for an identical previous-request retry only.

## Example

Replace the illustrative ID with an actual host element ID. A targeted relationship list keeps the result focused.

```json
{ "element_id": 12345, "relationships": ["type", "host", "dependents"], "limit": 100 }
```

## Results and verification

The result identifies `document_id` and `element_id`; `relationships` contains one independently paginated result per requested kind. Each has `total_count`, `returned_count`, `offset`, `has_more`, `next_offset`, and element identities. Results remove duplicate/invalid IDs and sort by element ID. Continue unfinished kinds individually rather than assuming one continuation covers all kinds.

`host`, `parent`, and `subcomponents` are family-instance relationships. `members` covers group or assembly members; `group` and `assembly` identify an element's containing membership. Empty results can mean the relationship is inapplicable. Null related-element metadata is not permission to assume a missing element's identity.

In a family document, explicitly request kinds excluding `joined`; the default includes it and fails because `JoinGeometryUtils` requires a project. Joined geometry is distinct from physical overlap and from hosted/dependent relationships.

Verify the relationship kind answers the question and preserve host-document context when using returned IDs. No coordinate/unit conversion is involved. Separately finish any saved-response retrieval under [execution rules](../execution-rules.md).

## Effects and recovery

Read-only: no join, unjoin, deletion, selection change, save, or export. Invalid element IDs or relationship kinds fail. Relationship discovery does not authorize editing dependents. Follow [operation recovery](../operation-recovery.md) for stale document or bridge failures.

## Compatibility

Source reference: PI-Revit 0.5.0, [GetElementRelationships.cs](../../../../src/Revit/Tools/GetElementRelationships.cs), plus identity/retry inputs. Revit 2025–2027 bridge targets; source review only, with no new live validation.
