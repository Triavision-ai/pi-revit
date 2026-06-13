#Requires -Version 5.1
<#
Builds and deploys the Revit bridge add-in.

Layout after deploy:
  %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge.addin
  %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge\  (RevitBridge.dll + the full
    build output: Roslyn/Microsoft.CodeAnalysis dependencies for execute_csharp, D13)

The bridge multi-targets net8.0-windows (Revit 2025/2026) and net10.0-windows (Revit 2027).
-RevitVersion selects which framework is built and deployed.

Usage:
  scripts\deploy.ps1
  scripts\deploy.ps1 -RevitVersion 2026 -RevitApiPath "C:\Program Files\Autodesk\Revit 2026"
  scripts\deploy.ps1 -RevitVersion 2027 -RevitApiPath "C:\Program Files\Autodesk\Revit 2027"
  scripts\deploy.ps1 -SkipBuild
#>
param(
    [string]$RevitVersion = '2025',
    [string]$Configuration = 'Release',
    [string]$RevitApiPath = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# Revit 2027 runs on .NET 10; 2025/2026 on .NET 8. Build/deploy the matching framework only.
$targetFramework = if ([int]$RevitVersion -ge 2027) { 'net10.0-windows' } else { 'net8.0-windows' }

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -RevitApiPath $RevitApiPath -TargetFramework $targetFramework
}

$sourceDir = Join-Path $PSScriptRoot "..\src\Revit\bin\$Configuration\$targetFramework"
$dll = Join-Path $sourceDir 'RevitBridge.dll'
if (-not (Test-Path $dll)) {
    throw "Build output not found: $dll. Run scripts\build.ps1 first."
}

$addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$targetDir = Join-Path $addinsDir 'RevitBridge'
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

# execute_csharp ships Roslyn (D13): copy the ENTIRE build output (RevitBridge.dll,
# the Microsoft.CodeAnalysis dependency closure, pdb and deps.json) into the add-in folder.
Copy-Item -Path (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force

Copy-Item (Join-Path $PSScriptRoot '..\src\Revit\RevitBridge.addin') $addinsDir -Force

Write-Host "Deployed Revit bridge add-in to $addinsDir" -ForegroundColor Green
Write-Host 'Restart Revit to load it. Verify with: scripts\revit.ps1 ping'
