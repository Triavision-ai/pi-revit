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

# 2. Global pi-revit command next to pi.
$pi = Get-Command pi -ErrorAction SilentlyContinue
if ($pi) {
    $binDir = Split-Path $pi.Source -Parent
    Remove-IfExists (Join-Path $binDir 'pi-revit.cmd') 'global pi-revit command'
} else {
    Write-Host 'pi not found on PATH; skipping the global pi-revit command.'
}

# 3. Bridge runtime folder (connection token; recreated on each Revit start).
Remove-IfExists (Join-Path $env:APPDATA 'RevitBridge') 'bridge runtime folder'

# 4. Pi package registration. Best-effort: the repo root is this script's parent.
if ($pi) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    try {
        & pi remove $repoRoot
        if ($LASTEXITCODE -eq 0) {
            Write-Host 'Removed the pi-revit Pi package.' -ForegroundColor Green
        } else {
            Write-Warning "pi remove exited with code $LASTEXITCODE; run 'pi remove ./' from the repo manually."
        }
    } catch {
        Write-Warning "Could not auto-remove the Pi package; run 'pi remove ./' from the repo manually. ($_)"
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
