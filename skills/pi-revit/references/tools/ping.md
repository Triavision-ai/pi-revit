# ping

## Purpose and preconditions

Check whether the selected local Revit bridge is reachable and which Revit/add-in version is loaded. No open document is required. Pi registers this utility independently of bridge tool discovery, so it remains available when Revit is closed or specialist tools are missing. Multiple sessions may require [explicit selection](manage_revit_instances.md).

<!-- generated:contract:start (npm run generate:contracts; do not edit this block) -->
## Contract (generated)

- **Source:** Pi extension utility, always active.
- **Writes model:** no. **Effects:** none. **Requires an open document:** no.
- **Contract hash:** `aefaeb4355d5d822`. `find_revit_tools` compares it with the selected bridge's live contract.

| Input | Type | Required | Default | Allowed values |
| --- | --- | --- | --- | --- |
| _(none)_ | | | | |

| Not covered by this tool | Use instead |
| --- | --- |
| Whether a project document is open | Tool: get_model_overview |
<!-- generated:contract:end -->

## Public inputs and example

The public input is an empty object. This utility does not accept document-identity or retry fields.

```json
{}
```

## Results and effects

The current bridge returns `ok`, `service`, `revitVersion`, `pid`, `addinVersion`, `bridgeId`, and `supportsOperationTracking`. The extension appends a warning when installed package and loaded add-in versions differ, or a note when tool registration just succeeded or failed. Ping can trigger discovery/registration, but does not execute a model operation or activate all specialist tools. It may bind an initial sole session through normal instance routing.

## Failures and recovery

A missing bridge usually means Revit is closed, still starting, or the add-in did not load. When user action is needed, identify that requirement. A successful ping can coexist with failed tool discovery; retry ping or restart Pi if discovery remains unavailable. Do not treat ping success as proof that a document is open. Selected-session failures require listing/selecting the intended current instance rather than automatic fallback.

## Verification and compatibility

Check the returned Revit/add-in version and any warning before relying on version-specific contracts. This utility uses a 10-second request budget and has no model operation receipt. Updating the Pi package does not replace the already loaded add-in; use the package's installation/upgrade procedure for a matching deployment. Do not deploy or restart merely because an unrelated task requested inspection. Contract source: `extensions/pi-revit/index.ts` (`registerPing`); bridge response: `src/Revit/BridgeServer.cs` (`/ping`).
