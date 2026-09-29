# execute_csharp

## Purpose and boundaries

Compile and execute synchronous C# on Revit's API thread for a task that available dedicated tools do not cover. Prefer dedicated query/edit tools when they support the operation. This is unrestricted code with possible model, UI, filesystem, and external effects; the transaction covers model edits only.

Contract: PI-Revit 0.5.0 source, [ExecuteCsharp.cs](../../../../src/Revit/Tools/ExecuteCsharp.cs). The current public schema is authoritative. This page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** yes. **Effects:** model, ui, files, external. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `2fa53a38ab9692cf`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `code` | string | yes |  |  |
| `inputs` | object | no |  |  |
| `expected_document` | string | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Automatic preview or rollback of custom code | Tool: dedicated tools with preview (set_parameters, transform_elements, delete_elements, manage_views, ...) |
| Undoing file, UI or external effects on rollback | User action: Inspect and clean up those effects explicitly |
| Saving the model | User action: Save only when the user asks |
<!-- generated:contract:end -->

## Inputs and preconditions

Read [execution rules](../execution-rules.md). Verify unfamiliar classes, methods, enum names, and overloads using `search_api_docs` against the installed API before writing code. Its top match includes detailed documentation; narrow a query to promote the required match. Use `manage_revit_scripts` to store and inspect an exact reusable version when appropriate; saving a script does not execute it or save a model.

| Input | Meaning |
| --- | --- |
| `code` | Required nonempty C# top-level script. Additional `using` directives are allowed at the top. |
| `inputs` | Optional JSON object, default `{}`, available separately as the `inputs` global. At most 100,000 JSON characters. Values are never interpolated into code. Validate types, nested values, ranges, units, and target identities inside the script. |
| `expected_document_id` | Required even for code intended only to inspect: copy the intended model's exact current overview identity. |
| `expected_document` | Optional additional document-title check; never replaces identity. |
| `_operation_id` | Optional extension argument only for an identical retry of the original request. Omit for a new operation. |

There is no `preview` argument and no automatic preview mode. Do not imply that a script execution will roll back merely because the user asked to preview. Use a dedicated tool with a preview contract or prepare an explicit non-editing inspection when that satisfies the request. Model saving, exports, UI actions, and external access must be within the requested scope.

## Script execution rules

- Available globals are `doc` (`Document`), `uidoc` (`UIDocument`), `uiapp` (`UIApplication`), `inputs` (`System.Text.Json.JsonElement`), and `Dump(value)` for intermediate result values.
- Default imports are `System`, `System.Linq`, `System.Collections.Generic`, `Autodesk.Revit.DB`, and `Autodesk.Revit.UI`. Add other namespaces explicitly, such as `Autodesk.Revit.DB.Architecture`.
- The backend owns one transaction named `execute_csharp`. Do not start another `Transaction` on `doc`; subtransactions are allowed. Compilation occurs before opening it. Normal completion commits; failure attempts rollback and reports confirmed cleanup status.
- Code must be fully synchronous. `async` and `await` are rejected at compilation checks. Never block on `Task.Result` or `.Wait()`; they can freeze the Revit thread.
- Lengths use internal decimal feet. Convert deliberately with `UnitUtils.ConvertToInternalUnits`/`ConvertFromInternalUnits`. Discover exact API signatures and document units rather than assuming meters.
- Filter at collector level (`OfCategory`, `OfClass`, `WhereElementIsNotElementType`) and keep loops bounded. Activate a required `FamilySymbol` before `NewFamilyInstance`.
- The call budget is 120 seconds, not a force-stop guarantee. Running Revit work cannot be interrupted mid-script. A client timeout or cancellation does not cancel backend work.
- The dialog guard attempts dismissive responses and returns `suppressedDialogs`. It can cancel a confirmation-dependent action and cannot guarantee handling every modal dialog. Inspect its report rather than assuming the requested action completed.

## Example

This script performs no model edit or save, but still uses the normal script transaction and required document guard. Replace the identity placeholder with the exact current overview identity.

```json
{
  "code": "var value = inputs.GetProperty(\"value\").GetDouble(); if (!double.IsFinite(value)) throw new ArgumentException(\"value must be finite\"); return new { value, nonnegative = value >= 0 };",
  "inputs": { "value": 12.5 },
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

The final expression/return becomes `returnValue`; `Dump` values appear in `dumps`; `durationMs` reports execution duration. Return primitives, strings, or anonymous objects/lists. Projection converts elements to compact identity objects, `ElementId` to an integer, `XYZ` to `{x,y,z}`, and parameters to value summaries. Other raw Revit objects do not round-trip.

Ordinary CLR value projection is bounded: 200 dumps, depth 6, 100 sequence/dictionary items, 40 properties, and 4,000 characters per string. Special projections, including copied `JsonElement` values, follow their own paths. These intrinsic caps differ from large-result retrieval through `read_revit_result`; paging the saved response cannot recover values never serialized. Explicitly bound and summarize returned data, and make completeness clear.

The bridge adds `model_changes` to every committed script result: the elements the script added, modified and deleted (counts plus up to 20 items with name and category), and `new_views` with the visibility state of up to three new views. A script that changed nothing reports only `{ "observed": false }`. In a family document, `model_changes.family` lists family types and parameters added, removed or changed; they are not elements and never appear in `added` or `modified`. `modified` also lists elements Revit updated as a side effect of regeneration (`modified_note` says so): report as changed only what the script targeted. A view duplicated in a script inherits its source's hidden categories, hidden elements and overrides; `new_views` shows them, so compare that state with the request instead of relying on a capture. `modified` lists existing objects the script changed; a PI-Revit scope note marks one that predates the request and that the request names.

A `returnValueError` can accompany a successfully committed script whose return value failed projection. Do not rerun the script merely to recover that value. Report `commitWarnings` and `suppressedDialogs`; inspect errors for whether rollback was actually confirmed. Files, UI, and external effects are not undone by model rollback.

Follow [operation recovery](../operation-recovery.md) after a timeout, cancellation, failed result save, or uncertain response. Reuse an operation ID only with identical code, inputs, identity, and all other arguments. Reread the affected model state; follow [visual verification](../visual-verification.md) for visible results. No automatic model save is performed by the tool, but arbitrary code can request one, so inspect intended code effects before executing it.
