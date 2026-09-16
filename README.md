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
  can commit a partially successful batch; inspect every failed update and
  `commitWarnings`. An unconfirmed rollback is reported as such.
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
The exact ID is required for `set_parameters`, `execute_csharp`,
`export_documents`, and `open_view`. It is also required for selection/zoom
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
| `ping` | Is the bridge reachable? Revit version |
| `get_model_overview` | Project info, units, levels, grids, category counts — call first |
| `get_elements` | Query/count elements of any category: parameter filters, pagination |
| `get_element_details` | Parameter values, location, bounding box, materials per element |
| `get_element_types` | Element types / family symbols, optional placed-instance counts |
| `manage_selection` | Get/set/clear the selection, zoom, temporary isolate |
| `open_view` | Activate a view or sheet in the Revit UI (like double-clicking it in the browser) |
| `set_parameters` | Bulk parameter writes + rename anything (one transaction per batch) |
| `search_api_docs` | Search the offline Revit API docs (works with no document open) |
| `execute_csharp` | Run a C# script in one auto-managed transaction — the escape hatch |
| `capture_view` | PNG snapshot of a view to a temp file (read the returned path to see it) |
| `export_documents` | PDF/DWG/PNG/IFC export of sheets and views — sorted into `Models\<title>--<identity hash>\exports` |
| `get_model_health` | Warnings grouped + worksets, phases, design options audit |
| `read_revit_result` | Read bounded fragments of a saved large tool result; extension-only, no Revit call |

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

- **Write tools are unrestricted by design.** `set_parameters` and `execute_csharp` modify the
  open model directly — there is no confirmation prompt and no sandbox. Writes run in named
  transactions (`execute_csharp` attempts rollback after script failure;
  `set_parameters` commits partial successes and reports each failure). Read the
  actual transaction outcome and failed lists. Script result-projection failure
  can leave a successful edit committed with a `returnValueError`; filesystem and
  UI effects are separate from model rollback.
- The add-in multi-targets .NET 8 (Revit 2025/2026) and .NET 10 (Revit 2027); `deploy.ps1`
  auto-detects the Revit versions you have installed and builds only the matching framework(s),
  so you only need the SDK for the Revit you run. The 0.3.0 changes were tested live
  on Revit 2025.4.3 (build 25.4.30.30, German UI). Revit 2026/2027 and large-model
  performance were not tested for this release. Export API/file checks do not
  establish full DWG drawing or IFC schema/geometry validation.
- **One Revit instance at a time** is discoverable (last started wins). When that instance
  closes or crashes, another one that is still running takes the slot over within 30s.
- A tool call that outlives its timeout is abandoned client-side but may still complete inside
  Revit — verify model state before re-issuing a write.
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
