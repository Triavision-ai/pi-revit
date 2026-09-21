# pi-revit

Native Revit tools for [Pi](https://pi.dev) — ask about, query, script, and modify the open
Autodesk Revit model from your terminal.

```text
You: how many levels in the model?
Pi:  calls get_model_overview → "There are 14 levels in the Revit model."

You: select all structural columns
Pi:  get_model_overview → get_elements → manage_selection → "Selected 222 structural columns."

You: rename level 'L1' to 'Ground Floor'
Pi:  get_model_overview → set_parameters → "Done — Level 'L1' is now 'Ground Floor'."
```

## How it works

```text
Pi terminal session
   │  native tools (registered by the pi-revit extension)
   ▼
localhost HTTP bridge          ← per-start token; connection info in %APPDATA%\RevitBridge\
   │
   ▼
headless Revit add-in          ← no ribbon, no panels; just a bridge
   │  ExternalEvent queue (Revit API thread)
   ▼
Revit API                      ← tool-owned model transactions; separate UI/file effects
```

The extension discovers its tools from the bridge at startup (retrying in the background until
Revit is up), so the tool list always matches what the add-in serves. Everything between Pi and
Revit is local-machine only; note that Pi sends conversation context and tool results to your
selected LLM provider, like any Pi session.

## Safety model

Be deliberate about pointing an LLM at a real project model. The add-in enforces what it can
enforce mechanically, and is honest about what it cannot:

- Tool metadata describes its classification; UI actions such as selection and view
  activation can change state even when `write` is false. Confirmation policy belongs
  to the client. Exact document targeting is enforced separately as described below.
- Parameter writes, C# scripts, temporary isolation, and IFC export own named Revit
  transactions. Failure handling is attached after transaction start, and results
  check transaction outcomes before claiming commit or rollback. `set_parameters`
  defaults to partial success; `atomic: true` rolls back the batch if any update
  fails, and `preview: true` rolls back proposed model changes after validation.
  Inspect every failed update, the validation status, and `commitWarnings`.
  An unconfirmed rollback is reported as such.
- `execute_csharp` is an unrestricted escape hatch by design — scripts have full CLR access.
  Treat it like giving the agent a macro editor, on a model you have saved or can restore.
- `execute_csharp` has a dialog guard that attempts dismissive responses to dialogs
  raised while the script runs. It does not establish that every Revit dialog or
  failure mode can be handled automatically.
- A model transaction does not undo filesystem output or earlier selection/zoom
  changes. A failed export can leave incomplete files; its error reports the output
  location and observed changed files. An isolation failure reports any earlier
  selection action that already completed.

### Target the exact open document

Call `get_model_overview` for the intended model and copy `project.documentId`
unchanged into `expected_document_id` on subsequent operations:

```json
{
  "expected_document_id": "<project.documentId from the current overview>",
  "updates": [{ "element_id": 12345, "parameter": "ALL_MODEL_INSTANCE_COMMENTS", "value": "Reviewed" }]
}
```

Replace the placeholders with the current document ID and an actual element ID.
The exact ID is required for `set_parameters`, `transform_elements`,
`delete_elements`, `change_element_types`, `manage_views`, `manage_sheets`,
`manage_sheet_placements`, `manage_schedules`, `create_tags`, `execute_csharp`,
`export_documents`, and `open_view`,
including supported previews. `manage_sheet_placements` requires it even for its
read-only `list` action because the tool is classified as write-capable.
It is also required for selection/zoom
changes and any `manage_selection` call with `isolate_in_view: true`, including
action `get`. Pure reads may omit it; a supplied ID is always checked.

The identity represents one currently open native document in one loaded bridge
session. It is not a persistent project ID, path, export-folder key, or credential.
Closing/reopening the document or restarting the bridge invalidates prior IDs.
Read the intended document's overview again after those transitions. The guard
checks the actual target on Revit's API thread immediately before execution.

Legacy `expected_document` titles remain an optional additional sanity check.
A title alone no longer satisfies the required guard, even when it matches.
Clients must refresh discovery and supply the new field after upgrading to 0.3.0.

Practical advice: work on saved models, keep worksharing backups/central protection as usual,
and review the agent's summary of what changed after any write session.

## Requirements

- Windows 10/11
- Autodesk Revit 2025, 2026, or 2027
- .NET SDK matching your Revit: [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) for Revit 2025/2026, [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) for Revit 2027
- [Node.js 20.3+](https://nodejs.org/)
- [Pi coding agent](https://pi.dev): `npm install -g --ignore-scripts @earendil-works/pi-coding-agent`

## Install

### One-command install

Close Revit, then in PowerShell:

```powershell
npx.cmd -y pi-revit
```

This installs the Pi package, builds and deploys the Revit bridge add-in, creates the
`Documents\pi-revit` workspace, and installs the global `pi-revit` command.

The installer first checks the selected .NET SDK. If it is missing or too old,
installation stops with the required version, a download link, and retry steps.
Interactive terminals also offer to open the download page. Revit uses a runtime
to run; compiling this add-in also needs the SDK. For Revit 2027, install the
[.NET 10 SDK for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0),
reopen PowerShell, and rerun the installer. Existing .NET versions can stay installed.
If an older SDK is still selected, check `dotnet --list-sdks`, your `PATH`, and any
`global.json` in the current directory or its parents.

Start Revit (click **Always Load** on the unsigned add-in prompt once) and open any
project. No panel or ribbon appears — the add-in is headless.

### Manual npm install

Use this if you prefer to run each step yourself:

```powershell
# 1. Install the Pi package from npm. This registers the pi-revit extension and skill.
pi install npm:pi-revit

# 2. Go to the installed package folder.
cd "$env:USERPROFILE\.pi\agent\npm\node_modules\pi-revit"

# 3. Build + deploy the Revit add-in (RevitBridge.dll + the Roslyn DLLs for execute_csharp).
npm.cmd run deploy

# 4. Create the workspace and global pi-revit command.
npm.cmd run setup
```

For a non-default Revit install location, use the PowerShell deploy script directly and pass
`-RevitVersion` / `-RevitApiPath`, e.g.:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -RevitVersion 2027 -RevitApiPath "D:\Autodesk\Revit 2027"
```

### Source install

Use this if you want to run directly from the GitHub checkout instead of the npm package:

```powershell
git clone https://github.com/Triavision-ai/pi-revit.git
cd pi-revit
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
pi install ./
powershell -ExecutionPolicy Bypass -File scripts\setup-workspace.ps1
```

### Upgrading to 0.3.0

**Breaking change:** writes and UI mutations now require `expected_document_id`.
Close Revit, update the Pi package and redeploy the add-in using the installation
steps above, then restart Revit and start a fresh Pi session. Both components must
be updated. Call `get_model_overview` and copy `project.documentId` into subsequent
mutating calls; a legacy `expected_document` title alone is insufficient. Refresh
the ID after closing/reopening a document or restarting Revit.

## Use it

Open **any terminal** — PowerShell, CMD, or Windows Terminal — and type:

```powershell
pi-revit
```

That's all. Pi starts with the Revit tools ready:

```text
> give me a model overview
> how many doors per level?
> select all structural columns
```

**How this works:** the setup step placed a small `pi-revit` command in the same folder as the
`pi` command itself. That folder is on your system PATH — which is exactly why *every* terminal
finds `pi-revit`, with no extra configuration. When you run it, it switches to your workspace at
`Documents\pi-revit` and starts Pi there, so your conventions file (`AGENTS.md`) loads
automatically and all Revit session history lives in one predictable place (`pi-revit -c`
continues the last session). The Revit tools themselves are installed globally in Pi, and the
extension discovers them live from the bridge inside Revit each time a session starts.

**Per model, automatically:** files sort themselves. Exports land in
`Documents\pi-revit\Models\<model title>--<identity hash>\exports`. The suffix derives
from the normalized saved-file path, cloud region/project/model identity, or Revit
Server path. Distinct saved paths therefore use different destinations even when
their titles or inherited project IDs match. Save As to another path selects a new
destination. Unsaved models or unavailable persistent identities use a token stable
only for that open document; their destination may change after reopening.

`model.txt` records the identity used. Existing title-only directories remain
untouched; upgrading does not migrate or merge old exports. An explicit
`output_dir` still overrides the default. File attribution uses a directory
snapshot, so avoid unrelated concurrent writers in a shared output directory.

Plain `pi` from any folder also works; `pi-revit` just adds the right working folder on top.

## Tools

| Tool | What it does |
|---|---|
| `find_revit_tools` | Search the native tool catalogue and activate specialist tools for the current Pi session. |
| `ping` | Is the bridge reachable? Revit version |
| `manage_revit_instances` | List reachable local Revit sessions or select the target for this Pi session |
| `get_model_overview` | Project info, units, levels, grids, category counts — call first |
| `get_model_coordinates` | Read base points and site/project locations; map internal points through the active shared coordinates |
| `get_mep_connections` | Inspect host MEP connectors, connection status, system identity, and physical/logical references |
| `get_elements` | Query/count elements: parameter filters, optional parameter values, pagination |
| `summarize_elements` | Count all matching elements by category, type, level, or raw parameter value |
| `manage_element_sets` | Create, list, read, or forget temporary snapshots of matching host elements |
| `get_element_details` | Parameter values, location, bounding box, materials per element |
| `get_element_types` | Element types / family symbols, optional placed-instance counts |
| `get_linked_models` | Direct Revit link instances, load status, document identities, placement transforms |
| `get_linked_elements` | Query/count one loaded link, with optional bounding boxes in host coordinates |
| `query_spatial_elements` | Find host elements by approximate bounding-box intersection or containment in a region |
| `measure_geometry` | Measure exact point distance or approximate host-element bounding-box separation |
| `get_schedules` | List schedules or read fields, widths, specifications, sort/filter rules, and displayed cells |
| `get_schedule_fields` | Discover eligible field parameter/type pairs for an existing schedule |
| `manage_schedules` | Create or configure regular schedules, including fields, widths, sorting, and filters |
| `create_tags` | Create host element, room, space, or area tags, with partial/atomic batches and preview |
| `get_element_relationships` | Host, type, level, members, joined geometry, and logical dependents of one element |
| `manage_selection` | Get/set/clear the selection, zoom, temporary isolate |
| `open_view` | Activate a view or sheet in the Revit UI (like double-clicking it in the browser) |
| `set_parameters` | Bulk parameter writes and renames, with previews and optional atomic batches |
| `transform_elements` | Move, copy, or rotate a whole selection, with explicit units and preview |
| `delete_elements` | Preview or delete selected elements and report Revit's full deletion set |
| `change_element_types` | Change types per target, with preview and optional atomic rollback |
| `manage_views` | Create plans, isometric 3D views, or sections; duplicate or update views |
| `manage_sheets` | Create, rename, or renumber sheets, with an optional titleblock at creation |
| `manage_sheet_placements` | List, place, or move viewports and schedule instances in sheet paper space |
| `search_api_docs` | Search the offline Revit API docs (works with no document open) |
| `execute_csharp` | Run a C# script with separate JSON inputs in one auto-managed transaction |
| `manage_revit_scripts` | Save immutable local script versions, inspect source, run an exact version, and read history |
| `capture_view` | PNG snapshot of a view to a temp file (read the returned path to see it) |
| `export_documents` | PDF/DWG/PNG/IFC export of sheets and views — sorted into `Models\<title>--<identity hash>\exports` |
| `get_model_health` | Warnings grouped + worksets, phases, design options audit |
| `read_revit_result` | Read bounded fragments of a saved large tool result; extension-only, no Revit call |
| `get_revit_operation` | Inspect a bridge operation receipt and its retained result without waiting for the model thread |

### Choose a Revit instance

`manage_revit_instances` defaults to `action: "list"`. It reports reachable
local bridge sessions with an opaque `bridge_id`, process ID, Revit/add-in
versions, selection status, and operation-tracking support. Choose the intended
session with `action: "select"` and the exact returned `bridge_id`. Selection
belongs to the current Pi extension session; it does not activate a document
or change the model.

When the first connection finds a sole instance, Pi binds to it automatically.
If several instances are available before a target is bound, explicitly select
one before model calls. Once bound, Pi keeps that exact bridge session. If it
closes or restarts, calls fail instead of switching to another instance, even
when only one remains. List again and explicitly select the intended new
identity. Selection refreshes the tool catalogue; check `tool_catalog_ready`,
and retry `ping` if discovery has not completed. Then read `get_model_overview`
for a fresh document identity before continuing work.

Each current bridge publishes its own discovery file in
`%APPDATA%\RevitBridge\instances\<bridgeId>.json`. The legacy `bridge.json`
discovery file is still supported. Older bridges without a generation ID receive
an opaque hash selector in the instance list; copy it unchanged rather than
constructing one. Legacy discovery can expose only the older instance named by
that shared file, so updating each add-in enables discovery of all instances.

Operation receipts and identical `_operation_id` retries route to the original
bridge encoded in the operation ID, even after selecting a different instance.
They do not switch the target for new calls. If the original bridge is unavailable,
the request fails without sending the action to another session.

### Read project coordinates and MEP connections

`get_model_coordinates` reads project/survey base points, the active project
location, site data, and paginated project locations. Supply a length `unit`;
optional `points` accepts up to 100 positions along document internal axes.
Revit's active project location maps those points to shared coordinates. Returned
lengths use the requested unit, while angles and latitude/longitude use degrees.
Location pages default to 50 entries (maximum 100); follow `next_offset`.
The tool requires a project document, makes no coordinate changes, and does not
infer a GIS coordinate reference system or datum.

`get_mep_connections` reads one supported host MEP curve or family instance with
an explicit `element_id` and length `unit`. Connector origins use internal axes;
positions and sizes use the requested unit, while normals are unit vectors.
`physically_connected` reports `IsConnected`; connector `AllRefs` can include
physical and logical references, so reference count is not a physical connection
count. Unsupported property reads are null with reasons in `unavailable`, not
false or zero substitutes. A null system without an unavailable reason can mean
the connector has no assigned system.

Connector and reference pages are independent: use `offset`/`limit` for connectors
and `reference_offset`/`reference_limit` for each connector's references, each
defaulting to 50 and capped at 100. Finish the needed reference pages before
advancing the connector page. The tool inspects one element without traversing
the connected network or linked models.

### Query regions and measure distances

`query_spatial_elements` takes a region `min`/`max` in the document's internal
axes and an explicit length `unit`. It compares host-element axis-aligned
bounding boxes with the region, using `intersects` (default) or `inside`.
Containment requires the whole box to fit; touching boundaries are included.
The nested `query` uses whole-scope element filters and must match at most
10,000 candidates before region testing. Query-level paging and projections
are not accepted. Results sort by element ID with outer paging (default 100,
maximum 200); follow `next_offset`. `without_bounds_count` reports candidates
omitted because they have no model bounding box.

`measure_geometry` measures exact Euclidean distance between supplied points
with `mode: "point_distance"`, or approximate bounding-box separation between
two host elements with `mode: "bounding_box_gap"`. Point mode returns signed
A-to-B `delta`; box mode returns nonnegative axis gaps. The required `unit`
applies to all inputs and outputs: millimeters, centimeters, meters, feet, or
inches. Both modes use document internal axes; linked contents are not traversed.

Bounding-box gaps are lower bounds on geometry separation. Boxes can enclose
nonphysical geometry, so touching/overlapping boxes do not establish a clash.
An optional nonnegative `clearance` in box mode flags gaps strictly below that
threshold; it identifies review candidates, not verified clearance failures.
Keep the returned method and approximation status with any reported result.

Practical workflows are available in the Pi skill references:
[room documentation](skills/pi-revit/references/room-documentation.md) and
[model audit and export](skills/pi-revit/references/model-audit-export.md).

### Read parameter values and summarize queries

Add `parameter_names` to `get_elements` to read up to 20 requested parameters
alongside element identity fields. Names can be display names, built-in parameter
names, or `guid:<GUID>` identities. `include_type_parameters: true` also reads
those parameters from each element's type. Projections expose missing and
ambiguous matches and include every matching parameter. Raw `value` uses Revit
internal units for measurable numbers; `displayValue` uses document formatting.
Bare counts return no parameter projections.

`summarize_elements` groups a host-element `query` by `category`, `typeName`,
`levelId`, or `parameter`. Parameter grouping uses exact raw values, with a
separate missing-parameter group, and rejects ambiguous display names. Set
`type_parameter: true` to group by a parameter on the type. The outer
`offset`/`limit` pages groups (default 100, maximum 500); counts always cover the
whole matching scope.

`summarize_elements` and element-set creation accept category, class, level,
type, active-view, and parameter filters inside `query`. They reject query-level
paging, projections, and `count_only`, and require at most 10,000 matching
elements. Narrow larger queries; an omitted query covers all host instances.

### Reuse an element set

Use `manage_element_sets` with `action: "create"` to snapshot query membership,
then `read` its `set_id`, `list` active-document sets, or `forget` a set. This
changes neither the model nor selection. The bridge retains at most 32 sets;
each expires 30 minutes after creation and belongs to the exact open document
and bridge session. Reads do not renew it. Reopening the document or restarting
the bridge requires a fresh set.

Read pages return current values for the original members, without reapplying
the query. Deleted or identity-changed members appear in `missing`. Check those
entries before passing surviving IDs to other tools. Pages default to 100
members (maximum 1000); follow `next_offset`, which includes missing members in
`visited_count`. Set reads also support `parameter_names` and
`include_type_parameters`.

### Preview parameter changes

`set_parameters` accepts 1–200 updates and defaults to committing successful
updates while reporting failures. Each update has its own subtransaction.
`atomic: true` rolls back the entire batch if any update fails. `preview: true`
performs eligible commit checks inside a transaction group, then rolls the
group back. Both options require the same exact document identity as an ordinary
write and can be combined.

Read `commit_validation_performed`: an atomic batch rejected before commit, or
a batch with no accepted updates, has not passed commit-time validation.
Successful preview responses confirm model rollback. Errors report the cleanup
outcome instead; inspect it before retrying.

Only committed updates appear in `succeeded`. Accepted steps from previews or
rolled-back batches appear in `proposed`, with `updated: 0` and
`committed: false`. Their `before`/`after` values describe each step in input
order, identified by its zero-based `index`. Repeated writes can have intermediate
values; reread the element when final values matter. Inspect `failed` and
`commitWarnings` as well. Numeric write inputs use the explicit `unit` or the
document's display units; raw numeric values in the returned snapshots use
Revit internal units.

### Transform, delete, and change types

These advanced tools can be activated with `find_revit_tools`. They operate on
the active host document and require `expected_document_id` for both previews
and writes. Their results use the same `succeeded`, `proposed`, `failed`,
`committed`, and commit-validation fields as parameter batches. `updated` counts
steps: one whole-selection transform or deletion counts as one step.

`transform_elements` moves, copies, or rotates 1–200 distinct `element_ids`
together. Always supply `unit`: `millimeters`, `centimeters`, `meters`, `feet`,
or `inches`. Move/copy takes a `translation` vector. Rotation takes an
`axis_origin`, a nonzero dimensionless `axis_direction`, and signed right-hand
`angle_degrees`. Coordinates refer to the document's internal origin and axes;
returned location snapshots use feet, regardless of input units. The selection
succeeds or rolls back as one step. Move/rotate rejects pinned requested
elements; the tool never unpins them automatically. Copy previews return
temporary `created_ids`, which must not be reused after rollback.

`delete_elements` deletes 1–200 distinct requested elements in one step, rejecting
pinned requested elements. Use `preview: true` to inspect Revit's complete
`deleted_ids` set and its `dependent_ids`, with rollback. A cascade exceeding
10,000 IDs rolls back. To check a later deletion against the preview, optionally
pass the full `deleted_ids` as `expected_deleted_ids`; a changed set causes
rollback before commit. This compares membership only. The actual deletion is
a new request, not an identical retry of the preview, so do not reuse the
preview's `_operation_id` for it.

`change_element_types` accepts 1–200 `element_id`/`type_id` pairs in `updates`,
with each target appearing once. Invalid types or pinned targets fail individually;
other valid changes can commit by default. `atomic: true` rolls back the complete
batch on any failure. Some type changes replace the original element: use the
committed `resulting_id` and `unique_id` afterward. Replacement IDs reported in
`proposed` are temporary after preview or atomic rollback.

Transforms and type changes can affect constrained or hosted elements beyond
the target snapshots. Deletion IDs cover elements returned by Revit's deletion
API, not surviving elements Revit may also modify. Review these results as
bounded descriptions of the operation, not a complete audit of every effect.

### Create views and arrange sheets

Activate `manage_views`, `manage_sheets`, and `manage_sheet_placements` through
`find_revit_tools`. Edits support `preview: true` and require the exact document
identity. Each call edits one view, sheet, or placement in one step. Created IDs
in previews are marked `id_is_temporary` and must not be reused after rollback.
Inspect the common transaction outcome and validation fields. Use `open_view`
to display committed views/sheets and `delete_elements` to remove them.

`manage_views` supports `create_plan`, `create_3d`, `create_section`, `duplicate`,
and `update`. Creation uses a compatible `view_family_type_id`; discover it with
`get_element_types` and `of_class: "ViewFamilyType"`. Plans also need `level_id`;
3D creation produces an isometric view. Duplicate/update targets `view_id`.
Duplication options are `duplicate`, `with_detailing`, and `dependent` where
supported. Optional name, scale, and template apply in the same step. Template
ID `-1` removes a template explicitly; incompatible templates or controlled
scale changes fail without automatically removing the template.

Sections use the document's internal coordinate frame. Supply `origin`, explicit
length `unit`, orthogonal nonzero `viewing_direction` and `up`, and positive
width/height/depth. Width and height center on the origin; depth extends in the
viewing direction. Direction vectors are dimensionless.

`manage_sheets` creates a sheet from `name` and `number`, optionally using a loaded
titleblock `FamilySymbol` specified by `titleblock_type_id`. Omitting it creates
a sheet without a titleblock. Updates use `sheet_id` and name/number; changing
an existing titleblock type uses `change_element_types` on its instance instead.

`manage_sheet_placements` lists both viewports and schedule instances for a
`sheet_id` (default 100 per page, maximum 200). Follow `next_offset`. Although
listing changes no model state, the tool's write-capable metadata means
`expected_document_id` is required for this action too.

To place content, supply `sheet_id`, `view_id`, explicit `unit`, and
`position: [x,y,0]`; to move it, supply `placement_id`, unit, and position.
Positions are paper-space sheet coordinates and must never be multiplied by
view scale. Viewports use their box center excluding the label; schedules use
their insertion point. Returned positions use feet and state `position_kind`.
Viewports support rotation `none`, `clockwise`, or `counterclockwise`; omit
rotation for schedules. Pinned placements cannot be moved, placeholder sheets
cannot receive content, and Revit checks whether a view can be placed.

### Configure schedules and create tags

`manage_schedules` creates a regular schedule from `category` and `name`, with
an optional `area_scheme_id` for area schedules. Configure an existing schedule
with `schedule_id`. Each call is one atomic step, requires `expected_document_id`,
and supports preview rollback. Revision schedules, templates, embedded schedules,
and calculated/combined-field authoring are outside this tool.

Use `get_schedule_fields` to discover eligible fields for an existing schedule.
It supports localized name filtering and paging (default 100, maximum 200).
`add_fields` identifies each field by its `parameter_id` plus `field_type` pair;
negative built-in parameter IDs are valid. When Count appears in discovery,
pass its returned pair unchanged, just like other fields. Count can also be
added as `{ "field_type": "Count" }` without a parameter ID. A supplied pair
must be eligible for the target schedule; do not guess its parameter ID.
`included` marks pairs already present.

In contrast, `update_fields`, `sort_fields`, and `filters` use the schedule-local
`field_id` returned by `get_schedules`. These IDs are neither parameter IDs nor
column indices. Read newly committed fields before assigning their sort/filter
rules. New schedule and field IDs returned by previews are temporary.

Field additions and updates support heading, hidden state, and width, with up
to 50 entries each. Widths require explicit length units and apply to both grid
and sheet columns. `sort_fields` allows four rules; `filters` allows eight.
Supplying either array replaces its entire list, including clearing it with
`[]`; omitting the property preserves it. `is_itemized` controls itemization.

Filter comparisons are `equals`, `not_equals`, `contains`, `greater_than`, or
`less_than`. Supply a matching `value_type` and value. Measured numeric filters
require an explicit compatible `unit`; unitless numbers must omit it.
`get_schedules` exposes each field's specification, filtering capabilities,
grid/sheet widths in feet, and current sort/filter rules. Returned numeric filter
values use internal units; returned comparison names use Revit enums rather
than the write schema's comparison strings.

`create_tags` accepts 1–100 host-document targets in one explicit view, using a
loaded tag `FamilySymbol`. Choose `kind` as `element`, `room`, `space`, or `area`.
Each target has `element_id` and `head_position: [x,y,z]` in document internal
coordinates, using the required length `unit`. The position always means the
tag head, including when `leader: true`; returned positions use feet. Linked
targets and face/subelement references are unsupported.

Element tags support horizontal/vertical orientation. Spatial tags require a
compatible plan view and positions at the spatial element's level; omit their
orientation property. Independent tags cannot use templates, perspective views,
or unlocked 3D views. Creation requires exact document targeting, defaults to
partial success, and supports `atomic: true` and `preview: true`. IDs in
`proposed` are temporary after preview or atomic rollback; use only committed
tag IDs for subsequent calls.

### Inspect links, schedules, and relationships

Call `get_linked_models` before querying a link. Pass its `link_instance_id` and
`linked_document_id` to `get_linked_elements` as `link_instance_id` and
`expected_linked_document_id`. The optional `expected_document_id` still identifies
the active host model. Refresh discovery after unloading or reloading a link.
Queries support `get_elements` filters and pagination, except active-view scoping.
Unloaded links cannot be queried, and nested links are not traversed.

Keep the full `reference` returned for each linked element: its numeric ID belongs
to the linked document and must not be used with host selection or write tools.
Different placements of one linked model can produce different host coordinates.
Optional `host_bounds` use internal feet and enclose all eight transformed box
corners; they are axis-aligned bounds, not exact geometry. A missing box is null.

Use `get_schedules` without an ID to list schedules, or with `schedule_id` to read
field definitions, width/specification metadata, sort/filter rules, and displayed
body cells. Text follows Revit's formatting and
can include grouped entries, headings, and totals; rows are not element IDs.
Hidden field definitions need not correspond to displayed columns. Follow both
`next_offset` and `next_column_offset`: finish the column pages for a row page
before moving to the next rows. List/body pages default to 50 entries (maximum
200); column pages default to 50 (maximum 50).

`get_element_relationships` reports each requested relationship separately, with
its own count and continuation offset. It reads host-document relationships only;
logical dependents are not a complete deletion-impact prediction. In family
documents, request relationship kinds that exclude `joined`. Relationship pages
default to 100 entries (maximum 200). Link-instance pages default to 100 (maximum
1000), and linked-element pages default to 200 (maximum 1000).

### Reuse a script with structured inputs

`execute_csharp` accepts an optional `inputs` JSON object separately from its
source code. The script reads it through the `inputs` `JsonElement` global,
for example `inputs.GetProperty("value").GetDouble()`. Inputs default to an
empty object and are not interpolated into source text. Scripts validate their
contents; the serialized object is limited to 100,000 characters.

`manage_revit_scripts` stores reusable definitions under
`%APPDATA%\pi-revit\scripts`. Its actions are `save`, `list`, `read`, `run`, and
`history`. Saving requires a name, description, code, and `input_types`; it never
executes the script. Versions are immutable 64-character SHA-256 hashes of the
saved definition. Read a version to inspect its source, then explicitly run that
exact name/hash with current `expected_document_id` and inputs. Runs verify
saved content integrity and require a bridge with operation receipts.

Up to 40 named `input_types` can be declared as string, number, integer, boolean,
object, or array. Every declared input is required and extra inputs are rejected.
Validation covers top-level kinds only: scripts still validate nested objects,
array contents, ranges, units, and model-specific rules. The Pi skill includes
a short save/read/run example using a numeric input.

Library runs use unrestricted `execute_csharp`, with the same model, UI, file,
and external effects, synchronous execution, transaction, and timeout behavior.
There is no preview mode, scheduled execution, or automatic model saving. A saved
script is not a saved Revit model.

List/history pages default to 20 entries (maximum 100), with optional exact-name
filtering. Local history stores version, document identity, input hash,
timestamps, and receipt IDs, rather than raw inputs or results. History states
record dispatch/response progress, not a model transaction outcome. After a
timeout or interruption, inspect `get_revit_operation`; an identical retry uses
the original `_operation_id`, script version, inputs, and document identity.

### Check an operation after a timeout

With a bridge that supports operation tracking, Pi automatically assigns an
operation ID to each bridge tool call and includes it in responses or request
errors. Call the native `get_revit_operation` tool with that `operation_id` to
check progress or recover the original result. This extension tool contacts the
operation's original local bridge directly, even when another instance is selected,
so it works while Revit's model thread is busy or no
document is open; the bridge must still be running.

| Receipt state | Meaning |
|---|---|
| `queued` | Waiting to begin; no tool execution has started. |
| `running` | Execution started; a client timeout or cancellation does not stop it. |
| `succeeded` | The tool returned successfully; inspect its payload for the actual model outcome. |
| `failed` | The call returned an error; this alone does not confirm rollback or absence of effects. |
| `expired_before_start` | Its queue deadline passed before execution; no tool action was performed. |
| `result_unavailable` | The tool finished, but a response could not be retained correctly; effects may already exist. |
| `unknown` | This bridge session has no receipt; the outcome is unknown. |

A successful receipt is not proof of a committed edit. For example, a completed
parameter preview can have receipt state `succeeded` while its payload reports
`preview: true`, `committed: false`, and proposed changes that were rolled back.
Inspect `committed`, validation flags, failures, and warnings in the retained
tool result.

To retry the same request, pass its exact ID as `_operation_id` on the original
tool call and preserve all original arguments. Within the same bridge session,
this waits for the existing operation or returns its cached response; it never
executes that ID a second time. Reusing the ID with a different tool or arguments
is rejected. Omitting `_operation_id` creates a new operation, so establish the
previous outcome before retrying an edit that way.

The bridge retains at most 128 completed full results, with a combined 32 MiB
limit. Older or oversized results can be evicted, but their receipt records
remain reserved for the session and report `result_available: false`. Replaying
an evicted result returns an error without repeating the action. At 10,000
receipt records, new tracked calls are rejected; existing receipts remain
queryable. A bridge restart clears all receipts and changes its identity. Old
IDs cannot be replayed in the new session. An unavailable original bridge or an
`unknown` receipt does not prove that an earlier edit never ran. Changing the
selected instance does not redirect old receipts or retries. Inspect the original
model before deciding on a new operation.

### Read complete results

Requested rows and parameter values are included in model-visible tool content.
Results up to 12,000 characters are complete inline. For a larger result, the Pi
extension saves the complete tool payload as UTF-8 JSON and returns `result_id`,
`file_path`, `total_chars`, `complete_inline: false`, and retrieval instructions.

Call `read_revit_result` with the returned ID and `offset: 0`, then follow each
`next_offset` until `has_more` is false. A requested fragment is at most 8,000
UTF-16 code units; it may be smaller so the escaped response stays within the
message limit. Concatenate each page's `text` in order. Individual fragments are
not standalone JSON objects from the original result. Use the returned offsets,
not byte counts or a guessed increment.

Result IDs are registered in memory by the current extension instance. After an
extension reload or new Pi process, an old ID may no longer resolve; use the
original absolute `file_path` with Pi's normal `read` tool while that file remains
available. Saved results live in a unique OS temporary directory, can contain
model data, and are subject to eventual OS/user cleanup. If saving fails after
Revit completed an operation, inspect actual model state before retrying a write.

Complete payload retrieval does not expand a tool's own query page or declared
limits. Continue `get_elements`/`get_element_types` pagination separately, and
check warning-group or projection truncation indicators. A bridge-only client
must consume `details.payload` for oversized results; `read_revit_result` belongs
to the Pi extension.

Display-name parameter filters now resolve on every element, including inside a
category/class scope. Explicit built-in IDs and shared GUIDs can retain collector
optimization. In `get_element_details`, `include.parameters` and
`include.type_parameters` are independent; disabling instance parameters still
allows a type-only result.

## Limitations — read before using on real projects

- **Writes change the open model directly.** There is no confirmation prompt.
  Typed editing tools provide preview and transaction outcomes; `execute_csharp`
  remains unrestricted and attempts rollback after script failure. Parameter
  and type-change batches default to partial success, with optional atomic rollback.
  Transforms and deletions apply the whole selection in one step. Read the
  actual transaction outcome and failed lists. Script result-projection failure
  can leave a successful edit committed with a `returnValueError`; filesystem and
  UI effects are separate from model rollback.
- The add-in multi-targets .NET 8 (Revit 2025/2026) and .NET 10 (Revit 2027); `deploy.ps1`
  auto-detects the Revit versions you have installed and builds only the matching framework(s),
  so you only need the SDK for the Revit you run. The 0.3.0 changes were tested live
  on Revit 2025.4.3 (build 25.4.30.30, German UI). Revit 2026/2027 and large-model
  performance were not tested for this release. Export API/file checks do not
  establish full DWG drawing or IFC schema/geometry validation.
- Pi targets one bridge session at a time. Use `manage_revit_instances` to select
  among reachable instances. Closing or restarting the selected bridge requires
  explicit selection of an available identity; model calls never fall back to another.
- A tool call that outlives its timeout is abandoned client-side but may still complete inside
  Revit. Check its receipt with `get_revit_operation`; when retrying the identical request,
  reuse `_operation_id`. If its outcome remains unknown, inspect the model before a new write.
- Long-running scripts cannot be interrupted mid-execution (Revit's API is single-threaded);
  the `execute_csharp` budget is 120s.

## Uninstall

### npm install

Close Revit, then in PowerShell — from any folder **outside** the installed package (Windows
cannot delete a folder your shell is standing in):

```powershell
powershell -ExecutionPolicy Bypass -File "$env:USERPROFILE\.pi\agent\npm\node_modules\pi-revit\scripts\uninstall.ps1"
```

### Source install

Close Revit, then in PowerShell from the GitHub checkout:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

This removes the Revit bridge add-in (every installed Revit version), the global `pi-revit`
command, the bridge runtime folder (`%APPDATA%\RevitBridge\`), and the Pi package registration
(it tries both the npm and the source-install form, so no extra `pi remove` is needed for
either install kind). Your workspace at `Documents\pi-revit` (notes + session
history) is **preserved** — add `-RemoveWorkspace` to delete it too, or `-RevitVersion 2026` to
target a single Revit version. Pi itself is left installed; remove it with
`npm uninstall -g @earendil-works/pi-coding-agent` if you want.
