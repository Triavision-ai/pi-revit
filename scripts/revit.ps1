#Requires -Version 5.1
<#
Manual debug client for the Revit bridge. Not used by the Pi extension
(the extension calls the bridge directly with fetch).

Usage:
  scripts\revit.ps1 ping
  scripts\revit.ps1 tools
  scripts\revit.ps1 describe-tool get_model_overview
  scripts\revit.ps1 run get_model_overview '{}'
  scripts\revit.ps1 run get_elements '{"category":"Walls","count_only":true}'
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('ping', 'tools', 'describe-tool', 'run')]
    [string]$Command = 'ping',

    [Parameter(Position = 1)]
    [string]$ToolName,

    [Parameter(Position = 2)]
    [string]$JsonArgs = '{}',

    [int]$TimeoutSec = 0
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$bridgeInfoPath = Join-Path $env:APPDATA 'RevitBridge\bridge.json'

function Write-JsonLine($value) {
    [Console]::Out.WriteLine(($value | ConvertTo-Json -Compress -Depth 20))
}

function Write-JsonError($message) {
    Write-JsonLine @{ error = $true; message = $message }
    exit 1
}

if ($TimeoutSec -le 0) {
    $envTimeout = 0
    if ([int]::TryParse([string]$env:REVIT_BRIDGE_TIMEOUT_SEC, [ref]$envTimeout) -and $envTimeout -gt 0) {
        $TimeoutSec = $envTimeout
    } else {
        $TimeoutSec = 30
    }
}

if (-not (Test-Path $bridgeInfoPath)) {
    Write-JsonError "Revit bridge info not found. Start Revit with the bridge add-in loaded. Expected: $bridgeInfoPath"
}

try {
    $info = Get-Content $bridgeInfoPath -Raw | ConvertFrom-Json
    if (-not $info.baseUrl -or -not $info.token) {
        Write-JsonError "Revit bridge info is invalid: $bridgeInfoPath"
    }

    switch ($Command) {
        'ping' {
            $response = Invoke-RestMethod -Method Get -Uri "$($info.baseUrl)/ping`?token=$($info.token)" -TimeoutSec $TimeoutSec
            Write-JsonLine $response
        }
        'tools' {
            $response = Invoke-RestMethod -Method Get -Uri "$($info.baseUrl)/tools`?token=$($info.token)" -TimeoutSec $TimeoutSec
            Write-JsonLine $response
        }
        'describe-tool' {
            if ([string]::IsNullOrWhiteSpace($ToolName)) {
                Write-JsonError 'describe-tool requires a tool name. Example: revit.ps1 describe-tool get_model_overview'
            }
            $encoded = [System.Uri]::EscapeDataString($ToolName)
            $response = Invoke-RestMethod -Method Get -Uri "$($info.baseUrl)/tools/$encoded`?token=$($info.token)" -TimeoutSec $TimeoutSec
            Write-JsonLine $response
        }
        'run' {
            if ([string]::IsNullOrWhiteSpace($ToolName)) {
                Write-JsonError "run requires a tool name. Example: revit.ps1 run get_model_overview '{}'"
            }
            if ([string]::IsNullOrWhiteSpace($JsonArgs)) { $JsonArgs = '{}' }
            # Validate locally so callers get a clear error before the bridge call.
            $null = $JsonArgs | ConvertFrom-Json
            $encoded = [System.Uri]::EscapeDataString($ToolName)
            $uri = "$($info.baseUrl)/tools/$encoded/execute`?token=$($info.token)"
            # Send bytes: PowerShell 5.1 would otherwise encode a string body as ISO-8859-1.
            $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($JsonArgs)
            $response = Invoke-RestMethod -Method Post -Uri $uri -Body $bodyBytes -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSec
            Write-JsonLine $response
        }
    }
}
catch {
    # Surface the bridge's JSON error body (409 no-document, 403 stale token,
    # 400 bad args, 404 unknown tool) instead of Invoke-RestMethod's generic
    # status-line message. The body is already one-line JSON; print it verbatim.
    $body = $null
    if ($_.ErrorDetails -and -not [string]::IsNullOrWhiteSpace($_.ErrorDetails.Message)) {
        $body = $_.ErrorDetails.Message
    } elseif ($_.Exception -is [System.Net.WebException] -and $_.Exception.Response) {
        # Windows PowerShell 5.1: read the WebException response stream directly.
        try {
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream(), [System.Text.Encoding]::UTF8)
            $body = $reader.ReadToEnd()
        } catch { $body = $null }
    }
    if (-not [string]::IsNullOrWhiteSpace($body)) {
        [Console]::Out.WriteLine($body.Trim())
        exit 1
    }
    Write-JsonError $_.Exception.Message
}
