# pi-revit

Native Revit tools for [Pi](https://pi.dev) — ask about, query, script, and modify the open
Autodesk Revit model from your terminal.

```text
You: how many levels in the model?
Pi:  calls get_model_overview → "There are 14 levels in the Revit model."

You: select all structural columns
Pi:  get_elements → manage_selection → "Selected 222 structural columns."

You: rename level 'L1' to 'Ground Floor'
Pi:  calls set_parameters → "Done — Level 'L1' is now 'Ground Floor'."
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
Revit API                      ← reads run directly; writes run in one named transaction
```

The extension discovers its tools from the bridge at startup (retrying in the background until
Revit is up), so the tool list always matches what the add-in serves. Everything between Pi and
Revit is local-machine only; note that Pi sends conversation context and tool results to your
selected LLM provider, like any Pi session.

## Safety model

Be deliberate about pointing an LLM at a real project model. The add-in enforces what it can
enforce mechanically, and is honest about what it cannot:

- Every write tool is flagged `write: true` — that flag is the machine-readable signal a client
  can gate on. Whether a write needs human confirmation is a **client-side decision**: the
  add-in cannot know your policy, so confirmation UX belongs in the Pi client/agent layer, not
  here.
- All writes run in one named transaction: committed on success, rolled back on failure, always
  visible in Revit's undo history. Commit-time warnings are reported back (`commitWarnings`);
  error-severity failures roll back with Revit's failure text.
- `execute_csharp` is an unrestricted escape hatch by design — scripts have full CLR access.
  Treat it like giving the agent a macro editor, on a model you have saved or can restore.
- Blocking popups are auto-answered so Revit can never hang behind a dialog; unrecognized
  dialogs get the dismissive answer (Cancel/Close/No), never a blind OK.
- Writes accept an optional `expected_document` check so a queued write cannot silently land in
  a different model than intended.

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
`Documents\pi-revit\Models\<model title>\exports` — the add-in derives the folder from the
document being exported, so even a session that touches many models files every output under
the right one, with no naming decision from you or the AI. Each model folder carries a
`model.txt` recording the model's GUID and file path, so two models that share a title stay
distinguishable.

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
| `set_parameters` | Bulk parameter writes + rename anything (one transaction per batch) |
| `search_api_docs` | Search the offline Revit API docs (works with no document open) |
| `execute_csharp` | Run a C# script in one auto-managed transaction — the escape hatch |
| `capture_view` | PNG snapshot of a view to a temp file (read the returned path to see it) |
| `export_documents` | PDF/DWG/PNG/IFC export of sheets and views — auto-sorted into `Models\<model>\exports` |
| `get_model_health` | Warnings grouped + worksets, phases, design options audit |

## Limitations — read before using on real projects

- **Write tools are unrestricted by design.** `set_parameters` and `execute_csharp` modify the
  open model directly — there is no confirmation prompt and no sandbox. Writes run in named
  transactions, undoable with Ctrl+Z in Revit (`execute_csharp` rolls back entirely on any
  error; `set_parameters` commits partial successes and reports each failure), but the model is
  yours to protect: test on copies, keep backups, read the result's `failed` lists.
- The add-in multi-targets .NET 8 (Revit 2025/2026) and .NET 10 (Revit 2027); `deploy.ps1`
  auto-detects the Revit versions you have installed and builds only the matching framework(s),
  so you only need the SDK for the Revit you run. Verified on Revit 2025 and 2027.
- **One Revit instance at a time** is discoverable (last started wins).
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
