# get_element_details

## Purpose and preconditions

Inspect known host elements: parameters, type information, location, model bounding box, or materials. This core tool requires the intended active document and discovered element IDs. For listing/filtering use `get_elements`; linked IDs do not identify elements in the host document.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `37ca1810049de7e3`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `element_ids` | array of integer | yes |  |  |
| `parameter_names` | array of string | no |  |  |
| `include` | object | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Listing or filtering elements | Tool: get_elements |
| Elements inside linked models | Tool: get_linked_elements |
| Exact solid geometry or faces | Revit API: Element.Geometry(Options). Check all its members in one call: `search_api_docs` with query `Element.Geometry`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

- Required `element_ids`: 1–50 integer IDs per call. Batch larger requests yourself.
- Optional `parameter_names`: exact case-insensitive localized display names or `BuiltInParameter` enum names. Omission or an empty list returns all applicable parameters. This details filter does **not** support `guid:<GUID>` lookup; that syntax belongs to `get_elements` projections and filters.
- Optional `include`: `parameters` defaults true; `type_parameters`, `location`, `bounding_box`, and `materials` default false. Type parameters can be requested independently of instance parameters.
- Optional `expected_document_id`: exact document read guard. Optional `_operation_id`: previous exact operation ID for identical retry only.

## Example

The numeric ID is illustrative: replace it with an ID discovered in the intended host model.

```json
{
  "element_ids": [12345],
  "parameter_names": ["ALL_MODEL_MARK", "ALL_MODEL_INSTANCE_COMMENTS"],
  "include": { "parameters": true, "type_parameters": true, "location": true, "bounding_box": true }
}
```

## Results and verification

`count` and `elements` cover found inputs; always inspect `not_found`. Returned identity includes type ID/name and level ID. Parameter rows carry `storageType`, `isType`, `isReadOnly`, `value`, and `displayValue`, with `builtInParameter`, shared GUID, and display-unit metadata where available. Preserve multiple same-named parameters; their names alone do not prove a unique writable identity.

Raw measurable values are internal Revit units, independent of the field named `unit` (which describes the formatted display unit). Locations, curve lengths, and bounding boxes use internal feet; point rotation is radians; material area/volume use square/cubic feet. There is no requested output-unit input. `displayValue` is formatted text and may be null.

`location` can be null, a point, or a bound curve's endpoints/length. `boundingBox` can be null and is not exact geometry. Material data comes from non-paint material IDs; unavailable areas/volumes can be omitted. Empty/partial geometry metadata is not evidence that an element has no physical geometry.

There is no query paging: batches are capped at 50 IDs. Finish any separately saved large response using [execution rules](../execution-rules.md). An empty parameter-name match in a localized model should prompt discovery of the actual name or built-in identity, not an assumption that the value is absent.

## Effects and recovery

Read-only: no edits, selection changes, saves, or exports. Missing individual IDs appear in `not_found`; an empty or oversized input batch fails. Re-discover replaced/deleted elements before a later write. Use [operation recovery](../operation-recovery.md) for identity/transport uncertainty; visual appearance still needs [visual verification](../visual-verification.md) when the broader task changes visible content.

## Compatibility

Source reference: PI-Revit 0.5.0, [GetElementDetails.cs](../../../../src/Revit/Tools/GetElementDetails.cs), with registry identity and extension retry inputs. Revit 2025–2027 bridge targets; source-reviewed, not newly validated live.
