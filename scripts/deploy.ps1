#Requires -Version 5.1
<#
Builds and deploys the Revit bridge add-in.

By default it auto-detects every supported Revit installed under %ProgramFiles%\Autodesk
(Revit 2025/2026 -> net8.0-windows, Revit 2027 -> net10.0-windows) and deploys to each one,
building only the framework(s) those installs need. Pass -RevitVersion to target a single
version explicitly (with -RevitApiPath when it is not in the default location).

Layout after deploy (per version):
  %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge.addin
  %APPDATA%\Autodesk\Revit\Addins\<version>\RevitBridge\  (RevitBridge.dll + the full
    build output: Roslyn/Microsoft.CodeAnalysis dependencies for execute_csharp)

Usage:
  scripts\deploy.ps1                                              # auto-detect + deploy to all installed
  scripts\deploy.ps1 -RevitVersion 2026
  scripts\deploy.ps1 -RevitVersion 2027 -RevitApiPath "D:\Autodesk\Revit 2027"
  scripts\deploy.ps1 -SkipBuild
#>
param(
    [string]$RevitVersion = '',
    [string]$Configuration = 'Release',
    [string]$RevitApiPath = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

# Revit 2025/2026 run on .NET 8; 2027 on .NET 10. Versions below 2025 run on .NET Framework
# and are not supported by this add-in.
$MinSupported = 2025
function Get-TargetFramework([int]$version) {
    if ($version -ge 2027) { 'net10.0-windows' } else { 'net8.0-windows' }
}

# Resolve the targets to deploy as objects: { Version; Path; Tfm }.
$targets = @()
if ($RevitVersion) {
    $v = [int]$RevitVersion
    $path = if ($RevitApiPath) { $RevitApiPath } else { Join-Path $env:ProgramFiles "Autodesk\Revit $v" }
    if (-not (Test-Path (Join-Path $path 'RevitAPI.dll'))) {
        throw "RevitAPI.dll not found in '$path'. Pass -RevitApiPath with your Revit $v install folder."
    }
    $targets += [pscustomobject]@{ Version = $v; Path = $path; Tfm = (Get-TargetFramework $v) }
}
else {
    # Auto-detect: scan %ProgramFiles%\Autodesk for "Revit <year>" folders that hold a RevitAPI.dll.
    $autodesk = Join-Path $env:ProgramFiles 'Autodesk'
    if (Test-Path $autodesk) {
        Get-ChildItem -LiteralPath $autodesk -Directory -Filter 'Revit 20*' -ErrorAction SilentlyContinue |
            ForEach-Object {
                if (($_.Name -match '(\d{4})') -and (Test-Path (Join-Path $_.FullName 'RevitAPI.dll'))) {
                    $v = [int]$matches[1]
                    if ($v -ge $MinSupported) {
                        $targets += [pscustomobject]@{ Version = $v; Path = $_.FullName; Tfm = (Get-TargetFramework $v) }
                    }
                    else {
                        Write-Warning "Skipping Revit $v (only 2025+ is supported by this add-in)."
                    }
                }
            }
    }
    if (-not $targets) {
        throw "No supported Revit (2025+) found under '$autodesk'. Install Revit, or pass -RevitVersion and -RevitApiPath for a non-default location."
    }
    $targets = @($targets | Sort-Object Version)
    Write-Host ("Detected Revit: " + (($targets | ForEach-Object { $_.Version }) -join ', ')) -ForegroundColor Cyan
}

# Build once per distinct target framework, compiling against a matching RevitAPI.dll.
if (-not $SkipBuild) {
    foreach ($group in ($targets | Group-Object Tfm)) {
        $apiPath = ($group.Group | Select-Object -First 1).Path
        & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -RevitApiPath $apiPath -TargetFramework $group.Name
    }
}

# Deploy each detected version to its own Addins folder.
foreach ($t in $targets) {
    $sourceDir = Join-Path $PSScriptRoot "..\src\Revit\bin\$Configuration\$($t.Tfm)"
    $dll = Join-Path $sourceDir 'RevitBridge.dll'
    if (-not (Test-Path $dll)) {
        throw "Build output not found: $dll. Run scripts\build.ps1 first (or omit -SkipBuild)."
    }

    $addinsDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$($t.Version)"
    $targetDir = Join-Path $addinsDir 'RevitBridge'
    New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

    # execute_csharp ships Roslyn: copy the ENTIRE build output (RevitBridge.dll,
    # the Microsoft.CodeAnalysis dependency closure, pdb and deps.json) into the add-in folder.
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $targetDir -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot '..\src\Revit\RevitBridge.addin') $addinsDir -Force

    Write-Host "Deployed Revit bridge add-in to $addinsDir (Revit $($t.Version), $($t.Tfm))" -ForegroundColor Green
}

Write-Host 'Restart Revit to load it. Verify with: scripts\revit.ps1 ping'
