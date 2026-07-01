<#
    Roblox Server Deployment — native PowerShell edition.

        irm https://raw.githubusercontent.com/xelasleepi/vps/main/install.ps1 | iex

    Runs entirely in the terminal (no GUI, no compiled exe, no .NET required).
    Self-elevates, then silently optimizes Windows and installs the software
    stack with a clean colored console UI and full logging. Idempotent — safe to
    re-run. Target: Tiny10 x64 23H2.
#>

# ============================================================================
#  0. Bootstrap: strict mode, self-URL, self-elevation
# ============================================================================
$ErrorActionPreference = 'Stop'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$SelfUrl = 'https://raw.githubusercontent.com/xelasleepi/vps/main/install.ps1'

# Re-launch elevated if we are not already Administrator. The elevated window
# re-fetches this script from $SelfUrl (works even when started via irm|iex,
# where there is no local script file to relaunch).
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
    ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "`n  Requesting administrator privileges..." -ForegroundColor Yellow
    $psExe = (Get-Process -Id $PID).Path        # relaunch with the same host (powershell/pwsh)
    if (-not $psExe) { $psExe = 'powershell.exe' }
    try {
        Start-Process -FilePath $psExe -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-NoExit',
            '-Command', "irm $SelfUrl | iex"
        )
    } catch {
        Write-Host "  Elevation was cancelled. Aborting." -ForegroundColor Red
    }
    return
}

# ============================================================================
#  1. Console setup (UTF-8 for box-drawing / icons)
# ============================================================================
try {
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $OutputEncoding = [Text.Encoding]::UTF8
    chcp 65001 > $null 2>&1
} catch { }

$Host.UI.RawUI.WindowTitle = 'Roblox Server Deployment'
$ProgressPreference = 'SilentlyContinue'   # we render our own progress

# ============================================================================
#  2. Configuration (embedded defaults; override with config.json beside script)
# ============================================================================
$Config = [ordered]@{
    AutoReboot      = $false
    CleanupOnFinish = $true
    Features = [ordered]@{
        OptimizeWindows            = $true
        InstallWinRAR              = $true
        InstallVisualCpp           = $true
        InstallDotNet              = $true
        InstallWebView2            = $true
        InstallDirectX             = $true
        InstallMemReduct           = $true
        InstallRoblox              = $true
        InstallRobloxAccountManager= $true
    }
    MemReduct = [ordered]@{
        Autostart          = $true
        StartMinimized     = $true
        MinimizeToTray     = $true
        AutoClean          = $true
        ThresholdPercent   = 85
    }
}

# ============================================================================
#  3. Working directories + logging
# ============================================================================
$Root = Join-Path $env:ProgramData 'RobloxDeploy'
$Dl   = Join-Path $Root 'downloads'
$Logs = Join-Path $Root 'logs'
$Tmp  = Join-Path $Root 'temp'
$null = New-Item -ItemType Directory -Force -Path $Root, $Dl, $Logs, $Tmp

$LogFiles = @{
    Install      = Join-Path $Logs 'install.log'
    Errors       = Join-Path $Logs 'errors.log'
    Downloads    = Join-Path $Logs 'downloads.log'
    Optimization = Join-Path $Logs 'optimization.log'
    Software     = Join-Path $Logs 'software.log'
}

function Write-LogFile {
    param([string]$Message, [string]$Channel = 'Install')
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    try { Add-Content -Path $LogFiles[$Channel] -Value $line -Encoding UTF8 } catch { }
    if ($Channel -ne 'Install') {
        try { Add-Content -Path $LogFiles['Install'] -Value $line -Encoding UTF8 } catch { }
    }
}

# ============================================================================
#  4. Terminal UI helpers
# ============================================================================
$script:Results = New-Object System.Collections.ArrayList
$script:StartTime = Get-Date

function HB([double]$b) {
    if ($b -ge 1GB) { '{0:N1} GB' -f ($b/1GB) }
    elseif ($b -ge 1MB) { '{0:N1} MB' -f ($b/1MB) }
    elseif ($b -ge 1KB) { '{0:N0} KB' -f ($b/1KB) }
    else { '{0} B' -f [int]$b }
}

