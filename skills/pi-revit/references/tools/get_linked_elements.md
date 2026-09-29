# get_linked_elements

## Purpose and preconditions

Query or count elements inside one loaded direct Revit link. Activate with `find_revit_tools`, select the intended host document, then discover the placement and linked identity using `get_linked_models`. It neither traverses nested links nor queries linked elements by host active-view visibility.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Contract hash:** `7f454a594862865c`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `category` | string | no |  |  |
| `of_class` | string | no |  |  |
| `filter` | object | no |  |  |
| `level` | string | no |  |  |
| `type_id` | integer | no |  |  |
| `count_only` | boolean | no |  |  |
| `parameter_names` | array of string | no |  |  |
| `include_type_parameters` | boolean | no |  |  |
| `fields` | array of string | no |  | `id`, `name`, `category`, `typeName`, `levelId` |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `link_instance_id` | integer | yes |  |  |
| `expected_linked_document_id` | string | yes |  |  |
| `include_bounds` | boolean | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Nested links | Revit API: RevitLinkInstance.GetLinkDocument on the nested link instances. Check all its members in one call: `search_api_docs` with query `RevitLinkInstance.GetLinkDocument`, then use `execute_csharp` within the requested scope. |
| Editing linked elements | User action: Edit the linked model itself |
<!-- generated:contract:end -->

## Public inputs

- Required `link_instance_id`: integer host link placement ID. Required `expected_linked_document_id`: exact linked identity returned by discovery.
- Optional `include_bounds`: default false; adds host-coordinate axis-aligned bounds.
- Optional query inputs inherited from [get_elements](get_elements.md): `category`, `of_class`, `level`, `type_id`, `filter`, `count_only`, `fields`, `parameter_names` (up to 20), `include_type_parameters`, `offset`, and `limit` (default 200, 1–1000).
- Do not pass `in_active_view`; it is absent from the public linked-query schema. Level/type IDs belong to the linked document.
- Optional `expected_document_id`: exact **host** read guard, separate from the required linked identity. Optional `_operation_id`: exact identical-retry ID only.

## Example

The number and identity text are placeholders. Replace them with discovery results for the intended host/link placement.

```json
{
  "link_instance_id": 12345,
  "expected_linked_document_id": "<linked_document_id from get_linked_models>",
  "category": "OST_Walls",
  "include_bounds": true,
  "limit": 100
}
```

## Results and verification

The query's counts, pagination, filter warnings, and parameter projections follow `get_elements`. Additional result context identifies the host document, linked document, link placement, and coordinate unit. Each returned element has `unique_id` and a full `reference` containing host identity, link instance ID, linked identity, and linked element ID. Preserve that whole reference, especially when a linked model is placed more than once.

Linked IDs must never be passed directly to host selection or write tools. A number can also exist in the host and identify a different element.

`host_bounds`, when requested, is null if the element has no model box; otherwise it is the host-axis-aligned envelope of all eight transformed box corners, in internal feet. It is approximate enclosing geometry, not a solid intersection or verified clash. Projection raw parameter values remain internal units from the linked document; numeric filters use explicit units or that linked document's display units.

Follow `next_offset` until the needed query is complete; separately finish any saved-response continuation from [execution rules](../execution-rules.md). Verify the exact host, linked identity, placement, and scope before reporting counts or positions.

## Effects and recovery

Read-only: no link loading, edits, selection, save, or export. Invalid placement, unloaded link, or stale linked identity fails. Discover current link identities again after load changes. For transport/host identity errors follow [operation recovery](../operation-recovery.md).

## Compatibility

Source reference: PI-Revit 0.5.0, [GetLinkedElements.cs](../../../../src/Revit/Tools/GetLinkedElements.cs) and inherited `GetElements` contract, plus identity/retry overlays. Supported bridge targets: Revit 2025–2027. No new live validation is claimed.
