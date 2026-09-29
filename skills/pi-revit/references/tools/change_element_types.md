# change_element_types

## Purpose and boundaries

Assign discovered element types to explicit host elements. Use `get_element_types` to find types and inspect target compatibility. This does not edit a type definition; use `set_parameters` on a type ID when that is the intended change. Revit constraints may affect connected or hosted elements beyond the returned target snapshots.

Contract: PI-Revit 0.5.0 source, [ChangeElementTypes.cs](../../../../src/Revit/Tools/ChangeElementTypes.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `810351ef8bac44ab`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `updates` | array of object | yes |  |  |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Editing a type definition's parameters | Tool: set_parameters |
| Creating a new type | Revit API: ElementType.Duplicate. Check all its members in one call: `search_api_docs` with query `ElementType.Duplicate`, then use `execute_csharp` within the requested scope. |
| Loading a family | Revit API: Document.LoadFamily. Check all its members in one call: `search_api_docs` with query `Document.LoadFamily`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

| Input | Meaning |
| --- | --- |
| `updates` | Required: 1–200 objects with positive `element_id` and `type_id`. Each target element may appear only once. |
| `preview` | Default `false`; commit-validates accepted changes then rolls them back. |
| `atomic` | Default `false`: valid targets can commit independently of failures. `true` rolls the entire batch back if any target fails. |
| `expected_document_id` | Required for every call, including previews. Use the intended model's exact current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Follow [execution rules](../execution-rules.md). Revit validates that each type is an existing `ElementType` acceptable to the target. Missing, pinned, or incompatible targets fail per update. The tool does not unpin them.

## Example

IDs are illustrative; replace them with an inspected host target and a compatible discovered type.

```json
{
  "updates": [{ "element_id": 12345, "type_id": 34567 }],
  "preview": true,
  "atomic": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Read `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. Entries include `before`, `after`, `resulting_id`, `unique_id`, `replaced`, `resulting_id_is_temporary` and `inherited_state`. Snapshot locations are internal feet. `inherited_state` lists what the retyped element keeps: its traits, Mark and Comments and, for the first 20 updates, how many views hide or override it. A replaced element (new ID) loses per-view hiding and overrides.

Revit can replace an element during a type change. After commit, use `resulting_id` and `unique_id`, not an assumed unchanged ID. Any replacement ID in `proposed` is temporary after either preview or atomic rollback; it must not be reused. An atomic rejection can lack commit validation.

Reread committed results, confirm assigned types, inspect affected dependents, and follow [visual verification](../visual-verification.md) for visible changes. Follow [operation recovery](../operation-recovery.md) for timeouts or uncertain outcomes. A write following a preview is a new operation. This does not save the model.
