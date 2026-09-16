#Requires -Version 5.1

function Test-PiRevitSdk {
    param(
        [string[]]$TargetFrameworks,
        [string]$Context = 'Revit bridge build',
        [switch]$OfferDownload
    )

    # Check the SDK selected in the build's working directory, not just installed
    # SDKs: global.json or PATH can still select an older SDK.
    $requiredMajor = 0
    foreach ($framework in $TargetFrameworks) {
        if ($framework -notmatch '^net(\d+)\.') {
            throw "Cannot determine the required .NET SDK for '$framework'."
        }
        $requiredMajor = [Math]::Max($requiredMajor, [int]$matches[1])
    }
    if ($requiredMajor -eq 0) { throw 'No target frameworks were supplied for the SDK check.' }

    $selectedVersion = $null
    if (Get-Command dotnet -ErrorAction SilentlyContinue) {
        try {
            $versionOutput = @(& dotnet --version 2>&1)
            if ($LASTEXITCODE -eq 0) {
                foreach ($line in $versionOutput) {
                    if ("$line".Trim() -match '^(\d+)\.\d+\.\d+(?:-[\w.-]+)?$') {
                        $selectedVersion = "$line".Trim()
                        if ([int]$matches[1] -ge $requiredMajor) { return $true }
                    }
                }
            }
        }
        catch {
            # A runtime-only installation or an unresolved global.json can make
            # --version fail. Present the same actionable prerequisite message.
        }
    }

    $downloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/$requiredMajor.0"
    Write-Host "`npi-revit prerequisite check: $Context" -ForegroundColor Yellow
    Write-Host "Building this add-in requires the .NET $requiredMajor SDK or a newer compatible SDK."
    if ($selectedVersion) {
        Write-Host "Your build tools currently use .NET SDK $selectedVersion."
    }
    else {
        Write-Host 'No usable .NET SDK could be selected in this terminal.'
    }
    Write-Host 'Revit can run normally with its runtime; compiling the pi-revit add-in also needs the SDK.'
    Write-Host "Install the .NET $requiredMajor SDK for Windows x64 (choose SDK, not Runtime)."
    Write-Host 'You can keep your existing .NET versions installed.'
    Write-Host "Download: $downloadUrl"
    Write-Host 'Then reopen PowerShell and rerun your install or build command.'
    Write-Host 'If the SDK is already installed, check dotnet --list-sdks, PATH, and any global.json selecting an older SDK.'

    if ($OfferDownload -and [Environment]::UserInteractive -and -not [Console]::IsInputRedirected) {
        try {
            $answer = Read-Host 'Open the SDK download page now? [y/N]'
            if ($answer -match '^(?i:y|yes)$') { Start-Process $downloadUrl | Out-Null }
        }
        catch {
            Write-Host "Open the download link above in your browser."
        }
    }
    return $false
}
