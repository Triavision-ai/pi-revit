#Requires -Version 5.1
<#
Builds the Revit bridge add-in.

The project multi-targets net8.0-windows (Revit 2025/2026) and net10.0-windows (Revit 2027).
Omit -TargetFramework to build both; pass one to build only it (e.g. when only one Revit is installed).

Usage:
  scripts\build.ps1
  scripts\build.ps1 -Configuration Debug
  scripts\build.ps1 -TargetFramework net10.0-windows -RevitApiPath "C:\Program Files\Autodesk\Revit 2027"
#>
param(
    [string]$Configuration = 'Release',
    [string]$RevitApiPath = '',
    [string]$TargetFramework = ''
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Revit\RevitBridge.csproj'

$buildArgs = @('build', $project, '-c', $Configuration)
if ($TargetFramework) {
    $buildArgs += "-p:TargetFramework=$TargetFramework"
}
if ($RevitApiPath) {
    $buildArgs += "-p:RevitApiPath=$RevitApiPath"
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
