#Requires -Version 5.1
<#
Sets up the pi-revit user environment on this PC:

  1. Workspace at Documents\pi-revit (AGENTS.md conventions, Models\ output tree,
     double-click launcher). An existing AGENTS.md is never overwritten.
  2. Global `pi-revit` command installed next to the `pi` command, so any terminal can run:
       pi-revit              -> Pi in the workspace
       pi-revit -c           -> continue the last session
     Model output sorts itself: the Revit tools file each export under
     Models\<model title>\ automatically, keyed by the document it came from.

Idempotent — safe to re-run. Usage:
  scripts\setup-workspace.ps1
  scripts\setup-workspace.ps1 -WorkspaceDir "D:\work\pi-revit"
#>
param(
    [string]$WorkspaceDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'pi-revit')
)

$ErrorActionPreference = 'Stop'
$templates = Join-Path (Split-Path $PSScriptRoot -Parent) 'workspace'

# 1. Workspace folders. Models\ is where the Revit tools file per-model output.
New-Item -ItemType Directory -Force -Path (Join-Path $WorkspaceDir 'Models') | Out-Null

# Conventions / notes: copy only when missing so user edits survive re-runs.
$workspaceAgents = Join-Path $WorkspaceDir 'AGENTS.md'
if (-not (Test-Path $workspaceAgents)) {
    Copy-Item (Join-Path $templates 'AGENTS.md') $workspaceAgents
}

# Launcher is code, not user data: always refresh.
Copy-Item (Join-Path $templates 'pi-revit-here.cmd') (Join-Path $WorkspaceDir 'pi-revit.cmd') -Force

Write-Host "Workspace ready: $WorkspaceDir" -ForegroundColor Green

# 2. Global pi-revit command next to pi (that directory is on PATH by definition).
#    The template's workspace path is rewritten to honor -WorkspaceDir.
#    Under `npx pi-revit`, npx prepends its ephemeral cache's node_modules\.bin to
#    PATH, and that folder holds a pi shim pulled in via peerDependencies. A launcher
#    written next to that shim lands in a folder that is neither on the user's own
#    PATH nor long-lived, so skip pi entries inside this package's node_modules tree
#    or any npx cache and use the user's permanent pi instead.
function Get-PermanentPiCommand {
    # Long-form both sides of the prefix comparison: PATH entries may carry 8.3
    # short names (AHMAD~1.TAH) while $PSScriptRoot is normalized, and a form
    # mismatch would silently defeat the own-tree exclusion.
    function Resolve-LongPath([string]$p) {
        try { (Get-Item -LiteralPath $p -ErrorAction Stop).FullName } catch { $p }
    }
    $ownTree = $null
    $repoRoot = Resolve-LongPath (Split-Path $PSScriptRoot -Parent)
    $idx = $repoRoot.LastIndexOf('\node_modules\', [StringComparison]::OrdinalIgnoreCase)
    if ($idx -ge 0) { $ownTree = $repoRoot.Substring(0, $idx + '\node_modules\'.Length) }
    Get-Command pi -All -ErrorAction SilentlyContinue | Where-Object {
        if (-not $_.Source) { return $false }
        $src = Resolve-LongPath $_.Source
        ($src -notmatch '\\_npx\\') -and
        (-not $ownTree -or -not $src.StartsWith($ownTree, [StringComparison]::OrdinalIgnoreCase))
    } | Select-Object -First 1
}

$pi = Get-PermanentPiCommand
if ($pi) {
    $binDir = Split-Path $pi.Source -Parent
    # Literal line swap (no regex): the workspace path may contain characters
    # like $ that a -replace replacement string would misinterpret.
    $globalCmd = Get-Content (Join-Path $templates 'pi-revit-global.cmd') | ForEach-Object {
        if ($_ -like 'set "WORKSPACE=*') { 'set "WORKSPACE=' + $WorkspaceDir + '"' } else { $_ }
    }
    # cmd.exe parses .cmd files in the console's OEM code page: write the launcher
    # in that encoding so workspace paths with non-ASCII characters (user names,
    # localized folder names) stay intact.
    Set-Content -Path (Join-Path $binDir 'pi-revit.cmd') -Value $globalCmd -Encoding Oem
    Write-Host "Global command installed: $(Join-Path $binDir 'pi-revit.cmd')" -ForegroundColor Green
    Write-Host 'Run pi-revit from any terminal (pi-revit -c continues the last session).'
} else {
    Write-Warning 'No permanent pi command found on PATH. Install Pi first (npm install -g --ignore-scripts @earendil-works/pi-coding-agent), then run npx.cmd -y pi-revit again (or re-run this script) to get the global pi-revit command.'
}
