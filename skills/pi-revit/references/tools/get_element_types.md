# get_element_types

## Purpose and preconditions

Discover loaded element types and family symbols, optionally distinguishing placed types from unused loaded types. This core tool requires the intended active document. Returned type IDs can scope `get_elements` or supply an appropriate later creation/type-change operation; availability alone does not establish compatibility with a target element.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, core tier: active by default.
- **Writes model:** no. **Effects:** none. **Requires an open document:** yes.
- **Works in:** project and family documents.
- **Contract hash:** `1eb28b6511ebb61e`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `category` | string | no |  |  |
| `of_class` | string | no |  |  |
| `name_filter` | string | no |  |  |
| `include_instance_count` | boolean | no |  |  |
| `offset` | integer | no |  |  |
| `limit` | integer | no |  |  |
| `expected_document_id` | string | no |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Creating or duplicating types | Revit API: ElementType.Duplicate. Check all its members in one call: `search_api_docs` with query `ElementType.Duplicate`, then use `execute_csharp` within the requested scope. |
| Loading families | Revit API: Document.LoadFamily. Check all its members in one call: `search_api_docs` with query `Document.LoadFamily`, then use `execute_csharp` within the requested scope. |
| The types of the family being edited in a family document | Revit API: FamilyManager.Types; FamilyManager.NewType; FamilyManager.Set; FamilyManager.AddParameter; FamilyManager.SetFormula. Check all its members in one call: `search_api_docs` with query `FamilyManager.Types; FamilyManager.NewType; FamilyManager.Set; FamilyManager.AddParameter; FamilyManager.SetFormula`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Public inputs

All are optional:

- `category`: localized category or built-in category name.
- `of_class`: element type class such as `WallType`, `FamilySymbol`, or `ViewFamilyType`.
- `name_filter`: case-insensitive substring of the type name or family name.
- `include_instance_count`: default false. Adds a pass through the scoped category, or the whole host model without a category.
- `offset`: default 0; `limit`: 1–1000, default 200.
- `expected_document_id`: exact optional read guard. `_operation_id`: optional identical-retry ID; omit on new calls.

## Example

```json
{ "category": "OST_Doors", "include_instance_count": true, "limit": 100 }
```

For view creation, use `of_class: "ViewFamilyType"`; for titleblocks use `category: "OST_TitleBlocks"`. Choose from actual returned IDs, not sample or remembered numbers.

## Results and verification

Read `types`, `total_count`, `returned_count`, `has_more`, and `next_offset`. Pages sort by family then type name. Rows contain `id`, `name`, `familyName`, `category`, and `isFamilySymbol`; `instanceCount` appears only when requested.

A zero instance count means no placed host instances counted for that type, not that the type is unloaded or safe to delete. Counts exclude linked contents. Verify family/category and inspect the type with `get_element_details` when a subsequent task depends on parameter values or compatibility. Type-name equality alone does not prove two types are interchangeable.

Follow query pagination and, independently, any saved-response continuation described in [execution rules](../execution-rules.md). There are no coordinate or unit inputs.

## Effects and recovery

Read-only; no type activation, creation, deletion, save, or selection change. Resolve invalid category/class errors using the installed API or current model information. Handle stale identity or bridge uncertainty through [operation recovery](../operation-recovery.md).

## Compatibility

Source reference: PI-Revit 0.5.0, [GetElementTypes.cs](../../../../src/Revit/Tools/GetElementTypes.cs), registry identity guard, and extension retry argument. Supported bridge targets: Revit 2025–2027. No new live validation is claimed.
