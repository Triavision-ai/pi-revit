#Requires -Version 5.1
<#
Sets up the pi-revit user environment on this PC:

  1. Workspace at Documents\pi-revit (AGENTS.md conventions, Projects\sample template,
     double-click launcher). Existing AGENTS.md files are never overwritten.
  2. Global `pi-revit` command installed next to the `pi` command, so any terminal can run:
       pi-revit              -> Pi in the workspace
       pi-revit <project>    -> Pi in Projects\<project>
       pi-revit <project> -c -> continue that project's last session

Idempotent — safe to re-run. Usage:
  scripts\setup-workspace.ps1
  scripts\setup-workspace.ps1 -WorkspaceDir "D:\work\pi-revit"
#>
param(
    [string]$WorkspaceDir = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'pi-revit')
)

$ErrorActionPreference = 'Stop'
$templates = Join-Path (Split-Path $PSScriptRoot -Parent) 'workspace'

# 1. Workspace folders
New-Item -ItemType Directory -Force -Path (Join-Path $WorkspaceDir 'Projects\sample') | Out-Null

# Conventions / notes: copy only when missing so user edits survive re-runs.
$workspaceAgents = Join-Path $WorkspaceDir 'AGENTS.md'
if (-not (Test-Path $workspaceAgents)) {
    Copy-Item (Join-Path $templates 'AGENTS.md') $workspaceAgents
}
$projectAgents = Join-Path $WorkspaceDir 'Projects\sample\AGENTS.md'
if (-not (Test-Path $projectAgents)) {
    Copy-Item (Join-Path $templates 'project-AGENTS.md') $projectAgents
}

# Launcher is code, not user data: always refresh.
Copy-Item (Join-Path $templates 'pi-revit-here.cmd') (Join-Path $WorkspaceDir 'pi-revit.cmd') -Force

Write-Host "Workspace ready: $WorkspaceDir" -ForegroundColor Green

# 2. Global pi-revit command next to pi (that directory is on PATH by definition).
$pi = Get-Command pi -ErrorAction SilentlyContinue
if ($pi) {
    $binDir = Split-Path $pi.Source -Parent
    Copy-Item (Join-Path $templates 'pi-revit-global.cmd') (Join-Path $binDir 'pi-revit.cmd') -Force
    Write-Host "Global command installed: $(Join-Path $binDir 'pi-revit.cmd')" -ForegroundColor Green
    Write-Host 'Run pi-revit from any terminal (pi-revit <project> for a project folder).'
} else {
    Write-Warning 'pi command not found on PATH. Install Pi first (npm install -g --ignore-scripts @earendil-works/pi-coding-agent), then re-run this script to get the global pi-revit command.'
}
