# get_linked_models

## Purpose and preconditions

Discover direct Revit link placements in the active host, including unloaded links, their identities, and transforms. Activate with `find_revit_tools`. This tool requires the intended active host document. It does not traverse nested links or load an unloaded link.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `dd234c17c5678369`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Loading or reloading an unloaded link | Revit API: RevitLinkType.Load. Check all its members in one call: `search_api_docs` with query `RevitLinkType.Load`, then use `execute_csharp` within the requested scope. |
| Nested links | Revit API: RevitLinkInstance.GetLinkDocument. Check all its members in one call: `search_api_docs` with query `RevitLinkInstance.GetLinkDocument`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

- Optional `offset`: default 0. Optional `limit`: 1–1000, default 100.
- Optional `expected_document_id`: exact host document guard.
- Optional `_operation_id`: exact previous operation ID for an identical retry, omitted for new reads.

## Example

```json
{ "offset": 0, "limit": 100 }
```

## Results and verification

Read `links`, `document_id`, `total_count`, `returned_count`, `has_more`, and `next_offset`. Placements sort by element ID. A row contains `link_instance_id`, `unique_id`, name/type ID, `loaded`, `linked_document_id`, document title, and `transform` with origin and basis vectors.

Copy the exact `linked_document_id` and `link_instance_id` into `get_linked_elements`. Its `expected_document_id`, if supplied, remains the **host** identity. An unloaded row has no linked document identity to query. Refresh discovery after unloading/reloading or reopening the linked document.

Transform origin uses internal feet; basis vectors encode orientation and scale and are not lengths to convert independently. The mapping is the instance's total transform into host coordinates. Two placements of one linked document have distinct instance identities and can produce different host coordinates. Preserve the placement when identifying linked elements.

Follow the returned instance pages separately from any saved-response continuation in [execution rules](../execution-rules.md). Verify load status and intended host/link before a linked query; names alone do not establish identity or position.

## Effects and recovery

Read-only: no link loading/unloading, positioning, model edit, save, or UI change. If a follow-up reports a stale linked identity, reread this tool against the intended host instead of substituting a different link. Follow [operation recovery](../operation-recovery.md) for bridge errors.

## Compatibility

Source reference: PI-Revit 0.4.0, [GetLinkedModels.cs](../../../../src/Revit/Tools/GetLinkedModels.cs), registry guard, and extension retry inputs. Source-reviewed for Revit 2025–2027 bridge targets, without new live validation.
