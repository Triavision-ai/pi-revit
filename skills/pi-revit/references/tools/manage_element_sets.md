# manage_element_sets

## Purpose and boundaries

Retain a temporary snapshot of matching host-element identities for reuse, without changing model contents or selection. Membership stays fixed; reads show current values and report members that were deleted or whose identity changed. This is not a saved Revit selection set or a live query subscription.

Contract: PI-Revit 0.4.0 source, [ManageElementSets.cs](../../../../src/Revit/Tools/ManageElementSets.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** no. **Effects:** session. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `none`: no durable outcome to check; report what was done.
- **Contract hash:** `6933cbc169654cfd`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `create`, `list`, `read`, `forget` |
| `query` | object | no |  |  |
| `label` | string | no |  |  |
| `set_id` | string | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `parameter_names` | array of string | no |  |  |
| `include_type_parameters` | boolean | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Saved Revit selection sets | Revit API: SelectionFilterElement.Create. Check all its members in one call: `search_api_docs` with query `SelectionFilterElement.Create`, then use `execute_csharp` within the requested scope. |
| Live query subscriptions | Tool: get_elements (run the query again) |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md). Every action requires an open document, including list/forget; a set belongs to the exact open document and bridge session.

| Input | Meaning |
| --- | --- |
| `action` | Required: `create`, `list`, `read`, or `forget`. |
| `query` | Creation scope with `category`, `of_class`, `level`, `type_id`, `in_active_view`, and `filter` supported by `get_elements`. Omission means all host instances, subject to the cap. |
| `label` | Optional creation label up to 160 characters, default `Element set`. |
| `set_id` | Required for read/forget: exact returned set ID. |
| `offset`, `limit` | Read paging, default 0/100; limit range 1–1000. |
| `parameter_names` | Read projections: at most 20 display names, built-in enum names, or `guid:<GUID>` identities. |
| `include_type_parameters` | Read option, default false; also project the requested identities from each element's type. |
| `expected_document_id` | Optional current exact overview identity, always checked if supplied. A saved set separately enforces its own original document identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Creation covers the entire query scope, with at most 10,000 matched elements. Query-level `offset`, `limit`, `count_only`, `fields`, and parameter projections are rejected; place read paging/projections at the outer level. Filter semantics and units are those of `get_elements`: numeric filter inputs use explicit units or document display units. Prefer exact built-in/GUID parameter identities over translated or ambiguous names.

At most 32 sets are retained across the bridge session. Sets expire 30 minutes after creation; reads do not extend expiry. They do not survive document close/reopen or bridge restart. `list` shows only sets for the current document. Forget an unneeded set or narrow the scope rather than assuming additional capacity.

## Example

Create a host-wall snapshot for later inspection; replace the document placeholder with the intended model's identity.

```json
{
  "action": "create",
  "query": { "category": "OST_Walls" },
  "label": "Walls for audit",
  "expected_document_id": "<project.documentId>"
}
```

Read the returned set ID with optional projections:

```json
{
  "action": "read",
  "set_id": "<set_id from create>",
  "offset": 0,
  "limit": 100,
  "parameter_names": ["ALL_MODEL_INSTANCE_COMMENTS"],
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Create returns `set_id`, `document_id`, `label`, `total_count`, `expires_at`, and query warnings. List returns metadata for current-document sets. Forget returns `forgotten: true`.

Read returns `snapshot_count`, `visited_count`, `returned_count`, `next_offset`, current `elements`, and `missing` with `deleted`/`identity_changed` reasons. Follow `next_offset`, which advances by visited members including missing ones; `returned_count` can be smaller. Do not rerun the original filters implicitly: previously matched elements may now have different values.

Parameter projections include `requested`, `isType`, `found`, `ambiguous`, and all matching values. Treat missing/ambiguous matches explicitly. Measurable numeric raw `value` uses internal units; `displayValue` is document-formatted. Check missing identities before passing current returned IDs to write tools, and obtain authorization for the actual modification separately from creating a set.

Effects are bridge-session memory only; there is no model transaction, preview, or save. Follow [operation recovery](../operation-recovery.md) for uncertain outcomes. An identical retry retrieves the original response, not fresh set contents; a new read is needed for current state. Unknown/expired sets require fresh discovery and creation, not guessing old membership.
