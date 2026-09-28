# manage_revit_instances

## Purpose and preconditions

List reachable local Revit sessions or select the intended session for this Pi extension instance. No open document is required. Selection chooses a bridge process; it does not activate a document inside it.

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** no. **Effects:** session. **Requires an open document:** no.
- **Verify the outcome:** `reread`: query the changed state again with a read tool.
- **Contract hash:** `68347717d8c667e6`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| `action` | string | no |  | `list`, `select` |
| `bridge_id` | string | no |  |  |

| Not covered by this tool | Use instead |
| --- | --- |
| Activating a document inside a Revit session | User action: Open or activate the document in Revit |
<!-- generated:contract:end -->

## Public inputs and examples

| Field | Contract |
| --- | --- |
| `action` | Optional `list` (default) or `select`. |
| `bridge_id` | Required for `select`; exact opaque 32-character lowercase hexadecimal identity returned by `list`. |

List available sessions:

```json
{ "action": "list" }
```

Select one, replacing the illustrative identity with the actual returned value:

```json
{
  "action": "select",
  "bridge_id": "00000000000000000000000000000001"
}
```

No document guard or operation-retry field applies to this utility.

## Results and effects

Listing returns `instances` with `bridge_id`, `pid`, `revit_version`, `addin_version`, `selected`, and `supports_operation_tracking`. Selection returns the selected identity/process/add-in details, instructions, and `tool_catalog_ready`. It refreshes tool discovery for the newly selected session. A false readiness value means selection can have succeeded while discovery is pending; retry [ping](ping.md), then inspect available tools.

Before initial binding, a sole reachable session binds automatically; multiple sessions require explicit selection. Once bound, the extension never silently falls back, even if only one different session remains. After closing/restarting the selected session, list again and explicitly select the intended new identity. Read `get_model_overview` afterward for a fresh exact document identity before edits.

Current bridges publish separate discovery files in `%APPDATA%\RevitBridge\instances\<bridgeId>.json`; the legacy `bridge.json` is also read. Older bridges without generation IDs receive opaque hash selectors. Copy returned selectors unchanged. One legacy file cannot independently advertise several older bridges; current add-ins are needed in those sessions for independent discovery.

## Failures, recovery, and verification

An invalid or unreachable `bridge_id` is rejected and selection stays unchanged. No automatic redirect to another model occurs. Verify selected status, `tool_catalog_ready`, and the intended model overview, not just a process title. Operation receipt reads and identical retries route to the original bridge encoded in their operation ID regardless of this selection. Selecting another session cannot recover a lost receipt or establish what happened in the original model.

## Compatibility

This is a Pi-native session utility, implemented in `extensions/pi-revit/index.ts` and `extensions/pi-revit/instance-router.ts`. Availability/operation tracking depends on the reachable add-in. Tool activation from the previous session should not be mistaken for current bridge support; inspect refreshed discovery. Session selection changes extension routing only and does not modify or save a Revit model.
