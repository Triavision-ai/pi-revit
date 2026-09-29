# search_api_docs

## Purpose and preconditions

Search the API documentation belonging to the selected running Revit installation before writing unfamiliar C# calls. This is a core tool; it requires a reachable bridge but no open document. The index reads `RevitAPI.xml` and `RevitAPIUI.xml` beside the loaded assemblies and supplements public enum members from assembly metadata. It does not search Autodesk product Help or explain a complete modeling workflow.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** none. **Requires an open document:** no.
- **Contract hash:** `dea6dc11655529a1`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `query` | string | yes |  |  |
| `kind` | string | no |  | `type`, `method`, `property`, `field`, `event` |
| `max_results` | integer | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Autodesk product Help or modeling guidance | User action: Autodesk Revit Help or project standards |
| Proof that code compiles or works | Tool: execute_csharp |
<!-- generated:contract:end -->

## Public inputs

- Required `query`: a nonempty class/member name or substring. Prefer `Wall.Create`, `FilteredElementCollector`, or an enum value over a natural-language question. To verify several members at once, separate up to 10 names with `;`, for example the reference of a tool's API limit.
- Optional `kind`: `type`, `method`, `property`, `field`, or `event`. Methods include constructors; enum values are fields.
- Optional `max_results`: 1–50, default 10; for a multi-member query, per member, default 3. There is no offset or continuation page.
- Optional `_operation_id`: only for an identical retry of a previous bridge request; omit for new searches. This tool has no `expected_document_id` input.

## Example

Narrow the signature to choose an overload; inspect the result rather than assuming the example identifies the required overload for your task.

```json
{ "query": "Wall.Create(Document, Curve", "kind": "method", "max_results": 5 }
```

Verify every member a script needs in one call instead of one search per member. `find_revit_tools` gives each API limit a ready `lookup` query of this form:

```json
{ "query": "View3D.CreatePerspective; ViewOrientation3D; View.CropBox" }
```

## Results and verification

A multi-member query returns `lookups` and `results` instead of `matches`: per member its `query`, `totalMatches`, the `best` match (signature, summary, shortened remarks, parameters, returns, and up to three documented exceptions, which state when a member refuses), up to two `alternatives` by signature, and a `note` for a rewrite or a miss. Search a member again on its own when its full remarks or other overloads matter.

For a single query, inspect `matches`, `totalMatches`, `returnedCount`, `sources`, and `warnings`. Each match includes signature, assembly, summary, remarks, parameter/return/exception documentation, and `since` where documented. Exact names rank ahead of prefixes and substrings; same-named overloads favor simpler signatures. Narrow the query to bring the relevant overload to the top.

Pi receives the structured JSON payload with the full available documentation for returned matches, or a saved-result envelope when it is large; retrieve that result as described in [execution rules](../execution-rules.md). The bridge also builds compact display text, but the current Pi extension does not present it to the model. A top match's presence does not establish that its class is suitable for the desired modeling operation. Read parameter restrictions and confirm the installed Revit version.

Creation-factory queries such as `Document.Create.NewRoom` and accessor queries such as `Element.get_Parameter` can be rewritten to the documented member. Verify the returned signatures: the rewrite note exists only in the bridge's compact display text and is not included in Pi's structured result. Public enum values can be searchable even when the XML gives them no description. Missing XML content is a documentation limitation, not proof that a member is absent from the API.

## Effects and recovery

The first query builds a process-wide lazy index and may take several seconds. Later searches reuse it. It performs no model/UI mutation, save, or export. Missing or unparsable documentation yields warnings; an empty index fails. For transport uncertainty use [operation recovery](../operation-recovery.md). Do not invent signatures when documentation is incomplete; report the gap and use verified API sources or inspection.

## Compatibility

Source reference: PI-Revit 0.5.0, [SearchApiDocs.cs](../../../../src/Revit/Tools/SearchApiDocs.cs), with the extension's optional retry input. This manual describes source behavior, not a new live validation. Search results depend on the selected installed Revit version (supported bridge targets: 2025–2027).
