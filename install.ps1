#Requires -Version 5.1
<#
    Roblox Server Deployment — one-line bootstrap.

    Usage (run in PowerShell):
        irm https://raw.githubusercontent.com/xelasleepi/vps/main/install.ps1 | iex

    Downloads the latest self-contained Setup.exe (no .NET install required) and
    its config.json from the GitHub release, then launches the deployment
    elevated. Approve the single UAC prompt — everything after that is automatic.
#>

$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$Repo   = 'xelasleepi/vps'
$ExeUrl = "https://github.com/$Repo/releases/latest/download/Setup.exe"
$CfgUrl = "https://github.com/$Repo/releases/latest/download/config.json"

$work = Join-Path $env:TEMP ('RobloxDeploy_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$exe = Join-Path $work 'Setup.exe'

Write-Host ''
Write-Host '  ╔══════════════════════════════════════════╗' -ForegroundColor Cyan
Write-Host '  ║        Roblox Server Deployment          ║' -ForegroundColor Cyan
Write-Host '  ╚══════════════════════════════════════════╝' -ForegroundColor Cyan
Write-Host ''
Write-Host '  Downloading Setup.exe (~63 MB, self-contained)...' -ForegroundColor White

$prev = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'   # dramatically speeds up Invoke-WebRequest
try {
    Invoke-WebRequest -Uri $ExeUrl -OutFile $exe -UseBasicParsing
    try {
        Invoke-WebRequest -Uri $CfgUrl -OutFile (Join-Path $work 'config.json') -UseBasicParsing
    } catch {
        Write-Host '  No config.json asset found — using the exe''s built-in defaults.' -ForegroundColor Yellow
    }
}
finally { $ProgressPreference = $prev }

if (-not (Test-Path $exe) -or (Get-Item $exe).Length -lt 1MB) {
    throw "Download failed or incomplete: $exe"
}

Write-Host "  Saved to: $work" -ForegroundColor DarkGray
Write-Host '  Launching deployment — approve the UAC prompt.' -ForegroundColor Green
Write-Host ''

# Setup.exe's manifest requires administrator; -Verb RunAs raises the UAC prompt
# whether or not the current PowerShell session is already elevated.
Start-Process -FilePath $exe -WorkingDirectory $work -Verb RunAs
