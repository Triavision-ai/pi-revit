# open_view

## Purpose and boundaries

Queue activation of an existing view or sheet in the Revit UI, like opening it in the Project Browser. It creates no view and edits no model data. Use `manage_views` or `manage_sheets` for authoring.

Contract: PI-Revit 0.5.0 source, [OpenView.cs](../../../../src/Revit/Tools/OpenView.cs). The current public schema is authoritative. This page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** ui. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `none`: no durable outcome to check; report what was done.
- **Contract hash:** `6bb71caab2e5304b`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `view_id` | integer | no |  |  |
| `name` | string | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Creating views | Tool: manage_views |
<!-- generated:contract:end -->

## Inputs and preconditions

| Input | Meaning |
| --- | --- |
| `view_id` | Existing view or sheet ID. Takes precedence over `name`. Discover with `get_elements` or use a committed creation result. |
| `name` | When no ID is supplied: exact view name, sheet number such as `A-101`, or `number - name`, matched case-insensitively. Ambiguous names fail; use an ID. |
| `expected_document_id` | Required even though the tool is read-classified: activation changes UI state. Copy the intended model's exact current `project.documentId`. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

At least one of `view_id` or `name` is required at execution. View templates and Revit views that cannot be activated are rejected. An active edit can prevent activation; resolve it before retrying. Follow [execution rules](../execution-rules.md).

## Example

ID `23456` is illustrative; replace it with a discovered or committed view ID.

```json
{
  "view_id": 23456,
  "expected_document_id": "<project.documentId>"
}
```

## Results, effects and verification

The result contains `requestedViewId`, `viewName`, and `viewType`. It confirms that activation was queued, not that a screenshot already shows that view. Revit performs the view change after control returns to it.

There is no preview transaction and no model save. A capture relying on the active view in the same call batch can show the previous view. Prefer a subsequent `capture_view` with the explicit target `view_id`, open the returned image, and follow [visual verification](../visual-verification.md).

If a call times out or its status is unclear, follow [operation recovery](../operation-recovery.md); check the requested view and actual UI before issuing a new operation.
