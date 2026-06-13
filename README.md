# pi-revit

Native Revit tools for [Pi](https://pi.dev) — ask about, query, script, and modify the open
Autodesk Revit model from your terminal.

```text
You: how many levels in the model?
Pi:  calls get_model_overview → "There are 14 levels in the Revit model."

You: select all structural columns
Pi:  get_elements → search_api_docs → execute_csharp → "Selected 222 structural columns."

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

The extension discovers its tools from the bridge at startup (`/reload` re-discovers), so the
tool list always matches what the add-in serves. Everything between Pi and Revit is
local-machine only; note that Pi sends conversation context and tool results to your selected
LLM provider, like any Pi session.

## Requirements

- Windows 10/11
- Autodesk Revit 2025, 2026, or 2027
- .NET SDK matching your Revit: [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0) for Revit 2025/2026, [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) for Revit 2027
- [Node.js 20.3+](https://nodejs.org/)
- [Pi coding agent](https://pi.dev): `npm install -g --ignore-scripts @earendil-works/pi-coding-agent`

## Install

Close Revit, then in PowerShell:

```powershell
git clone https://github.com/Triavision-ai/pi-revit.git
cd pi-revit

# 1. Build + deploy the Revit add-in (RevitBridge.dll + the Roslyn DLLs for execute_csharp)
#    Defaults to Revit 2025. For another version, pass -RevitVersion and -RevitApiPath, e.g.:
#      ... scripts\deploy.ps1 -RevitVersion 2027 -RevitApiPath "C:\Program Files\Autodesk\Revit 2027"
powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1

# 2. Install the Pi package
pi install ./

# 3. Workspace + global pi-revit command
powershell -ExecutionPolicy Bypass -File scripts\setup-workspace.ps1
```

Start Revit (click **Always Load** on the unsigned add-in prompt once) and open any
project. No panel or ribbon appears — the add-in is headless.

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

**How this works:** install step 3 placed a small `pi-revit` command in the same folder as the
`pi` command itself. That folder is on your system PATH — which is exactly why *every* terminal
finds `pi-revit`, with no extra configuration. When you run it, it switches to your workspace at
`Documents\pi-revit` and starts Pi there, so your conventions file (`AGENTS.md`) loads
automatically and all Revit session history lives in one predictable place. The Revit tools
themselves are installed globally in Pi, and the extension discovers them live from the bridge
inside Revit each time a session starts.

**Per project:** create one subfolder per Revit project under `Documents\pi-revit\Projects\`:

```powershell
pi-revit tower-b        # Pi scoped to that project — its notes and its session history
pi-revit tower-b -c     # continue that project's last session
```

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
| `export_documents` | PDF/DWG/PNG/IFC export of sheets and views |
| `get_model_health` | Warnings grouped + worksets, phases, design options audit |

## Limitations — read before using on real projects

- **Write tools are unrestricted by design.** `set_parameters` and `execute_csharp` modify the
  open model directly — there is no confirmation prompt and no sandbox. Every write runs in one
  named transaction (rolled back on error, undoable with Ctrl+Z in Revit), but the model is
  yours to protect: test on copies, keep backups, read the result's `failed` lists.
- The add-in multi-targets .NET 8 (Revit 2025/2026) and .NET 10 (Revit 2027); `deploy.ps1`
  builds and deploys the framework matching `-RevitVersion`. Verified on Revit 2025 and 2027.
- **One Revit instance at a time** is discoverable (last started wins).
- A tool call that outlives its timeout is abandoned client-side but may still complete inside
  Revit — verify model state before re-issuing a write.
- Long-running scripts cannot be interrupted mid-execution (Revit's API is single-threaded);
  the `execute_csharp` budget is 120s.

## Uninstall

Close Revit, then in PowerShell from the repo:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
```

This removes the Revit bridge add-in (every installed Revit version), the global `pi-revit`
command, the bridge runtime folder (`%APPDATA%\RevitBridge\`), and the Pi package registration.
Your workspace at `Documents\pi-revit` (notes + session history) is **preserved** — add
`-RemoveWorkspace` to delete it too, or `-RevitVersion 2026` to target a single Revit version.
Pi itself is left installed; remove it with `npm uninstall -g @earendil-works/pi-coding-agent` if
you want.
