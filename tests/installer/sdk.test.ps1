#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\scripts\check-sdk.ps1')

function Assert-True($Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

# Stub only the external SDK probe; exercise the production prerequisite check.
function dotnet {
    Assert-True ($args.Count -eq 1 -and $args[0] -eq '--version') 'The prerequisite check must never build or install.'
    $global:LASTEXITCODE = $script:SdkStatus
    $script:SdkVersion
}

$cases = @(
    @{ Version = '9.0.312'; Status = 0; Frameworks = @('net10.0-windows'); Expected = $false; Label = 'SDK 9 with Revit 2027' },
    @{ Version = '10.0.100'; Status = 0; Frameworks = @('net10.0-windows'); Expected = $true; Label = 'SDK 10 with Revit 2027' },
    @{ Version = '11.0.100'; Status = 0; Frameworks = @('net10.0-windows'); Expected = $true; Label = 'Newer SDK accepted' },
    @{ Version = '9.0.312'; Status = 0; Frameworks = @('net8.0-windows'); Expected = $true; Label = 'SDK 9 with older Revit' },
    @{ Version = '8.0.400'; Status = 0; Frameworks = @('net8.0-windows'); Expected = $true; Label = 'SDK 8 with older Revit' },
    @{ Version = '7.0.400'; Status = 0; Frameworks = @('net8.0-windows'); Expected = $false; Label = 'SDK too old for .NET 8' },
    @{ Version = '9.0.312'; Status = 0; Frameworks = @('net8.0-windows', 'net10.0-windows'); Expected = $false; Label = 'Mixed Revit versions need SDK 10' },
    @{ Version = '10.0.100'; Status = 0; Frameworks = @('net8.0-windows', 'net10.0-windows'); Expected = $true; Label = 'One SDK can build both targets' },
    @{ Version = 'No SDKs were found'; Status = 1; Frameworks = @('net10.0-windows'); Expected = $false; Label = 'Runtime-only installation' },
    @{ Version = 'A compatible .NET SDK was not found (global.json)'; Status = 1; Frameworks = @('net10.0-windows'); Expected = $false; Label = 'Unresolved SDK pin' },
    @{ Version = 'unexpected output'; Status = 0; Frameworks = @('net10.0-windows'); Expected = $false; Label = 'Unrecognized SDK output' }
)

foreach ($case in $cases) {
    $script:SdkVersion = $case.Version
    $script:SdkStatus = $case.Status
    $captured = @(Test-PiRevitSdk -TargetFrameworks $case.Frameworks -Context 'Detected Revit: 2027' 6>&1)
    $result = $captured[-1]
    Assert-True ($result -is [bool] -and $result -eq $case.Expected) $case.Label
    if (-not $case.Expected) {
        $message = $captured -join "`n"
        $major = if ($case.Frameworks -contains 'net10.0-windows') { 10 } else { 8 }
        Assert-True ($message.Contains("https://dotnet.microsoft.com/en-us/download/dotnet/$major.0")) 'Missing matching download URL'
        Assert-True ($message.Contains('SDK, not Runtime')) 'Missing SDK/runtime explanation'
        Assert-True ($message.Contains('reopen PowerShell')) 'Missing retry instructions'
        Assert-True ($message.Contains('global.json')) 'Missing SDK selection troubleshooting'
        if ($case.Status -eq 0 -and $case.Version -match '^\d+\.') {
            Assert-True ($message.Contains("currently use .NET SDK $($case.Version)")) 'Missing selected SDK version'
        }
    }
    Write-Host "PASS: $($case.Label)"
}

# Simulate a machine with no dotnet command, regardless of the host's installation.
function Get-Command { param($Name, $ErrorAction) return $null }
$captured = @(Test-PiRevitSdk -TargetFrameworks @('net10.0-windows') 6>&1)
Assert-True ($captured[-1] -eq $false) 'Missing dotnet should fail cleanly'
Assert-True (($captured -join "`n").Contains('No usable .NET SDK')) 'Missing dotnet explanation'
Write-Host 'PASS: No dotnet command'
