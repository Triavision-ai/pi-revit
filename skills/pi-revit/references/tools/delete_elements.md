# delete_elements

## Purpose and boundaries

Delete explicitly selected host elements and report the full deletion set returned by Revit, including deleted dependents. This is a single model-edit step. `get_element_relationships` dependents alone do not predict the complete deletion cascade, and the returned deleted IDs do not audit surviving elements modified by constraints.

Contract: PI-Revit 0.5.0 source, [DeleteElements.cs](../../../../src/Revit/Tools/DeleteElements.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `7c3880c989b85bb0`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `element_ids` | array of integer | yes |  |  |
| `preview` | boolean | no |  |  |
| `expected_deleted_ids` | array of integer | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Pinned elements (rejected, never unpinned automatically) | User action: Confirm unpinning (Element.Pinned) first |
| Elements inside linked models | User action: Edit the linked model itself |
| One-step purge of unused types | Tool: get_element_types with include_instance_count, then delete_elements |
<!-- generated:contract:end -->

## Inputs and preconditions

Follow [execution rules](../execution-rules.md), identify the exact requested removal, and inspect a preview before committing a deletion with dependencies.

| Input | Meaning |
| --- | --- |
| `element_ids` | Required: 1–200 distinct positive host IDs. Every requested element must exist and must not be pinned. |
| `preview` | Default `false`; validate through commit then roll back the enclosing group. |
| `expected_deleted_ids` | Optional exact full set of 1–10,000 distinct positive IDs from a prior preview. A different deletion set causes rollback. |
| `expected_document_id` | Required for preview and real deletion; copy the intended open model's exact current identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

The selection succeeds or rolls back together. Cascades above 10,000 deleted IDs are rejected and rolled back. The tool never unpins requested elements. `expected_deleted_ids` protects the deletion set, not every property, constraint, or surviving dependent effect since preview.

## Example

ID `12345` is illustrative. Discover the intended host target and use the actual overview identity.

```json
{
  "element_ids": [12345],
  "preview": true,
  "expected_document_id": "<project.documentId>"
}
```

For an authorized real deletion, use the complete returned preview `deleted_ids` as `expected_deleted_ids` and set `preview: false`. That changed request is a new operation; do not reuse the preview's `_operation_id`. If the set changes, inspect a fresh preview rather than dropping the check.

## Results, recovery and verification

Read `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. The successful/proposed step contains `deleted_ids`, `deleted_count`, and `dependent_ids`. A preview reports proposed removals; it does not remove the live targets after rollback.

After commit, verify the requested removals and inspect consequential model/visible changes. Follow [visual verification](../visual-verification.md) where appearance or documentation changes. Follow [operation recovery](../operation-recovery.md) before repeating a timed-out deletion; an absent client response does not mean nothing was deleted. This tool does not save the model.