function Write-Banner {
    Write-Host ''
    Write-Host '  ╔════════════════════════════════════════════════════╗' -ForegroundColor Cyan
    Write-Host '  ║' -ForegroundColor Cyan -NoNewline
    Write-Host '            Roblox Server Deployment                ' -ForegroundColor White -NoNewline
    Write-Host '║' -ForegroundColor Cyan
    Write-Host '  ╚════════════════════════════════════════════════════╝' -ForegroundColor Cyan
    Write-Host '   Tiny10 x64 · unattended · silent' -ForegroundColor DarkGray
    Write-Host ''
}

function Write-Section($Title) {
    Write-Host ''
    Write-Host "  ── $Title " -ForegroundColor Blue -NoNewline
    Write-Host ('─' * [Math]::Max(0, 46 - $Title.Length)) -ForegroundColor DarkGray
}

function Write-Line($Glyph, $Color, $Name, $Detail) {
    Write-Host "   $Glyph " -ForegroundColor $Color -NoNewline
    Write-Host ('{0,-38}' -f $Name) -ForegroundColor Gray -NoNewline
    if ($Detail) { Write-Host $Detail -ForegroundColor DarkGray } else { Write-Host '' }
}
function Write-Ok   ($n, $d) { Write-Line '✔' Green   $n $d;  Write-LogFile "[SUCCESS] $n $d" 'Software' }
function Write-Skip ($n, $d) { Write-Line '↷' Yellow  $n $d;  Write-LogFile "[SKIPPED] $n $d" 'Software' }
function Write-Fail ($n, $d) { Write-Line '✖' Red     $n $d;  Write-LogFile "[FAILED]  $n $d" 'Errors' }
function Write-Info ($m)     { Write-Host "   • $m" -ForegroundColor DarkGray; Write-LogFile "[INFO] $m" }

function Record($Name, $Status, $Elapsed, $Detail) {
    [void]$script:Results.Add([pscustomobject]@{
        Name = $Name; Status = $Status
        Elapsed = ('{0:N1}s' -f ([double]$Elapsed)); Detail = $Detail
    })
}

