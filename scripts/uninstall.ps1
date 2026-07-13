#Requires -Version 5.1
<#
Removes everything the pi-revit install scripts placed on this PC:

  - Revit bridge add-in:  %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge.addin
                          %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge\
  - Global command:       the pi-revit command next to the pi command
  - Bridge runtime:       %APPDATA%\RevitBridge\ (per-start connection token / bridge.json)
  - Pi package:           the pi-revit registration (pi remove), best-effort when pi is on PATH

The workspace at Documents\pi-revit holds YOUR data (AGENTS.md notes, per-project
session history) and is preserved by default. Pass -RemoveWorkspace to delete it too.

Pi itself (the global coding agent) is yours to manage and is never touched.

Close Revit before running. Idempotent — safe to re-run.

Usage:
  scripts\uninstall.ps1
  scripts\uninstall.ps1 -RevitVersion 2027
  scripts\uninstall.ps1 -RemoveWorkspace
#>
param(
    [string]$RevitVersion = '',
    [string]$WorkspaceDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'pi-revit'),
    [switch]$RemoveWorkspace
)

$ErrorActionPreference = 'Stop'

function Remove-IfExists {
    param([string]$Path, [string]$Label)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
        Write-Host "Removed $Label : $Path" -ForegroundColor Green
    } else {
        Write-Host "Not present, skipped $Label : $Path"
    }
}

# 1. Revit add-in. With no -RevitVersion, sweep every Addins\<version> folder so the
#    user need not remember which Revit they deployed to.
$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
$versions = if ($RevitVersion) {
    @($RevitVersion)
} elseif (Test-Path -LiteralPath $addinsRoot) {
    Get-ChildItem -LiteralPath $addinsRoot -Directory | ForEach-Object { $_.Name }
} else {
    @()
}
foreach ($v in $versions) {
    $dir = Join-Path $addinsRoot $v
    Remove-IfExists (Join-Path $dir 'RevitBridge.addin') "Revit $v add-in manifest"
    Remove-IfExists (Join-Path $dir 'RevitBridge')       "Revit $v add-in folder"
}

# 2. Global pi-revit command next to the user's permanent pi. When this script runs
#    from an npx cache or a node_modules copy, PATH may lead Get-Command to a pi shim
#    inside that ephemeral tree first (see setup-workspace.ps1) — the launcher was
#    never installed there, so resolve the permanent pi the same way setup does.
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

$pi = Get-Command pi -ErrorAction SilentlyContinue
$piPermanent = Get-PermanentPiCommand
if ($piPermanent) {
    $binDir = Split-Path $piPermanent.Source -Parent
    Remove-IfExists (Join-Path $binDir 'pi-revit.cmd') 'global pi-revit command'
} else {
    Write-Host 'No permanent pi found on PATH; skipping the global pi-revit command.'
}

# 3. Bridge runtime folder (connection token; recreated on each Revit start).
Remove-IfExists (Join-Path $env:APPDATA 'RevitBridge') 'bridge runtime folder'

# 4. Pi package registration. Pi keys a package by its install source: the npm
#    installer (npx.cmd -y pi-revit) registers `npm:pi-revit`, while an install
#    from a clone of this repo (`pi install ./`) registers the repo path resolved
#    from the repo root. Try both forms; each succeeds only for the source that
#    is actually registered, so a machine with both gets both removed.
if ($pi) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    # When this script runs from the npm-installed copy, `pi remove npm:pi-revit`
    # deletes the very folder the script lives in — and Windows fails that delete
    # (EBUSY) while any working directory sits inside it. Step our own process out
    # if needed; the calling shell's location we can only warn about.
    if ($repoRoot -match '\\node_modules\\' -and
        (Get-Location).Path.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Set-Location $env:SystemRoot
        Write-Warning "This PowerShell was started inside $repoRoot. If the package removal below fails, run 'cd \' in your shell and re-run the uninstall."
    }
    try {
        $removed = @()
        # './' resolves against the repo root, so it runs from there — and first,
        # while this folder is guaranteed to still exist. 'npm:pi-revit' then runs
        # from a neutral directory so no working directory blocks the folder delete.
        foreach ($source in @('./', 'npm:pi-revit')) {
            Push-Location $(if ($source -eq './') { $repoRoot } else { $env:SystemRoot })
            try {
                $global:LASTEXITCODE = 0
                & pi remove $source
                if ($LASTEXITCODE -eq 0) {
                    $removed += $source
                }
            } finally {
                Pop-Location
            }
        }
        if ($removed.Count -gt 0) {
            Write-Host "Removed the pi-revit Pi package ($($removed -join ', '))." -ForegroundColor Green
        } else {
            Write-Warning "No pi-revit Pi package registration was found. If pi still lists it, run 'pi remove npm:pi-revit' (npm install) or 'pi remove ./' from this repo (install from source)."
        }
    } catch {
        Write-Warning "Could not auto-remove the Pi package; run 'pi remove npm:pi-revit' (npm install) or 'pi remove ./' from this repo (install from source). ($_)"
    }
}

# 5. Workspace (user data) — opt-in only.
if ($RemoveWorkspace) {
    Remove-IfExists $WorkspaceDir 'workspace'
} else {
    Write-Host ''
    Write-Host "Workspace preserved (your notes + session history): $WorkspaceDir" -ForegroundColor Yellow
    Write-Host 'Pass -RemoveWorkspace to delete it too.'
}

Write-Host ''
Write-Host 'pi-revit uninstalled. Pi itself is untouched; remove it yourself if you want:' -ForegroundColor Green
Write-Host '  npm uninstall -g @earendil-works/pi-coding-agent'
