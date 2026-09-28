# manage_revit_scripts

## Purpose and preconditions

Maintain reusable C# definitions and run an explicitly chosen immutable version. The local library is `%APPDATA%\pi-revit\scripts`, containing versions and run history. `save`, `list`, `read`, and `history` work without Revit or an open model. `run` needs the intended open document, its exact identity, and a bridge with operation receipts.

Read the exact saved source before running it. A library run has the same unrestricted model/UI/file/external effects as [execute_csharp](execute_csharp.md), including one backend-owned transaction, synchronous execution, and a 120-second budget. Saving a definition neither executes it nor saves the Revit model. There is no library preview mode or automatic model saving.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** yes. **Effects:** model, ui, files, external. **Requires an open document:** no.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `3a257b284de8dae5`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | yes |  | `list`, `save`, `read`, `run`, `history` |
| `name` | string | no |  |  |
| `version` | string | no |  |  |
| `description` | string | no |  |  |
| `code` | string | no |  |  |
| `input_types` | object | no |  |  |
| `inputs` | object | no |  |  |
| `expected_document_id` | string | no |  |  |
| `_operation_id` | string | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |

| Not covered by this tool | Use instead |
| --- | --- |
| Preview or rollback of a script run | Tool: dedicated tools with preview (set_parameters, transform_elements, delete_elements, manage_views, ...) |
<!-- generated:contract:end -->

## Public inputs

| Field | Contract |
| --- | --- |
| `action` | Required `list`, `save`, `read`, `run`, or `history`. |
| `name` | Required for save/read/run; optional exact filter for list/history. 1–64 lowercase letters, digits, underscores or hyphens, beginning with a letter. |
| `version` | Required for read/run: exact saved 64-character lowercase SHA-256 hash. There is no implicit latest version. |
| `description` | Required for save; string of at most 2,000 characters. |
| `code` | Required for save; nonblank source of at most 100,000 characters. |
| `input_types` | Required for save; object declaring at most 40 named inputs. Kinds: `string`, `number`, `integer`, `boolean`, `object`, `array`. |
| `inputs` | Object for run, defaults to `{}`; every declared input is required, undeclared inputs are rejected. Maximum 100,000 JSON characters. |
| `expected_document_id` | Required nonempty exact overview identity for run, even for code that only reads. |
| `_operation_id` | Optional for an identical retry of run; copy the original ID and preserve version, inputs, and document. Omit for new work. |
| `offset`, `limit` | List/history pagination: nonnegative offset default `0`; limit `1`–`100`, default `20`. |

Input names start with a letter and contain at most 64 letters, digits, or underscores. Validation checks only top-level kinds; scripts must validate nested contents, ranges, units, IDs, and domain rules. `integer` requires a safe integer; `number` requires a finite number. Inputs reach the script as a separate `System.Text.Json.JsonElement`, never as substituted source text.

## Examples

Save a definition that only echoes a numeric check:

```json
{
  "action": "save",
  "name": "check_number",
  "description": "Echo a supplied number and report whether it is nonnegative.",
  "code": "var value = inputs.GetProperty(\"value\").GetDouble(); return new { value, nonnegative = value >= 0 };",
  "input_types": { "value": "number" }
}
```

Read it before running. Replace the illustrative hash below with the exact `version` returned by save:

```json
{
  "action": "read",
  "name": "check_number",
  "version": "0000000000000000000000000000000000000000000000000000000000000001"
}
```

Then run that version, replacing both the hash and document placeholder with actual returned identities:

```json
{
  "action": "run",
  "name": "check_number",
  "version": "0000000000000000000000000000000000000000000000000000000000000001",
  "expected_document_id": "<project.documentId from the intended model overview>",
  "inputs": { "value": 12.5 }
}
```

This source performs no model edit or save; its run still uses the normal script transaction and receipt. Inspect paged local run history:

```json
{ "action": "history", "name": "check_number", "offset": 0, "limit": 20 }
```

## Results and effects

Save returns `name`, `version`, `created_at`, `file_path`, and `executed: false`. The hash covers source, description, and normalized input declarations. Saving identical content returns the same immutable definition; reads integrity-check saved content before use. Read returns the source, input declarations, and metadata. List returns versions with descriptions/input declarations and `next_offset`.

Run forwards the saved source and validated inputs to `execute_csharp`; inspect its result and receipt rather than inferring a commit from local history. History returns records newest first, including version, document identity, input hash, timestamps, run ID, and available bridge/operation receipt identifiers. It excludes raw input values/results. `prepared` is not proof of dispatch or execution, `response_received` is not proof of a successful model edit, and `outcome_unconfirmed` requires receipt inspection.

## Failures, recovery, and verification

Invalid names/hashes, altered saved content, missing inputs, extra inputs, or invalid kinds are rejected. Read and inspect another version explicitly rather than guessing latest. If local history preparation fails, do not assume a dispatch occurred; follow the reported error/receipt. If final history updating fails after a response, the returned result warns about it: use the operation receipt.

After interruption, check [get_revit_operation](get_revit_operation.md) before an identical retry with original `_operation_id`, version, document, and inputs. Client cancellation does not stop running C#. Follow [operation recovery](../operation-recovery.md) and inspect actual model/file/UI effects when uncertain. Use [visual verification](../visual-verification.md) for visible results. Library metadata and a returned response do not prove that a requested model outcome is correct.

## Compatibility

Contract source: `extensions/pi-revit/script-library.ts`, with dispatch through `extensions/pi-revit/index.ts`. Local storage operations depend on the package; run also depends on the bridge's `execute_csharp` contract and operation-tracking support. Check unfamiliar API members using [search_api_docs](search_api_docs.md) for the selected Revit version before saving/running code.
