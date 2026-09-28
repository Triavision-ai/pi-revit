# get_revit_operation

## Purpose and preconditions

Read a retained operation receipt after timeout, cancellation, or another uncertain outcome. The original bridge must remain reachable and support operation tracking. No open document or Revit model-thread execution is required. Selection of another bridge does not redirect old receipt reads.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** no. **Effects:** none. **Requires an open document:** no.
- **Contract hash:** `a21eea435d7eef6d`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `operation_id` | string | yes |  |  |

| Not covered by this tool | Use instead |
| --- | --- |
| Operations from an earlier bridge session | User action: Inspect the model state directly; receipts do not survive a restart |
<!-- generated:contract:end -->

## Public inputs and example

`operation_id` is a required string of 1–120 characters copied exactly from the original response or error. It contains a bridge generation and request UUID. Do not invent an ID from an element or document ID. There is no `expected_document_id` or `_operation_id` input on this reader.

Replace the example with the real original ID:

```json
{
  "operation_id": "00000000000000000000000000000001:00000000-0000-4000-8000-000000000002"
}
```

## Results and interpretation

A known receipt returns `operation_id`, `bridge_id`, `tool`, `state`, timestamps, `result_available`, `result_http_status`, and the retained `result` when present. An unknown receipt returns its identity, `state: "unknown"`, and an explanatory message. Large receipts can use [saved-result continuation](read_revit_result.md).

| State | Meaning |
| --- | --- |
| `queued`, `running` | Work is unfinished; the reader does not cancel it. |
| `succeeded` | Call completed. Inspect transaction/result fields; a successful preview need not commit. |
| `failed` | Inspect the failure and effects; this does not prove rollback. |
| `expired_before_start` | No tool action began. |
| `result_unavailable` | Response/receipt construction failed; effects may already exist. |
| `unknown` | No receipt in that session; this does not prove the action never ran. |

`result_available: false` can also mean a completed result was evicted while its receipt/outcome stayed reserved. Receipt retrieval does not execute the original tool, change the selected instance, or cancel work. Its request budget is 10 seconds.

## Failures, recovery, and verification

An unavailable original bridge cannot be replaced with a different selected instance. Restart changes its identity and clears receipts. Follow [operation recovery](../operation-recovery.md), preserving the original tool and all arguments. Only repeat an identical request through its original tool with the original `_operation_id`; a reader call itself is not the retry.

Compare the receipt result with actual model/UI/file effects when needed. Retained limits are 128 full results / 32 MiB, with up to 10,000 reserved operation records per bridge session; eviction does not allow re-execution. Older bridges without tracking cannot supply this guarantee. Contract sources: `extensions/pi-revit/index.ts` (`registerOperationReader`), `extensions/pi-revit/instance-router.ts`, and `src/Revit/OperationStore.cs`.
