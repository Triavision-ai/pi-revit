# read_revit_result

## Purpose and preconditions

Read bounded fragments of an oversized response saved locally by this extension. Revit and an open model are unnecessary. Use the opaque `result_id` returned with `file_path` and `complete_inline: false`; this is not an arbitrary file-reading tool or a new query against Revit. <!-- inv:saved-result-sandbox -->

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** no. **Effects:** none. **Requires an open document:** no.
- **Contract hash:** `b318aeb83a3acd30`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `result_id` | string | yes |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |

| Not covered by this tool | Use instead |
| --- | --- |
| Paging the original query | Tool: the original query tool (for example get_elements) with its next_offset |
<!-- generated:contract:end -->

## Public inputs and example

| Field | Contract |
| --- | --- |
| `result_id` | Required string returned by this extension instance. |
| `offset` | Optional nonnegative integer; default `0`. Use the previous `next_offset`. |
| `limit` | Optional integer `1`–`8000`; default `8000`. JSON escaping may shorten the actual fragment. |

Replace the illustrative UUID with the real returned ID:

```json
{
  "result_id": "00000000-0000-4000-8000-000000000001",
  "offset": 0,
  "limit": 8000
}
```

## Results and effects

The result contains `result_id`, `offset`, `returned_chars`, `total_chars`, `has_more`, `next_offset`, `fragment: true`, and `text`. Concatenate `text` fragments in order; each page is not a standalone JSON result. Offsets count UTF-16 code units, not bytes or Unicode characters. Follow returned offsets until `has_more` is false when complete data is needed. The model-facing response remains bounded including JSON metadata/escaping, so do not infer progress from requested `limit`.

This reads a local saved file without contacting Revit or changing the model. Result IDs last for the current extension instance. After reload, use the original absolute `file_path` with Pi's `read` tool while the file exists. Saved-result continuation does not remove the original query's limits or replace its own row/column/group pagination.

## Failures, recovery, and verification

An unknown ID can mean the extension reloaded; use the original path. An offset past `total_chars` or invalid limit is rejected. A missing local file requires another source of the original result. On a tracked bridge, [get_revit_operation](get_revit_operation.md) may retain it; do not repeat a model write just to recover its output. A large-result save error can occur after Revit finished, so follow [operation recovery](../operation-recovery.md).

Confirm continuity of returned offsets and final completion before treating the reconstructed payload as complete. Keep the query's truncation and pagination labels intact. Contract source: `extensions/pi-revit/index.ts` (`modelContent`, `registerResultReader`); this is a local package contract independent of the loaded Revit version.
