#Requires -Version 5.1
<#
Builds the Revit bridge add-in.

Usage:
  scripts\build.ps1
  scripts\build.ps1 -Configuration Debug
  scripts\build.ps1 -RevitApiPath "C:\Program Files\Autodesk\Revit 2026"
#>
param(
    [string]$Configuration = 'Release',
    [string]$RevitApiPath = ''
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Revit\RevitBridge.csproj'

$buildArgs = @('build', $project, '-c', $Configuration)
if ($RevitApiPath) {
    $buildArgs += "-p:RevitApiPath=$RevitApiPath"
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
