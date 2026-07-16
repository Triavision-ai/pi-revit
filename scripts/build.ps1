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

# Stamp the package version into the assembly so the bridge can report which release
# the deployed add-in came from; the pi extension compares it against its own package
# version at session start to detect partial updates.
$packageJson = Join-Path $PSScriptRoot '..\package.json'
if (Test-Path $packageJson) {
    $packageVersion = (Get-Content $packageJson -Raw | ConvertFrom-Json).version
    if ($packageVersion) {
        $buildArgs += "-p:Version=$packageVersion"
    }
}
if ($TargetFramework) {
    # Override TargetFrameworks (plural) rather than TargetFramework: NuGet restore ignores a
    # single -p:TargetFramework and still restores every framework the project declares, so a
    # machine without the .NET 10 SDK fails the whole build with NETSDK1045 even when only
    # net8.0 was asked for. Overriding the list restricts restore + build to this one framework.
    $buildArgs += "-p:TargetFrameworks=$TargetFramework"
}
if ($RevitApiPath) {
    $buildArgs += "-p:RevitApiPath=$RevitApiPath"
}

& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}
