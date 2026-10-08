# manage_selection

## Purpose and boundaries

Read or change Revit's host-element selection, zoom to elements, or apply Temporary Hide/Isolate in the active view. Query with `get_elements` first, then pass returned IDs; selection has no inline element filter. Linked element IDs are not host IDs.

Contract: PI-Revit 0.5.0 source, [ManageSelection.cs](../../../../src/Revit/Tools/ManageSelection.cs) and [DocumentGuard.cs](../../../../src/Revit/Tools/DocumentGuard.cs). The current public schema is authoritative. This page documents source behavior, not a live-model test.

<!-- generated:contract:start (generated from the tool contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** ui, model. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `0108ff205d518aaa`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | no |  | `get`, `set`, `add`, `remove`, `clear`, `zoom` |
| `element_ids` | array of integer | no |  |  |
| `isolate_in_view` | boolean | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Permanent hiding or view visibility changes | Revit API: View.HideElements; View.SetCategoryHidden. Check all its members in one call: `search_api_docs` with query `View.HideElements; View.SetCategoryHidden`, then use `execute_csharp` within the requested scope. |
| Saved selection sets | Revit API: SelectionFilterElement.Create. Check all its members in one call: `search_api_docs` with query `SelectionFilterElement.Create`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

| Input | Meaning |
| --- | --- |
| `action` | `get` (default), `set`, `add`, `remove`, `clear`, or `zoom`. |
| `element_ids` | Host element IDs. Required and nonempty for `set`/`add`/`remove`. Zoom uses them when supplied; otherwise it uses the current nonempty selection. Zoom is refused (nothing changes) when no open view shows any of the targets, because Revit would raise a modal dialog and switch the active view: open a view that shows them first with `open_view`. Use `clear` to empty selection. |
| `isolate_in_view` | Default `false`. Temporarily isolates the resulting selection or zoom targets. An empty target set resets Temporary Hide/Isolate. Requires an active graphical view. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Read [execution rules](../execution-rules.md) for document targeting. Plain `get` only reads the selection of the active model; every non-`get` action, and any call with `isolate_in_view: true` (including with `get`), changes the UI of the bound model. Use `get` when the user refers to the current selection. Invalid IDs are reported in `not_found` if valid IDs remain; a request containing IDs but no valid targets fails. Duplicate IDs are resolved once.

## Example

Illustrative IDs must be replaced with discovered host IDs.

```json
{
  "action": "set",
  "element_ids": [12345, 12346],
  "isolate_in_view": true
}
```

To reset isolation explicitly, use `action: "clear"` with `isolate_in_view: true`; this also clears selection.

## Results, effects and recovery

`get` returns `count` and element identities. Changes return `selectedCount`; zoom returns `shownCount` (targets some open view shows) and, when only some are shown, `not_shown_in_open_views`. Isolation reports `viewId`, `viewName`, `isolatedCount` or `temporaryIsolateReset`, and any `commitWarnings`.

Selection and zoom alter UI state. Isolation changes temporary view state through a small internal transaction. The tool is read-classified but declares possible `ui` and `model` effects; classification is not permission to mutate. There is no `preview` or all-or-nothing contract covering selection plus isolation. Selection/zoom may already have completed when isolation fails; those effects are not rolled back.

Read selection again or inspect the actual view to verify the requested result; use [visual verification](../visual-verification.md) for visible focus/isolation outcomes. For an uncertain result, follow [operation recovery](../operation-recovery.md) and inspect existing UI state before repeating. This does not save the model.
