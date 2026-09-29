# create_tags

## Purpose and boundaries

Create element, room, space, or area tags for host-document targets in one explicit view using a loaded tag family type. Linked targets and face/subelement references are unsupported. This tool creates tags, not tag families or target elements.

Contract: PI-Revit 0.5.0 source, [CreateTags.cs](../../../../src/Revit/Tools/CreateTags.cs). Activate this advanced tool through `find_revit_tools`. The public schema is authoritative; this page documents source behavior, not a live-model test.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** bridge tool, advanced tier: activate it with `find_revit_tools`.
- **Writes model:** yes. **Effects:** model. **Requires an open document:** yes.
- **Works in:** project documents only; the bridge refuses other documents before running, with the route to use instead.
- **Verify the outcome:** `capture`: capture the visible result and inspect the image.
- **Contract hash:** `61b4ced397d41d57`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `kind` | string | yes |  | `element`, `room`, `space`, `area` |
| `view_id` | integer | yes |  |  |
| `tag_type_id` | integer | yes |  |  |
| `unit` | string | yes |  | `millimeters`, `centimeters`, `meters`, `feet`, `inches` |
| `targets` | array of object | yes |  |  |
| `leader` | boolean | no |  |  |
| `orientation` | string | no |  | `horizontal`, `vertical` |
| `preview` | boolean | no |  |  |
| `atomic` | boolean | no |  |  |
| `expected_document_id` | string | yes |  |  |

Bridge calls also accept `_operation_id`, only to retry an identical earlier request (see [operation recovery](../operation-recovery.md)).

| Not covered by this tool | Use instead |
| --- | --- |
| Tagging elements inside linked models | Revit API: Reference.CreateLinkReference; IndependentTag.Create. Check all its members in one call: `search_api_docs` with query `Reference.CreateLinkReference; IndependentTag.Create`, then use `execute_csharp` within the requested scope. |
| Tagging faces or subelements | Revit API: IndependentTag.Create with a face Reference. Check all its members in one call: `search_api_docs` with query `IndependentTag.Create`, then use `execute_csharp` within the requested scope. |
| Creating or loading tag families | Revit API: Document.LoadFamily. Check all its members in one call: `search_api_docs` with query `Document.LoadFamily`, then use `execute_csharp` within the requested scope. |
| Text notes, dimensions and other annotation | Revit API: TextNote.Create; Creation.ItemFactoryBase.NewDimension. Check all its members in one call: `search_api_docs` with query `TextNote.Create; Creation.ItemFactoryBase.NewDimension`, then use `execute_csharp` within the requested scope. |
<!-- generated:contract:end -->

## Inputs and preconditions

Follow [execution rules](../execution-rules.md). Discover a compatible loaded `FamilySymbol`, the intended view, and host targets before requesting placement.

| Input | Meaning |
| --- | --- |
| `kind` | Required: `element`, `room`, `space`, or `area`. |
| `view_id` | Required existing view ID. Templates, perspective views, and unlocked 3D views are rejected. Spatial tags require a compatible plan view. |
| `tag_type_id` | Required loaded tag `FamilySymbol` ID; Revit validates tag compatibility. The tool activates an inactive symbol within the edit. |
| `unit` | Required length unit: `millimeters`, `centimeters`, `meters`, `feet`, or `inches`. |
| `targets` | Required array of 1–100 objects containing positive `element_id` and finite `head_position: [x,y,z]`. |
| `leader` | Optional boolean, default false. Position still means the tag head when a leader is enabled. |
| `orientation` | Element tags only: `horizontal` (default) or `vertical`. Omit entirely for room/space/area tags. |
| `preview` | Default `false`; commit-validates accepted tags then rolls back model changes. |
| `atomic` | Default false: valid target tags may commit despite others failing. True rolls back all tags if any target fails. |
| `expected_document_id` | Required for all calls, including preview; exact current overview identity. |
| `_operation_id` | Optional extension argument only for an identical retry. Omit for a new operation. |

Positions use the document's internal coordinates, not sheet coordinates. For room/space/area tags, the target must have a point location on the plan view's level; supply the head at that spatial level. The tool creates the spatial tag at the target location before applying the requested head and leader.

## Example

IDs and positions are illustrative. Replace them with an inspected compatible plan, loaded room-tag type, room, and head position on its level.

```json
{
  "kind": "room",
  "view_id": 23456,
  "tag_type_id": 34567,
  "unit": "meters",
  "targets": [{ "element_id": 12345, "head_position": [4, 6, 0] }],
  "leader": false,
  "preview": true,
  "atomic": true,
  "expected_document_id": "<project.documentId>"
}
```

## Results, recovery and verification

Inspect `committed`, `succeeded`, `proposed`, `failed`, `commitWarnings`, and `commit_validation_performed`. Per-target entries include `tag_id`, `unique_id`, actual `tag_type_id`, owner `view_id`, `kind`, actual `head_position` in feet, `leader`, and `id_is_temporary`.

Every tag in `proposed` is temporary, including atomic rollback results. Only reuse IDs from committed `succeeded` entries. Preview validates model acceptance but cannot establish final visual readability after rollback.

Read committed tags and follow [visual verification](../visual-verification.md) to inspect text, head positions, leader paths, overlaps, and clipping in the actual view. Follow [operation recovery](../operation-recovery.md) before repeating an uncertain call to avoid duplicate tags. A real call after preview is a new operation. This tool does not save the model.