# ============================================================================
#  5. Download manager (retry · resume-safe · SHA-256 · inline progress)
# ============================================================================
function Get-File {
    param([string]$Url, [string]$Dest, [string]$Label, [int]$Retries = 3, [string]$Sha256)

    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $fs = $null; $stream = $null; $resp = $null
        try {
            $req = [System.Net.HttpWebRequest]::Create($Url)
            $req.UserAgent      = 'SetupDeployer/2.0'
            $req.AllowAutoRedirect = $true
            $req.Timeout        = 60000
            $req.ReadWriteTimeout = 300000
            $resp   = $req.GetResponse()
            $total  = [double]$resp.ContentLength
            $stream = $resp.GetResponseStream()
            $fs     = [System.IO.File]::Open($Dest, 'Create')

            $buffer = New-Object byte[] 81920
            $read   = 0.0
            $sw     = [Diagnostics.Stopwatch]::StartNew()
            $lastMs = 0
            while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $fs.Write($buffer, 0, $n); $read += $n
                if (($sw.Elapsed.TotalMilliseconds - $lastMs) -gt 120) {
                    $spd = $read / [Math]::Max(0.001, $sw.Elapsed.TotalSeconds)
                    $pct = if ($total -gt 0) { [int](($read / $total) * 100) } else { 0 }
                    Write-Host ("`r   ⏬ {0,-24} {1,8} / {2,-8} {3,3}%  {4}/s      " -f `
                        $Label, (HB $read), (HB $total), $pct, (HB $spd)) -ForegroundColor Cyan -NoNewline
                    $lastMs = $sw.Elapsed.TotalMilliseconds
                }
            }
            $fs.Close(); $stream.Close(); $resp.Close()
            Write-Host ("`r   ⏬ {0,-24} {1,8}   downloaded             " -f $Label, (HB $read)) -ForegroundColor DarkCyan
            Write-LogFile "[OK] $Label -> $Dest ($([int]$read) bytes)" 'Downloads'

            if ($Sha256) {
                $h = (Get-FileHash -Path $Dest -Algorithm SHA256).Hash
                if ($h -ne $Sha256) { throw "SHA-256 mismatch (got $h)" }
            }
            return $true
        }
        catch {
            Write-Host ("`r   ⚠ {0,-24} attempt {1}/{2}: {3}                 " -f `
                $Label, $attempt, $Retries, $_.Exception.Message) -ForegroundColor DarkYellow
            Write-LogFile "[RETRY $attempt/$Retries] $Label : $($_.Exception.Message)" 'Downloads'
            Start-Sleep -Seconds ([Math]::Min(8, $attempt * 2))
        }
        finally {
            if ($fs)     { $fs.Dispose() }
            if ($stream) { $stream.Dispose() }
            if ($resp)   { $resp.Dispose() }
        }
    }
    return $false
}

# ============================================================================
#  6. Detection + registry helpers
# ============================================================================
function Test-UninstallName([string]$Pattern) {
    $keys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($k in $keys) {
        try {
            if (Get-ItemProperty $k -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -like "*$Pattern*" }) { return $true }
        } catch { }
    }
    return $false
}

function Set-Reg {
    param([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord')
    try {
        if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
        return $true
    } catch { Write-LogFile "[WARN] reg $Path\$Name : $($_.Exception.Message)" 'Optimization'; return $false }
}

# ============================================================================
#  7. Generic installer
# ============================================================================
function Install-Item {
    param(
        [string]$Name, [string]$Url, [string]$Arguments,
        [scriptblock]$Detect, [scriptblock]$Verify,
        [string]$Sha256, [string]$Ext = '.exe', [bool]$Enabled = $true
    )
    if (-not $Enabled) { Write-Skip $Name '(disabled in config)'; Record $Name 'Skipped' 0 'disabled'; return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-LogFile "[INFO] Installing $Name" 'Software'

    if ($Detect -and (& $Detect)) { Write-Skip $Name '(already installed)'; Record $Name 'Skipped' $sw.Elapsed.TotalSeconds 'present'; return }

    $file = Join-Path $Dl (($Name -replace '[^\w]', '_') + $Ext)
    if (-not (Get-File -Url $Url -Dest $file -Label $Name -Sha256 $Sha256)) {
        Write-Fail $Name 'download failed after 3 attempts'; Record $Name 'Failed' $sw.Elapsed.TotalSeconds 'download'; return
    }
    try {
        if ($Arguments) { $p = Start-Process -FilePath $file -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden }
        else            { $p = Start-Process -FilePath $file -PassThru -Wait -WindowStyle Hidden }
        $code = $p.ExitCode
    } catch { Write-Fail $Name $_.Exception.Message; Record $Name 'Failed' $sw.Elapsed.TotalSeconds 'run'; return }

    Start-Sleep -Milliseconds 400
    $ok = if ($Verify) { & $Verify } else { $code -in 0, 1638, 3010, 1641 }
    if ($ok) { Write-Ok $Name ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Record $Name 'Installed' $sw.Elapsed.TotalSeconds "exit $code" }
    else     { Write-Fail $Name "installer exit code $code"; Record $Name 'Failed' $sw.Elapsed.TotalSeconds "exit $code" }
}

# ============================================================================
#  8. Windows optimization
# ============================================================================
function Invoke-Optimizations {
    if (-not $Config.Features.OptimizeWindows) { Write-Info 'Optimization disabled by config.'; return }

    Write-Section 'Optimizing Windows'

    # --- Services ---------------------------------------------------------
    $svc = @{
        'SysMain' = 'SysMain'; 'WSearch' = 'Windows Search'; 'DoSvc' = 'Delivery Optimization'
        'XblAuthManager' = 'Xbox Auth'; 'XblGameSave' = 'Xbox Save'
        'XboxGipSvc' = 'Xbox GIP'; 'XboxNetApiSvc' = 'Xbox Net'
    }
    $done = 0
    foreach ($s in $svc.Keys) {
        try {
            if (Get-Service -Name $s -ErrorAction SilentlyContinue) {
                Stop-Service -Name $s -Force -ErrorAction SilentlyContinue
                Set-Service  -Name $s -StartupType Disabled -ErrorAction SilentlyContinue
                $done++
            }
        } catch { }
    }
    Write-Ok 'Disable background services' "($done of $($svc.Count) present)"; Write-LogFile "[OPT] services disabled: $done" 'Optimization'

    # --- Game Bar / Game DVR ---------------------------------------------
    Set-Reg 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 0
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
    Write-Ok 'Disable Xbox Game Bar & Game DVR'

    # --- Consumer experience / suggestions -------------------------------
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 1
    $cdm = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
    foreach ($v in 'SubscribedContent-338389Enabled','SubscribedContent-338388Enabled',
                   'SubscribedContent-338387Enabled','SystemPaneSuggestionsEnabled',
                   'SoftLandingEnabled','RotatingLockScreenEnabled','RotatingLockScreenOverlayEnabled',
                   'SilentInstalledAppsEnabled','PreInstalledAppsEnabled','OemPreInstalledAppsEnabled') {
        Set-Reg $cdm $v 0
    }
    Write-Ok 'Disable Consumer Experience & suggestions'

    # --- Background apps + Store auto-update ------------------------------
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy' 'LetAppsRunInBackground' 2
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload' 2
    Write-Ok 'Disable Background Apps & auto app updates'

    # --- Power plan + timeouts + hibernation ------------------------------
    $ult = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
    powercfg -duplicatescheme $ult 2>$null | Out-Null
    $act = powercfg -setactive $ult 2>&1
    if ($LASTEXITCODE -ne 0) { powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null; $plan = 'High Performance' } else { $plan = 'Ultimate Performance' }
    foreach ($t in '-standby-timeout-ac 0','-standby-timeout-dc 0','-hibernate-timeout-ac 0',
                    '-hibernate-timeout-dc 0','-monitor-timeout-ac 0','-monitor-timeout-dc 0') {
        Start-Process powercfg -ArgumentList "-change $t" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
    }
    powercfg -hibernate off 2>$null | Out-Null
    Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
    # USB selective suspend + PCIe ASPM off
    powercfg -setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 2>$null | Out-Null
    powercfg -setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0 2>$null | Out-Null
    powercfg -setactive SCHEME_CURRENT 2>$null | Out-Null
    Write-Ok "Power plan → $plan (never sleep/hibernate)"

    # --- System perf tweaks ----------------------------------------------
    Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 24
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' 'VisualFXSetting' 2
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec' 0
    Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' 'MaintenanceDisabled' 1
    schtasks /Change /TN '\Microsoft\Windows\Defrag\ScheduledDefrag' /Disable 2>$null | Out-Null
    Write-Ok 'System performance tweaks (scheduling, visuals, defrag)'

    # --- Explorer ---------------------------------------------------------
    $adv = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
    Set-Reg $adv 'HideFileExt' 0
    Set-Reg $adv 'Hidden' 1
    Set-Reg $adv 'LaunchTo' 1
    Set-Reg $adv 'Start_TrackDocs' 0
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'ShowRecent' 0
    Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'ShowFrequent' 0
    Write-Ok 'Explorer (extensions, hidden files, This PC, no recent)'

    # --- Cleanup ----------------------------------------------------------
    $freed = 0
    foreach ($d in $env:TEMP, "$env:SystemRoot\Temp", "$env:SystemRoot\Prefetch") {
        try {
            Get-ChildItem $d -Force -ErrorAction SilentlyContinue | ForEach-Object {
                $freed += ($_.Length) ; Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Write-Ok 'Clean Temp / Windows Temp / Prefetch' "(~$(HB $freed) freed)"
    Write-LogFile "[OPT] cleanup freed ~$([int]$freed) bytes" 'Optimization'
}

# ============================================================================
#  9. Software
# ============================================================================
function Invoke-Software {
    Write-Section 'Installing Software'
    $f = $Config.Features

    # WinRAR
    Install-Item -Name 'WinRAR' -Enabled $f.InstallWinRAR `
        -Url 'https://www.rarlab.com/rar/winrar-x64-711.exe' -Arguments '/S' `
        -Detect { (Test-Path "$env:ProgramFiles\WinRAR\WinRAR.exe") -or (Test-UninstallName 'WinRAR') } `
        -Verify { Test-Path "$env:ProgramFiles\WinRAR\WinRAR.exe" }

    # Visual C++ Redistributables (2005–2022, x86 + x64)
    if ($f.InstallVisualCpp) {
        $vc = @(
            @{ N='VC++ 2015-2022 x64'; U='https://aka.ms/vs/17/release/vc_redist.x64.exe'; A='/install /quiet /norestart' }
            @{ N='VC++ 2015-2022 x86'; U='https://aka.ms/vs/17/release/vc_redist.x86.exe'; A='/install /quiet /norestart' }
            @{ N='VC++ 2013 x64'; U='https://aka.ms/highdpimfc2013x64enu'; A='/install /quiet /norestart' }
            @{ N='VC++ 2013 x86'; U='https://aka.ms/highdpimfc2013x86enu'; A='/install /quiet /norestart' }
            @{ N='VC++ 2012 x64'; U='https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x64.exe'; A='/install /quiet /norestart' }
            @{ N='VC++ 2012 x86'; U='https://download.microsoft.com/download/1/6/B/16B06F60-3B20-4FF2-B699-5E9B7962F9AE/VSU_4/vcredist_x86.exe'; A='/install /quiet /norestart' }
            @{ N='VC++ 2010 x64'; U='https://download.microsoft.com/download/A/8/0/A80747C3-41BD-45DF-B505-E9710D2744E0/vcredist_x64.exe'; A='/q /norestart' }
            @{ N='VC++ 2010 x86'; U='https://download.microsoft.com/download/C/6/D/C6D0FD4E-9E53-4897-9B91-836EBA2AACD3/vcredist_x86.exe'; A='/q /norestart' }
            @{ N='VC++ 2008 x64'; U='https://download.microsoft.com/download/5/D/8/5D8C65CB-C849-4025-8E95-C3966CAFD8AE/vcredist_x64.exe'; A='/q' }
            @{ N='VC++ 2008 x86'; U='https://download.microsoft.com/download/9/7/7/977B481A-7BA6-4E30-AC40-ED51EB2028F2/vcredist_x86.exe'; A='/q' }
            @{ N='VC++ 2005 x64'; U='https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x64.EXE'; A='/q' }
            @{ N='VC++ 2005 x86'; U='https://download.microsoft.com/download/8/B/4/8B42259F-5D70-43F4-AC2E-4B208FD8D66A/vcredist_x86.EXE'; A='/q' }
        )
        foreach ($r in $vc) { Install-Item -Name $r.N -Url $r.U -Arguments $r.A }
    } else { Write-Skip 'Visual C++ Redistributables' '(disabled in config)' }

    # .NET Framework 4.8
    Install-Item -Name '.NET Framework 4.8' -Enabled $f.InstallDotNet `
        -Url 'https://go.microsoft.com/fwlink/?linkid=2088631' -Arguments '/q /norestart' `
        -Detect { (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release -ge 528040 }

    # .NET Desktop Runtime 8
    Install-Item -Name '.NET Desktop Runtime 8' -Enabled $f.InstallDotNet `
        -Url 'https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.11/windowsdesktop-runtime-8.0.11-win-x64.exe' -Arguments '/install /quiet /norestart' `
        -Detect { Test-Path "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App\8.*" }

    # Edge WebView2 Runtime
    Install-Item -Name 'Edge WebView2 Runtime' -Enabled $f.InstallWebView2 `
        -Url 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -Arguments '/silent /install' `
        -Detect {
            $p = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue).pv
            $p -and $p -ne '0.0.0.0'
        }

    # DirectX June 2010 runtime (self-extractor → DXSETUP)
    if ($f.InstallDirectX) {
        if ((Test-Path "$env:SystemRoot\System32\d3dx9_43.dll")) {
            Write-Skip 'DirectX Runtime (June 2010)' '(already present)'; Record 'DirectX' 'Skipped' 0 'present'
        } else {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $dxSfx = Join-Path $Dl 'directx_redist.exe'; $dxDir = Join-Path $Tmp 'directx'
            $null = New-Item -ItemType Directory -Force -Path $dxDir
            if (Get-File -Url 'https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe' -Dest $dxSfx -Label 'DirectX Runtime') {
                Start-Process $dxSfx -ArgumentList "/Q /T:`"$dxDir`" /C" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                $setup = Join-Path $dxDir 'DXSETUP.exe'
                if (Test-Path $setup) { Start-Process $setup -ArgumentList '/silent' -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
                if (Test-Path "$env:SystemRoot\System32\d3dx9_43.dll") { Write-Ok 'DirectX Runtime (June 2010)' ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Record 'DirectX' 'Installed' $sw.Elapsed.TotalSeconds }
                else { Write-Fail 'DirectX Runtime (June 2010)' 'DXSETUP did not complete'; Record 'DirectX' 'Failed' $sw.Elapsed.TotalSeconds }
            } else { Write-Fail 'DirectX Runtime (June 2010)' 'download failed'; Record 'DirectX' 'Failed' $sw.Elapsed.TotalSeconds }
        }
    } else { Write-Skip 'DirectX Runtime (June 2010)' '(disabled in config)' }

    # Mem Reduct + configuration
    Install-Item -Name 'Mem Reduct' -Enabled $f.InstallMemReduct `
        -Url 'https://github.com/henrypp/memreduct/releases/download/v3.4/memreduct-3.4-setup.exe' `
        -Arguments '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS' `
        -Detect { (Test-Path "$env:ProgramFiles\Mem Reduct\memreduct.exe") -or (Test-UninstallName 'Mem Reduct') } `
        -Verify { Test-Path "$env:ProgramFiles\Mem Reduct\memreduct.exe" }
    if ($f.InstallMemReduct) { Set-MemReductConfig }

    # Roblox
    if ($f.InstallRoblox) {
        Install-Roblox
    } else { Write-Skip 'Roblox' '(disabled in config)' }

    # Roblox Account Manager (portable ZIP)
    if ($f.InstallRobloxAccountManager) {
        Install-RAM
    } else { Write-Skip 'Roblox Account Manager' '(disabled in config)' }
}

function Set-MemReductConfig {
    try {
        $exe = "$env:ProgramFiles\Mem Reduct\memreduct.exe"
        if (-not (Test-Path $exe)) { return }
        if ($Config.MemReduct.Autostart) {
            Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' 'Mem Reduct' "`"$exe`"" 'String'
        }
        $ini = "$env:ProgramFiles\Mem Reduct\memreduct.ini"
        $body = @"
[settings]
Autorun=$([int]$Config.MemReduct.Autostart)
MinimizeToTray=$([int]$Config.MemReduct.MinimizeToTray)
HideOnClose=$([int]$Config.MemReduct.MinimizeToTray)
StartMinimized=$([int]$Config.MemReduct.StartMinimized)
AutoclearEnable=$([int]$Config.MemReduct.AutoClean)
AutoclearReductByPhysical=$($Config.MemReduct.ThresholdPercent)
"@
        Set-Content -Path $ini -Value $body -Encoding UTF8 -ErrorAction SilentlyContinue
        Write-Info 'Configured Mem Reduct (autostart, tray, auto-clean).'
    } catch { Write-Info "Mem Reduct config partial: $($_.Exception.Message)" }
}

function Install-Roblox {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ver = "$env:LOCALAPPDATA\Roblox\Versions"
    if ((Test-Path $ver) -and (Get-ChildItem $ver -Recurse -Filter 'RobloxPlayerBeta.exe' -ErrorAction SilentlyContinue)) {
        Write-Skip 'Roblox' '(already installed)'; Record 'Roblox' 'Skipped' 0 'present'; return
    }
    $boot = Join-Path $Dl 'RobloxPlayerInstaller.exe'
    if (-not (Get-File -Url 'https://www.roblox.com/download/client?os=win' -Dest $boot -Label 'Roblox')) {
        Write-Fail 'Roblox' 'download failed'; Record 'Roblox' 'Failed' $sw.Elapsed.TotalSeconds; return
    }
    Start-Process $boot -WindowStyle Hidden -ErrorAction SilentlyContinue
    $ok = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        if ((Test-Path $ver) -and (Get-ChildItem $ver -Recurse -Filter 'RobloxPlayerBeta.exe' -ErrorAction SilentlyContinue)) { $ok = $true; break }
    }
    Get-Process RobloxPlayerBeta, RobloxPlayerLauncher, Roblox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($ok) { Write-Ok 'Roblox' ("({0:N0}s)" -f $sw.Elapsed.TotalSeconds); Record 'Roblox' 'Installed' $sw.Elapsed.TotalSeconds }
    else     { Write-Fail 'Roblox' 'player not detected after install'; Record 'Roblox' 'Failed' $sw.Elapsed.TotalSeconds }
}

function Install-RAM {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $dir = "$env:ProgramFiles\Roblox Account Manager"
    if ((Test-Path $dir) -and (Get-ChildItem $dir -Filter '*.exe' -ErrorAction SilentlyContinue)) {
        Write-Skip 'Roblox Account Manager' '(already installed)'; Record 'Roblox Account Manager' 'Skipped' 0 'present'; return
    }
    $zip = Join-Path $Dl 'RobloxAccountManager.zip'
    if (-not (Get-File -Url 'https://github.com/ic3w0lf22/Roblox-Account-Manager/releases/latest/download/RobloxAccountManager.zip' -Dest $zip -Label 'Roblox Account Manager' -Ext '.zip')) {
        Write-Fail 'Roblox Account Manager' 'download failed'; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds; return
    }
    try {
        $null = New-Item -ItemType Directory -Force -Path $dir
        Expand-Archive -Path $zip -DestinationPath $dir -Force
        $exe = Get-ChildItem $dir -Recurse -Filter '*.exe' | Select-Object -First 1
        if ($exe) { Write-Ok 'Roblox Account Manager' ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Record 'Roblox Account Manager' 'Installed' $sw.Elapsed.TotalSeconds }
        else { Write-Fail 'Roblox Account Manager' 'no exe in archive'; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds }
    } catch { Write-Fail 'Roblox Account Manager' $_.Exception.Message; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds }
}

# ============================================================================
#  10. Summary
# ============================================================================
function Write-Summary {
    $elapsed = (Get-Date) - $script:StartTime
    $ins = @($script:Results | Where-Object Status -eq 'Installed')
    $skp = @($script:Results | Where-Object Status -eq 'Skipped')
    $fail = @($script:Results | Where-Object Status -eq 'Failed')

    Write-Host ''
    Write-Host '  ╔════════════════════════════════════════════════════╗' -ForegroundColor Cyan
    $head = if ($fail.Count) { "Deployment Complete — $($fail.Count) failed" } else { 'Deployment Complete' }
    Write-Host ('  ║  {0,-49} ║' -f $head) -ForegroundColor White
    Write-Host '  ╚════════════════════════════════════════════════════╝' -ForegroundColor Cyan
    Write-Host ("   Installed {0}   ·   Skipped {1}   ·   Failed {2}" -f $ins.Count, $skp.Count, $fail.Count) -ForegroundColor Gray
    Write-Host ("   Elapsed   {0:hh\:mm\:ss}" -f $elapsed) -ForegroundColor Gray
    Write-Host ("   Logs      {0}" -f $Logs) -ForegroundColor DarkGray
    if ($fail.Count) {
        Write-Host ''
        Write-Host '   Failed operations:' -ForegroundColor Red
        foreach ($x in $fail) { Write-Host ("     ✖ {0}  {1}" -f $x.Name, $x.Detail) -ForegroundColor Red }
    }
    Write-Host ''
    Write-LogFile "[DONE] installed=$($ins.Count) skipped=$($skp.Count) failed=$($fail.Count) elapsed=$($elapsed.ToString('hh\:mm\:ss'))"

    if ($Config.CleanupOnFinish) {
        Remove-Item "$Dl\*", "$Tmp\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($Config.AutoReboot) {
        Write-Host '   AutoReboot enabled — restarting in 30s (Ctrl+C to cancel).' -ForegroundColor Yellow
        shutdown /r /t 30 /c 'Roblox Server Deployment complete.' 2>$null
    }
}

# ============================================================================
#  11. Run
# ============================================================================
try {
    Write-Banner
    Write-LogFile '════ Roblox Server Deployment started ════'
    Invoke-Optimizations
    Invoke-Software
    Write-Summary
}
catch {
    Write-Host "`n   FATAL: $($_.Exception.Message)" -ForegroundColor Red
    Write-LogFile "[FATAL] $($_.Exception.Message)" 'Errors'
}
finally {
    if (-not $Config.AutoReboot) {
        Write-Host '   Press Enter to close…' -ForegroundColor DarkGray
        try { Read-Host | Out-Null } catch { }
    }
}
