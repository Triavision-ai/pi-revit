# Operation recovery

Read after a timeout, cancellation, uncertain result, or a failure that may have left effects. [get_revit_operation](tools/get_revit_operation.md) documents receipt inputs and outputs. The bridge's deduplication and session routing enforce retry behavior; Markdown does not supply these guarantees.

## Resolve the original outcome

1. Retain the exact original tool, arguments, intended document identity, operation ID, and bridge identity. Supporting bridges assign IDs automatically; the extension includes them in responses or request errors.
2. Read the original receipt with `get_revit_operation`. This bypasses the model thread and does not queue another model action. Receipt reads and identical retries route to the original bridge regardless of the selected target for new calls; they do not change selection.
3. Interpret state and result together. `queued` or `running` is unfinished. `expired_before_start` confirms no tool action began. `succeeded` means the call completed, not that a change committed: a successful preview can report `committed: false`. `failed` and `result_unavailable` do not establish rollback. `unknown`, an unavailable original bridge, or a restarted session does not prove the action never ran. <!-- inv:uncertain-outcome -->
4. If a retry is needed, add the exact `_operation_id` to the original tool call and preserve every other argument. <!-- inv:receipt-routing --> The bridge waits for or returns that operation's result without repeating it. A tool/argument mismatch is rejected. Omitting `_operation_id` creates a new operation. Applying a preview is a new operation because the arguments change, but do that only after the preview outcome is known.
5. If the receipt cannot establish the outcome, inspect the original model and other effects before issuing a new modification. Stop dependent edits when the outcome remains consequentially uncertain; report what is known and what access or evidence is missing. Do not change instances or manufacture a new ID to bypass uncertainty.

## Retention and restart limits

The bridge keeps at most 128 full completed results totaling 32 MiB. Eviction leaves the ID and outcome reserved for the remainder of that bridge session, with `result_available: false`; an identical retry neither reconstructs the result nor executes again. At 10,000 records, new tracked calls are rejected while retained receipts remain queryable. Restart clears receipts and changes bridge identity. An old operation ID cannot be replayed against the new session. Older bridges without tracking have no receipt guarantee.

Client cancellation does not cancel bridge execution. A queued operation may still begin before its deadline, and started work cannot be interrupted. Standard bridge calls have a 30-second client budget; `execute_csharp`, `capture_view`, and `export_documents` have 120 seconds. A timeout can leave already-started work running. Wait or inspect the existing receipt according to the task; do not flood the queue with fresh calls.

## Failure triage

| Symptom | Interpretation and next action |
| --- | --- |
| Exact document identity rejected | No tool action was performed. Activate the intended model, read its overview, and pass the refreshed identity unchanged. |
| Several instances or selected session unavailable | [List/select the intended instance](tools/manage_revit_instances.md) and refresh the overview for new work. Choosing another instance does not resolve an old operation. |
| Bridge unavailable | Revit is closed, still starting, or the add-in did not load. Use [ping](tools/ping.md); when user action is needed, explain that Revit/add-in must be running. |
| HTTP 409, `hasActiveDocument: false` | Revit is running without an open project. Open the intended project before document calls; blindly waiting/retrying cannot fix this. |
| Timeout or modal dialog | Work may be queued or running. Inspect its receipt and any actual effects before a new action. |
| Large-result local save failed | Revit may already have finished. The error carries the operation ID on tracked bridges; recover the retained result through the receipt. Do not rerun a write to recover its display. |
| Partial selection/isolation/export failure | Earlier UI changes or incomplete files may remain. Inspect reported paths/effects; rollback cannot undo files or earlier UI actions. IFC commit warnings also need inspection. |
| Script `returnValueError` | Result projection can fail after the model committed. Read commit status and inspect effects; do not infer rollback from missing output. |
| Installed/loaded version mismatch | Use [ping](tools/ping.md) and the package's installation/upgrade guidance. Updating the Pi package alone does not deploy a matching Revit add-in. |

Large recovered receipts may themselves use [saved-result continuation](tools/read_revit_result.md). Keep receipt status, transaction status, artifact existence, and visual correctness separate in the final report.
