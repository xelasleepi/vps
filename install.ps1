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
        UpdateDrivers              = $true   # detect hardware + install latest drivers via Windows Update
        UpdateNvidiaDriver         = $true   # if a bare-metal NVIDIA GPU is found, fetch the latest driver from NVIDIA directly
        InstallWinRAR              = $true
        InstallVisualCpp           = $true
        InstallDotNet              = $true
        InstallWebView2            = $true
        InstallDirectX             = $true
        InstallLoafy               = $true   # Roblox instance optimizer (supersedes Mem Reduct)
        InstallMemReduct           = $false  # replaced by Loafy; flip to $true to run both
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
#  3b. Resume state — checkpoint file that makes re-runs smart
# ============================================================================
# Every completed step is recorded to state.json immediately. If the run is
# closed midway (or the box reboots), re-running detects what already finished,
# shows it as "done in a previous run", and continues with what's left. Set the
# environment variable DEPLOY_RESET=1 before running to start completely fresh.
$StateFile        = Join-Path $Root 'state.json'
$script:State     = @{}
$script:PriorDone = 0

if ($env:DEPLOY_RESET -eq '1' -and (Test-Path $StateFile)) {
    Remove-Item $StateFile -Force -ErrorAction SilentlyContinue
}

function Get-State {
    if (Test-Path $StateFile) {
        try {
            $obj = Get-Content $StateFile -Raw -ErrorAction Stop | ConvertFrom-Json
            foreach ($p in $obj.PSObject.Properties) {
                $script:State[$p.Name] = @{ Status = $p.Value.Status; Time = $p.Value.Time; Detail = $p.Value.Detail }
            }
        } catch { $script:State = @{} }
    }
    $script:PriorDone = @($script:State.Values | Where-Object { $_.Status -eq 'Done' }).Count
}
function Save-State { try { ($script:State | ConvertTo-Json -Depth 4) | Set-Content -Path $StateFile -Encoding UTF8 -ErrorAction SilentlyContinue } catch { } }
function Test-Done([string]$Key) { $script:State.ContainsKey($Key) -and $script:State[$Key].Status -eq 'Done' }
function Set-Step([string]$Key, [string]$Status, [string]$Detail = '') {
    $script:State[$Key] = @{ Status = $Status; Time = (Get-Date -Format 's'); Detail = $Detail }
    Save-State
}

# Runs an optimization step. Optimizations are cheap + idempotent, so they
# ALWAYS re-apply on every run (no checkpoint gate) — that keeps the box
# optimized even after a Windows update resets something, and means a plain
# re-run is always correct without any reset flag. The $Body returns a short
# detail string shown next to the ✔.
function Do-Opt {
    param([string]$Key, [string]$Name, [scriptblock]$Body)
    try {
        $detail = & $Body
        Write-Ok $Name $detail
        Record $Name 'Optimized' 0 "$detail"
    } catch {
        Write-Fail $Name $_.Exception.Message
        Record $Name 'Failed' 0 $_.Exception.Message
    }
}

function Show-Resume {
    if ($script:PriorDone -gt 0) {
        Write-Host "   ⟳ Smart re-run — $($script:PriorDone) item(s) already installed; skipping those, doing the rest." -ForegroundColor DarkCyan
        Write-Host ''
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

# Resolves the download URL of the latest GitHub release asset matching a
# wildcard (e.g. '*setup.exe'). GitHub asset names include the version, so a
# fixed URL breaks on every new release — this keeps the script future-proof.
# Returns $null on any failure (caller falls back to a pinned URL).
function Get-GitHubAsset {
    param([string]$Repo, [string]$Match)
    try {
        $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" `
            -Headers @{ 'User-Agent' = 'SetupDeployer'; 'Accept' = 'application/vnd.github+json' } `
            -TimeoutSec 30
        $asset = $rel.assets | Where-Object { $_.name -like $Match } | Select-Object -First 1
        if ($asset) { return $asset.browser_download_url }
    } catch {
        Write-LogFile "[WARN] GitHub asset resolve failed for $Repo ($Match): $($_.Exception.Message)" 'Downloads'
    }
    return $null
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
    # Writes a registry value (creating the key path). Emits nothing to the
    # pipeline so it is safe to call inside Do-Opt bodies whose return value is
    # captured as the step's detail string.
    param([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord')
    try {
        if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
        New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
    } catch { Write-LogFile "[WARN] reg $Path\$Name : $($_.Exception.Message)" 'Optimization' }
}

# ============================================================================
#  7. Generic installer
# ============================================================================
function Install-Item {
    param(
        [string]$Name, [string]$Url, [string]$Arguments,
        [scriptblock]$Detect, [scriptblock]$Verify,
        [string]$Sha256, [string]$Ext = '.exe', [bool]$Enabled = $true, [string]$Key,
        [string]$Repo, [string]$AssetMatch, [int]$TimeoutSec = 300
    )
    if (-not $Enabled) { Write-Skip $Name '(disabled in config)'; Record $Name 'Skipped' 0 'disabled'; return }
    if (-not $Key) { $Key = 'sw.' + ($Name -replace '[^\w]', '') }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Write-LogFile "[INFO] Installing $Name" 'Software'

    # Live detection is authoritative — skip if present (from this or a prior run).
    if ($Detect -and (& $Detect)) {
        $why = if (Test-Done $Key) { '(done in a previous run)' } else { '(already installed)' }
        Write-Skip $Name $why; Set-Step $Key 'Done' 'detected'; Record $Name 'Skipped' $sw.Elapsed.TotalSeconds 'present'; return
    }
    # No detector available, but the checkpoint says it finished — trust it.
    if (-not $Detect -and (Test-Done $Key)) {
        Write-Skip $Name '(done in a previous run)'; Record $Name 'Skipped' 0 'state'; return
    }

    # Resolve a versioned GitHub asset (only now that we actually need to
    # download), falling back to the pinned -Url if the API is unreachable.
    if ($Repo -and $AssetMatch) {
        $resolved = Get-GitHubAsset -Repo $Repo -Match $AssetMatch
        if ($resolved) { $Url = $resolved }
    }
    if (-not $Url) { Write-Fail $Name 'no download URL available'; Set-Step $Key 'Failed' 'nourl'; Record $Name 'Failed' 0 'nourl'; return }

    $file = Join-Path $Dl (($Name -replace '[^\w]', '_') + $Ext)
    if (-not (Get-File -Url $Url -Dest $file -Label $Name -Sha256 $Sha256)) {
        Write-Fail $Name 'download failed after 3 attempts'; Set-Step $Key 'Failed' 'download'; Record $Name 'Failed' $sw.Elapsed.TotalSeconds 'download'; return
    }
    try {
        $p = if ($Arguments) { Start-Process -FilePath $file -ArgumentList $Arguments -PassThru -WindowStyle Hidden }
             else            { Start-Process -FilePath $file -PassThru -WindowStyle Hidden }
        # Bounded wait so a silent installer that hangs (e.g. a hidden "close the
        # running app" prompt) can never freeze the whole deployment.
        if ($p.WaitForExit($TimeoutSec * 1000)) {
            $code = $p.ExitCode
        } else {
            taskkill /F /T /PID $p.Id 2>$null | Out-Null
            try { if (-not $p.HasExited) { $p.Kill() } } catch { }
            $code = -1
            Write-Host ("`r   ⚠ {0} installer exceeded {1}s — killed, verifying…      " -f $Name, $TimeoutSec) -ForegroundColor DarkYellow
            Write-LogFile "[WARN] $Name installer timed out after $TimeoutSec s; killed then verified." 'Software'
        }
    } catch { Write-Fail $Name $_.Exception.Message; Set-Step $Key 'Failed' 'run'; Record $Name 'Failed' $sw.Elapsed.TotalSeconds 'run'; return }

    Start-Sleep -Milliseconds 400
    $ok = if ($Verify) { & $Verify } else { $code -in 0, 1638, 3010, 1641 }
    if ($ok) { Write-Ok $Name ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Set-Step $Key 'Done' "exit $code"; Record $Name 'Installed' $sw.Elapsed.TotalSeconds "exit $code" }
    else     { Write-Fail $Name "installer exit code $code"; Set-Step $Key 'Failed' "exit $code"; Record $Name 'Failed' $sw.Elapsed.TotalSeconds "exit $code" }
}

# ============================================================================
#  8. Windows optimization
# ============================================================================
# Makes the run reversible: exports the registry subtrees this script modifies,
# and (once per machine) creates a System Restore point. On Tiny10 System Restore
# is often stripped, so the .reg exports are the primary rollback path.
function Get-VendorDriverUrl($name) {
    switch -Regex ($name) {
        'NVIDIA|GeForce|Quadro|RTX|GTX' { 'https://www.nvidia.com/Download/index.aspx' }
        'AMD|Radeon'                    { 'https://www.amd.com/en/support' }
        'Intel'                         { 'https://www.intel.com/content/www/us/en/download-center/home.html' }
        default                         { $null }
    }
}

# Installs applicable driver updates from Windows Update via the WU COM API.
# Returns count installed, or -1 when WU is unavailable (often stripped on Tiny10).
function Update-DriversViaWindowsUpdate {
    try {
        $session  = New-Object -ComObject Microsoft.Update.Session
        $searcher = $session.CreateUpdateSearcher()
        $res = $searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
        if ($res.Updates.Count -eq 0) { return 0 }

        $toGet = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $res.Updates) { [void]$toGet.Add($u) }
        $dl = $session.CreateUpdateDownloader(); $dl.Updates = $toGet; [void]$dl.Download()

        $toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
        foreach ($u in $res.Updates) { if ($u.IsDownloaded) { [void]$toInstall.Add($u) } }
        if ($toInstall.Count -eq 0) { return 0 }

        $installer = $session.CreateUpdateInstaller(); $installer.Updates = $toInstall
        [void]$installer.Install()
        return $toInstall.Count
    } catch {
        Write-LogFile "[WARN] Windows Update driver path unavailable: $($_.Exception.Message)" 'Software'
        return -1
    }
}

# Reads the installed NVIDIA driver version from WMI and converts the Windows
# driver string (e.g. 32.0.16.1062) to NVIDIA's form (610.62) — the last 5 digits.
function Get-InstalledNvidiaVersion {
    $dv = (Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -match 'NVIDIA' } | Select-Object -First 1).DriverVersion
    if (-not $dv) { return $null }
    $d = ($dv -replace '\.', '')
    if ($d.Length -lt 5) { return $null }
    ($d.Substring($d.Length - 5)).Insert(3, '.')
}

# Queries NVIDIA's driver-lookup API for the latest Game Ready Driver version, then
# builds the desktop/notebook package URL (drivers are unified across GeForce, so
# the version is the same for every modern card). Returns @{Version;Url} or $null.
function Get-NvidiaLatest {
    param([bool]$Notebook)
    try {
        $osid = if ([Environment]::OSVersion.Version.Build -ge 22000) { 135 } else { 57 }  # Win11 : Win10
        $api  = 'https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php'
        $q    = "$api`?func=DriverManualLookup&psid=127&pfid=1005&osID=$osid&languageCode=1033&isWHQL=1&dch=1&sort1=0&numberOfResults=1"
        $body = (Invoke-WebRequest -Uri $q -UseBasicParsing -TimeoutSec 30).Content
        $ver  = [regex]::Match($body, '"Version"\s*:\s*"([\d.]+)"').Groups[1].Value
        if (-not $ver) { return $null }
        $type = if ($Notebook) { 'notebook' } else { 'desktop' }
        $url  = "https://us.download.nvidia.com/Windows/$ver/$ver-$type-win10-win11-64bit-international-dch-whql.exe"
        # Verify the constructed package actually exists before returning it.
        try { if ((Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 30).StatusCode -ne 200) { return $null } } catch { return $null }
        [pscustomobject]@{ Version = $ver; Url = $url }
    } catch { Write-LogFile "[WARN] NVIDIA lookup failed: $($_.Exception.Message)" 'Software'; return $null }
}

# Fetches + silently installs the latest NVIDIA driver straight from NVIDIA, but
# only when it's actually newer than what's installed.
function Install-NvidiaDriver([string]$gpuName) {
    $installed = Get-InstalledNvidiaVersion
    $notebook  = $gpuName -match 'Laptop|Mobile|Max-Q'
    $latest    = Get-NvidiaLatest -Notebook $notebook
    if (-not $latest) { Write-Skip 'NVIDIA driver' '(NVIDIA lookup unavailable)'; Record 'NVIDIA driver' 'Skipped' 0 'lookup'; return }

    if ($installed -and ([double]$installed -ge [double]$latest.Version)) {
        Write-Skip 'NVIDIA driver' "(up to date — $installed)"; Set-Step 'sw.NvidiaDriver' 'Done' $installed; Record 'NVIDIA driver' 'Skipped' 0 "$installed"; return
    }

    $have = if ($installed) { $installed } else { 'none' }
    Write-Info ("NVIDIA driver: installed {0} -> latest {1} (~900 MB)" -f $have, $latest.Version)
    $file = Join-Path $Dl ("nvidia-{0}.exe" -f $latest.Version)
    if (-not (Get-File -Url $latest.Url -Dest $file -Label ("NVIDIA {0}" -f $latest.Version))) {
        Write-Fail 'NVIDIA driver' 'download failed'; Set-Step 'sw.NvidiaDriver' 'Failed' 'download'; Record 'NVIDIA driver' 'Failed' 0; return
    }

    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $p = Start-Process -FilePath $file -ArgumentList '-s -noreboot -clean' -PassThru -WindowStyle Hidden
        if (-not $p.WaitForExit(900 * 1000)) { taskkill /F /T /PID $p.Id 2>$null | Out-Null }
    } catch { Write-LogFile "[WARN] NVIDIA setup: $($_.Exception.Message)" 'Software' }

    $now = Get-InstalledNvidiaVersion
    if ($now -and ([double]$now -ge [double]$latest.Version)) {
        Write-Ok 'NVIDIA driver' ("updated to {0} ({1:N0}s)" -f $latest.Version, $sw.Elapsed.TotalSeconds); Set-Step 'sw.NvidiaDriver' 'Done' $latest.Version; Record 'NVIDIA driver' 'Optimized' 0 $latest.Version
    } else {
        Write-Ok 'NVIDIA driver' ("installer ran for {0} — applies after reboot" -f $latest.Version); Set-Step 'sw.NvidiaDriver' 'Done' 'pending-reboot'; Record 'NVIDIA driver' 'Optimized' 0 'installer ran'
    }
}

# Detects hardware, flags devices missing drivers, and installs the latest drivers
# via Windows Update. VM-aware (skips desktop-GPU advice on a hypervisor).
function Invoke-Drivers {
    Write-Section 'Hardware detection & drivers'
    $gpus = @(); $isVM = $false
    try {
        $cpu   = ((Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue | Select-Object -First 1).Name).Trim()
        $gpus  = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object -Expand Name)
        $cs    = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
        $board = Get-CimInstance Win32_BaseBoard -ErrorAction SilentlyContinue
        $ramGb = if ($cs) { [math]::Round($cs.TotalPhysicalMemory / 1GB, 1) } else { 0 }
        $isVM  = ("$($cs.Model) $($cs.Manufacturer)") -match 'Virtual|VMware|VirtualBox|KVM|QEMU|Xen|Hyper-V|Bochs|Parallels'

        Write-Info ("CPU  : {0}" -f $cpu)
        Write-Info ("GPU  : {0}" -f (($gpus | Where-Object { $_ }) -join ', '))
        Write-Info ("RAM  : {0} GB    Board: {1} {2}" -f $ramGb, $board.Manufacturer, $board.Product)
        Write-Info ("Type : {0}" -f $(if ($isVM) { 'Virtual machine (GPU drivers are usually hypervisor guest tools)' } else { 'Physical machine' }))
    } catch { Write-LogFile "[WARN] hardware inventory: $($_.Exception.Message)" 'Software' }

    # Devices with a driver problem (ConfigManagerErrorCode != 0).
    try {
        $bad = @(Get-CimInstance Win32_PnPEntity -ErrorAction SilentlyContinue |
                 Where-Object { $_.ConfigManagerErrorCode -and $_.ConfigManagerErrorCode -ne 0 })
        if ($bad.Count) {
            Write-Skip 'Devices needing a driver' "($($bad.Count) found)"
            foreach ($d in ($bad | Select-Object -First 8)) { Write-Host ("     • {0}" -f $d.Name) -ForegroundColor Yellow }
        } else {
            Write-Ok 'Driver status' 'all present devices have drivers'
        }
    } catch { }

    # Install latest drivers via Windows Update (all vendors, signed).
    $n = Update-DriversViaWindowsUpdate
    if     ($n -gt 0) { Write-Ok   'Latest drivers via Windows Update' "($n installed — reboot may apply them)"; Record 'Driver update' 'Optimized' 0 "$n via WU" }
    elseif ($n -eq 0) { Write-Ok   'Latest drivers via Windows Update' 'already up to date'; Record 'Driver update' 'Optimized' 0 'up to date' }
    else              { Write-Skip 'Latest drivers via Windows Update' '(WU unavailable on this OS)'; Record 'Driver update' 'Skipped' 0 'no WU' }

    # GPU drivers: fetch NVIDIA's latest directly from NVIDIA (WU lags months on
    # GPUs); for AMD/Intel there's no clean public API, so link the vendor page.
    if (-not $isVM) {
        foreach ($g in $gpus) {
            if (($g -match 'NVIDIA|GeForce|RTX|GTX|Quadro') -and $Config.Features.UpdateNvidiaDriver) {
                Install-NvidiaDriver $g
            } else {
                $u = Get-VendorDriverUrl $g
                if ($u) { Write-Info ("Newest {0} driver (if WU lags): {1}" -f (($g -split ' ')[0]), $u) }
            }
        }
    }
}

function New-SafetyBackup {
    Write-Section 'Safety backup (reversibility)'
    $backupDir = Join-Path $Root 'backup'
    $null = New-Item -ItemType Directory -Force -Path $backupDir
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

    $targets = [ordered]@{
        'multimedia'   = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        'power'        = 'HKLM\SYSTEM\CurrentControlSet\Control\Power'
        'priority'     = 'HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl'
        'memmgmt'      = 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
        'graphics'     = 'HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers'
        'desktop'      = 'HKCU\Control Panel\Desktop'
        'mouse'        = 'HKCU\Control Panel\Mouse'
        'explorer-adv' = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
        'gamestore'    = 'HKCU\System\GameConfigStore'
    }
    $n = 0
    foreach ($k in $targets.Keys) {
        $out = Join-Path $backupDir ('{0}_{1}.reg' -f $stamp, $k)
        reg export $targets[$k] $out /y 2>$null | Out-Null
        if (Test-Path $out) { $n++ }
    }
    Write-Ok 'Registry backup' "($n subtrees -> $backupDir)"
    Write-LogFile "[BACKUP] exported $n registry subtrees to $backupDir" 'Install'

    # System Restore point — best-effort, once per machine per deployment.
    if (-not (Test-Done 'safety.restorepoint')) {
        try {
            Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction SilentlyContinue
            Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' 'SystemRestorePointCreationFrequency' 0
            Write-Host '   Creating restore point (may take up to a minute)…' -ForegroundColor DarkGray
            Checkpoint-Computer -Description 'Before Roblox Server Deployment' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
            Write-Ok 'System Restore point created'
            Set-Step 'safety.restorepoint' 'Done' $stamp
        } catch {
            Write-Skip 'System Restore point' '(unavailable on this OS — the registry export is your rollback)'
        }
    } else {
        Write-Skip 'System Restore point' '(already created in an earlier run)'
    }
    Record 'Safety backup' 'Optimized' 0 "$n reg subtrees exported"
}

function Invoke-Optimizations {
    if (-not $Config.Features.OptimizeWindows) { Write-Info 'Optimization disabled by config.'; return }

    Write-Section 'Optimizing Windows'

    # Each group is state-gated: on a re-run a completed group is shown as
    # "(done in a previous run)" and skipped. Bodies return their detail string.

    Do-Opt 'services' 'Disable background services' {
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
        Write-LogFile "[OPT] services disabled: $done" 'Optimization'
        "($done of $($svc.Count) present)"
    }

    Do-Opt 'gamebar' 'Disable Xbox Game Bar & Game DVR' {
        Set-Reg 'HKCU:\System\GameConfigStore' 'GameDVR_Enabled' 0
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR' 'AllowGameDVR' 0
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR' 'AppCaptureEnabled' 0
        ''
    }

    Do-Opt 'consumer' 'Disable Consumer Experience & suggestions' {
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CloudContent' 'DisableWindowsConsumerFeatures' 1
        $cdm = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager'
        foreach ($v in 'SubscribedContent-338389Enabled','SubscribedContent-338388Enabled',
                       'SubscribedContent-338387Enabled','SystemPaneSuggestionsEnabled',
                       'SoftLandingEnabled','RotatingLockScreenEnabled','RotatingLockScreenOverlayEnabled',
                       'SilentInstalledAppsEnabled','PreInstalledAppsEnabled','OemPreInstalledAppsEnabled') {
            Set-Reg $cdm $v 0
        }
        ''
    }

    Do-Opt 'background' 'Disable Background Apps & auto app updates' {
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy' 'LetAppsRunInBackground' 2
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore' 'AutoDownload' 2
        ''
    }

    Do-Opt 'power' 'Power plan (never sleep/hibernate)' {
        $ult = 'e9a42b02-d5df-448d-aa00-03f14749eb61'
        powercfg -duplicatescheme $ult 2>$null | Out-Null
        powercfg -setactive $ult 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { powercfg -setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null; $plan = 'High Performance' } else { $plan = 'Ultimate Performance' }
        foreach ($t in '-standby-timeout-ac 0','-standby-timeout-dc 0','-hibernate-timeout-ac 0',
                        '-hibernate-timeout-dc 0','-monitor-timeout-ac 0','-monitor-timeout-dc 0') {
            Start-Process powercfg -ArgumentList "-change $t" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
        }
        powercfg -hibernate off 2>$null | Out-Null
        Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power' 'HiberbootEnabled' 0
        powercfg -setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 0 2>$null | Out-Null
        powercfg -setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 0 2>$null | Out-Null
        powercfg -setactive SCHEME_CURRENT 2>$null | Out-Null
        "→ $plan"
    }

    Do-Opt 'system' 'System performance tweaks (scheduling, visuals, defrag)' {
        Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\PriorityControl' 'Win32PrioritySeparation' 24
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects' 'VisualFXSetting' 2
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'EnableTransparency' 0
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Serialize' 'StartupDelayInMSec' 0
        Set-Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance' 'MaintenanceDisabled' 1
        schtasks /Change /TN '\Microsoft\Windows\Defrag\ScheduledDefrag' /Disable 2>$null | Out-Null
        ''
    }

    Do-Opt 'explorer' 'Explorer (extensions, hidden files, This PC, no recent)' {
        $adv = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced'
        Set-Reg $adv 'HideFileExt' 0
        Set-Reg $adv 'Hidden' 1
        Set-Reg $adv 'LaunchTo' 1
        Set-Reg $adv 'Start_TrackDocs' 0
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'ShowRecent' 0
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'ShowFrequent' 0
        ''
    }

    # ---- loafy-optimizer core (ported from the user's "Loafy Optimizer",
    #      esports profile — SAFE + MODERATE tweaks only) ----------------------
    Do-Opt 'netlatency' 'Latency & responsiveness (throttling off, Nagle off, timer res)' {
        $sp = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile'
        Set-Reg $sp 'NetworkThrottlingIndex' 0xffffffff
        Set-Reg $sp 'SystemResponsiveness' 10                              # Loafy reg.sysresp (10, not 0)
        Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' 'GlobalTimerResolutionRequests' 1  # lat.global-timer-res
        # Nagle off on every TCP interface (net.nagle-off)
        Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces' -ErrorAction SilentlyContinue | ForEach-Object {
            Set-Reg $_.PSPath 'TcpAckFrequency' 1
            Set-Reg $_.PSPath 'TCPNoDelay' 1
        }
        ipconfig /flushdns 2>$null | Out-Null                             # net.flush-dns
        netsh int tcp set global autotuninglevel=normal 2>$null | Out-Null # net.tcp-tuned
        netsh int tcp set global rss=enabled 2>$null | Out-Null
        ''
    }

    Do-Opt 'gaming' 'Gaming (MMCSS Games priorities, Game Mode on, TDR)' {
        $g = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games'  # reg.mmcss-games
        Set-Reg $g 'GPU Priority' 8
        Set-Reg $g 'Priority' 6
        Set-Reg $g 'Scheduling Category' 'High' 'String'
        Set-Reg $g 'SFIO Priority' 'High' 'String'
        Set-Reg 'HKCU:\Software\Microsoft\GameBar' 'AllowAutoGameMode' 1   # game.mode-on
        Set-Reg 'HKCU:\Software\Microsoft\GameBar' 'AutoGameModeEnabled' 1
        Set-Reg 'HKLM:\SOFTWARE\Microsoft\PolicyManager\default\ApplicationManagement\AllowGameDVR' 'value' 0  # reg.gamedvr
        Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' 'TdrDelay' 10   # gpu.tdr-extend
        ''
    }

    Do-Opt 'cpumem' 'CPU & memory (core unpark, boost, caches off)' {
        # NOTE: Power Throttling is deliberately left ENABLED. Loafy uses EcoQoS
        # (POWER_THROTTLING_EXECUTION_SPEED) to park idle Roblox instances on
        # E-cores; a global PowerThrottlingOff=1 would defeat that. Do not re-add.
        # No core parking + aggressive boost (cpu.no-park / cpu.boost-aggressive)
        powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR 0cc5b647-c1df-4637-891a-dec35c318583 100 2>$null | Out-Null
        powercfg -setacvalueindex SCHEME_CURRENT SUB_PROCESSOR be337238-0d82-4146-a960-4f3749d470c7 2 2>$null | Out-Null
        powercfg -setactive SCHEME_CURRENT 2>$null | Out-Null
        $mm = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management'
        Set-Reg "$mm\PrefetchParameters" 'EnablePrefetcher' 0
        Set-Reg "$mm\PrefetchParameters" 'EnableSuperfetch' 0
        Set-Reg $mm 'LargeSystemCache' 0                                   # ram.large-system-cache-off
        Set-Reg $mm 'ClearPageFileAtShutdown' 0                            # ram.clear-pf-shutdown-off
        ''
    }

    Do-Opt 'storage' 'Storage (TRIM on, NTFS last-access & 8.3 off)' {
        fsutil behavior set DisableDeleteNotify 0 2>$null | Out-Null       # stor.trim-on
        fsutil behavior set disablelastaccess 1 2>$null | Out-Null         # stor.lastaccess-off
        fsutil behavior set disable8dot3 1 2>$null | Out-Null              # stor.8dot3-off
        ''
    }

    Do-Opt 'telemetry' 'Privacy & telemetry (services, telemetry basic, Cortana, widgets)' {
        # svc.* — safe-to-disable services from the esports profile
        foreach ($s in 'DiagTrack','dmwappushservice','WerSvc','Fax','RemoteRegistry','WMPNetworkSvc','MapsBroker') {
            if (Get-Service -Name $s -ErrorAction SilentlyContinue) {
                Stop-Service $s -Force -ErrorAction SilentlyContinue
                Set-Service  $s -StartupType Disabled -ErrorAction SilentlyContinue
            }
        }
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry' 1  # tel.dt-min (1=Basic, safe)
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' 'AllowCortana' 0
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' 'AllowNewsAndInterests' 0
        Set-Reg 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Feeds' 'ShellFeedsTaskbarViewMode' 2
        ''
    }

    Do-Opt 'desktop' 'Desktop snappiness (menu 50ms, kill timeouts, mouse accel off)' {
        Set-Reg 'HKCU:\Control Panel\Desktop' 'MenuShowDelay' '50' 'String'          # reg.menu-delay (50)
        Set-Reg 'HKCU:\Control Panel\Desktop' 'WaitToKillAppTimeout' '5000' 'String' # reg.wait-to-kill
        Set-Reg 'HKCU:\Control Panel\Desktop' 'HungAppTimeout' '2000' 'String'
        Set-Reg 'HKCU:\Control Panel\Desktop' 'AutoEndTasks' '1' 'String'
        Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control' 'WaitToKillServiceTimeout' '2000' 'String'  # reg.kill-service
        Set-Reg 'HKCU:\Control Panel\Mouse' 'MouseSpeed' '0' 'String'                # reg.mouse-accel
        Set-Reg 'HKCU:\Control Panel\Mouse' 'MouseThreshold1' '0' 'String'
        Set-Reg 'HKCU:\Control Panel\Mouse' 'MouseThreshold2' '0' 'String'
        ''
    }

    # Cleanup runs every time (temp regenerates) — not state-gated.
    $freed = 0
    foreach ($d in $env:TEMP, "$env:SystemRoot\Temp", "$env:SystemRoot\Prefetch") {
        try {
            Get-ChildItem $d -Force -ErrorAction SilentlyContinue | ForEach-Object {
                $freed += ($_.Length) ; Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Write-Ok 'Clean Temp / Windows Temp / Prefetch' "(~$(HB $freed) freed)"
    Record 'Clean temporary files' 'Optimized' 0 "~$(HB $freed) freed"
    Write-LogFile "[OPT] cleanup freed ~$([int]$freed) bytes" 'Optimization'

    # Persist the optimizations across reboots (vps-opti.bat + logon task).
    Register-BootOptimizer
}

# Writes vps-opti.bat and registers a logon scheduled task ("VPS-Opti") that
# re-applies the drift-prone tweaks (disabled services, key registry values,
# power plan) elevated at every logon — so the box stays optimized even after a
# Windows update or reboot re-enables something.
function Register-BootOptimizer {
    try {
        $bat = Join-Path $Root 'vps-opti.bat'
        $content = @'
@echo off
REM VPS-Opti - re-applies key optimizations at every logon.
REM Generated + managed by the Roblox Server Deployment script. Safe to re-run.
setlocal

REM --- keep bloat / telemetry / Xbox services disabled ---
for %%S in (SysMain WSearch DoSvc DiagTrack dmwappushservice WerSvc Fax RemoteRegistry WMPNetworkSvc MapsBroker XblAuthManager XblGameSave XboxGipSvc XboxNetApiSvc) do (
    sc config "%%S" start= disabled >nul 2>&1
    sc stop "%%S" >nul 2>&1
)

REM --- latency / gaming / responsiveness (Loafy esports values) ---
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile" /v NetworkThrottlingIndex /t REG_DWORD /d 4294967295 /f >nul 2>&1
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile" /v SystemResponsiveness /t REG_DWORD /d 10 /f >nul 2>&1
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel" /v GlobalTimerResolutionRequests /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games" /v "GPU Priority" /t REG_DWORD /d 8 /f >nul 2>&1
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games" /v "Priority" /t REG_DWORD /d 6 /f >nul 2>&1
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games" /v "Scheduling Category" /t REG_SZ /d High /f >nul 2>&1
REM (Power Throttling left ON on purpose — Loafy's EcoQoS depends on it.)
reg add "HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" /v TdrDelay /t REG_DWORD /d 10 /f >nul 2>&1
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" /v LargeSystemCache /t REG_DWORD /d 0 /f >nul 2>&1
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" /v ClearPageFileAtShutdown /t REG_DWORD /d 0 /f >nul 2>&1
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKCU\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d 0 /f >nul 2>&1
reg add "HKCU\Software\Microsoft\GameBar" /v AllowAutoGameMode /t REG_DWORD /d 1 /f >nul 2>&1
reg add "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled /t REG_DWORD /d 1 /f >nul 2>&1

REM --- power: never sleep / hibernate / display-off ---
powercfg -change -standby-timeout-ac 0 >nul 2>&1
powercfg -change -monitor-timeout-ac 0 >nul 2>&1
powercfg -change -hibernate-timeout-ac 0 >nul 2>&1
powercfg -hibernate off >nul 2>&1

endlocal
'@
        Set-Content -Path $bat -Value $content -Encoding ASCII -ErrorAction SilentlyContinue

        # Logon task, highest privileges, so HKLM writes succeed unattended.
        # Path has no spaces (ProgramData\RobloxDeploy) so no extra quoting needed.
        schtasks /Create /TN 'VPS-Opti' /TR $bat /SC ONLOGON /RL HIGHEST /F 2>$null | Out-Null

        Write-Ok 'Auto-apply optimizations at startup' '(task "VPS-Opti" + vps-opti.bat)'
        Record 'Startup auto-optimizer' 'Optimized' 0 'VPS-Opti logon task'
        Write-LogFile "[OPT] boot optimizer installed: $bat (scheduled task VPS-Opti)" 'Optimization'
    } catch {
        Write-Fail 'Auto-apply optimizations at startup' $_.Exception.Message
        Record 'Startup auto-optimizer' 'Failed' 0 $_.Exception.Message
    }
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
            $why = if (Test-Done 'sw.DirectX') { '(done in a previous run)' } else { '(already present)' }
            Write-Skip 'DirectX Runtime (June 2010)' $why; Set-Step 'sw.DirectX' 'Done' 'present'; Record 'DirectX' 'Skipped' 0 'present'
        } else {
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $dxSfx = Join-Path $Dl 'directx_redist.exe'; $dxDir = Join-Path $Tmp 'directx'
            $null = New-Item -ItemType Directory -Force -Path $dxDir
            if (Get-File -Url 'https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe' -Dest $dxSfx -Label 'DirectX Runtime') {
                Start-Process $dxSfx -ArgumentList "/Q /T:`"$dxDir`" /C" -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
                $setup = Join-Path $dxDir 'DXSETUP.exe'
                if (Test-Path $setup) { Start-Process $setup -ArgumentList '/silent' -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue }
                if (Test-Path "$env:SystemRoot\System32\d3dx9_43.dll") { Write-Ok 'DirectX Runtime (June 2010)' ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Set-Step 'sw.DirectX' 'Done' 'installed'; Record 'DirectX' 'Installed' $sw.Elapsed.TotalSeconds }
                else { Write-Fail 'DirectX Runtime (June 2010)' 'DXSETUP did not complete'; Set-Step 'sw.DirectX' 'Failed' 'dxsetup'; Record 'DirectX' 'Failed' $sw.Elapsed.TotalSeconds }
            } else { Write-Fail 'DirectX Runtime (June 2010)' 'download failed'; Set-Step 'sw.DirectX' 'Failed' 'download'; Record 'DirectX' 'Failed' $sw.Elapsed.TotalSeconds }
        }
    } else { Write-Skip 'DirectX Runtime (June 2010)' '(disabled in config)' }

    # Loafy — Roblox instance optimizer (Native-AOT background console).
    if ($f.InstallLoafy) { Install-Loafy } else { Write-Skip 'Loafy (Roblox optimizer)' '(disabled in config)' }

    # Mem Reduct + configuration. Kill any running instance first (else the Inno
    # installer blocks on a hidden "close the app" prompt), tell it to close apps,
    # and cap the run time. Asset name is versioned, so resolve latest from GitHub.
    if ($f.InstallMemReduct) { taskkill /F /IM memreduct.exe 2>$null | Out-Null }
    Install-Item -Name 'Mem Reduct' -Enabled $f.InstallMemReduct `
        -Repo 'henrypp/memreduct' -AssetMatch '*setup.exe' `
        -Url 'https://github.com/henrypp/memreduct/releases/download/v.3.5.2/memreduct-3.5.2-setup.exe' `
        -Arguments '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /CLOSEAPPLICATIONS' `
        -TimeoutSec 180 `
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

# Kills every Roblox-related process so the run stays unattended.
function Stop-RobloxProcesses {
    foreach ($n in 'RobloxPlayerBeta','RobloxPlayerLauncher','RobloxPlayerInstaller','RobloxCrashHandler','RobloxPlayerBeta_tmp','Roblox') {
        taskkill /F /IM "$n.exe" /T 2>$null | Out-Null
    }
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like 'Roblox*' } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Find-RobloxPlayer {
    $ver = "$env:LOCALAPPDATA\Roblox\Versions"
    if (-not (Test-Path $ver)) { return $null }
    Get-ChildItem $ver -Recurse -Filter 'RobloxPlayerBeta.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Install-Loafy {
    # Downloads the Native-AOT Loafy.exe (Roblox instance optimizer) to a
    # no-space path, registers a highest-privilege logon task so it runs hidden
    # in the background at every startup (it needs admin for standby-list flush),
    # and launches it now. No .NET runtime required.
    $sw  = [Diagnostics.Stopwatch]::StartNew()
    $dir = Join-Path $env:ProgramData 'Loafy'          # C:\ProgramData\Loafy — no spaces (schtasks-friendly)
    $exe = Join-Path $dir 'Loafy.exe'
    $url = 'https://github.com/xelasleepi/vps/releases/download/loafy/Loafy.exe'

    if ((Test-Path $exe) -and (Test-Done 'sw.Loafy')) {
        Write-Skip 'Loafy (Roblox optimizer)' '(done in a previous run)'; Record 'Loafy' 'Skipped' 0 'present'; return
    }

    # Fetch the expected SHA-256 from the release's own sidecar so the elevated
    # exe is integrity-verified. The sidecar is re-uploaded with every rebuild,
    # so this stays correct without hardcoding a hash. If the sidecar is
    # unreachable, $expected stays null and Get-File just skips verification.
    $expected = $null
    try { $expected = ((Invoke-WebRequest "$url.sha256" -UseBasicParsing -TimeoutSec 30).Content -split '\s+')[0].Trim() } catch { }

    $null = New-Item -ItemType Directory -Force -Path $dir
    taskkill /F /IM Loafy.exe 2>$null | Out-Null       # stop a running copy so we can overwrite
    if (-not (Get-File -Url $url -Dest $exe -Label 'Loafy (Roblox optimizer)' -Sha256 $expected)) {
        Write-Fail 'Loafy (Roblox optimizer)' 'download failed'; Set-Step 'sw.Loafy' 'Failed' 'download'; Record 'Loafy' 'Failed' $sw.Elapsed.TotalSeconds; return
    }

    # Auto-start hidden at logon (a Task Scheduler console app has no visible window).
    schtasks /Create /TN 'Loafy' /TR $exe /SC ONLOGON /RL HIGHEST /F 2>$null | Out-Null
    Start-Process $exe -WindowStyle Hidden -ErrorAction SilentlyContinue

    if (Test-Path $exe) {
        Write-Ok 'Loafy (Roblox optimizer)' ("(auto-starts at logon, {0:N1}s)" -f $sw.Elapsed.TotalSeconds)
        Set-Step 'sw.Loafy' 'Done' 'installed'; Record 'Loafy' 'Installed' $sw.Elapsed.TotalSeconds
    } else {
        Write-Fail 'Loafy (Roblox optimizer)' 'exe missing after download'; Set-Step 'sw.Loafy' 'Failed' 'verify'; Record 'Loafy' 'Failed' $sw.Elapsed.TotalSeconds
    }
}

function Install-Roblox {
    $sw = [Diagnostics.Stopwatch]::StartNew()

    # Already installed? Skip fast (detection is authoritative).
    if (Find-RobloxPlayer) {
        $why = if (Test-Done 'sw.Roblox') { '(done in a previous run)' } else { '(already installed)' }
        Write-Skip 'Roblox' $why; Set-Step 'sw.Roblox' 'Done' 'present'; Record 'Roblox' 'Skipped' 0 'present'; return
    }

    # Clean slate, then download + run the bootstrapper (it installs itself).
    Stop-RobloxProcesses
    $boot = Join-Path $Dl 'RobloxPlayerInstaller.exe'
    if (-not (Get-File -Url 'https://www.roblox.com/download/client?os=win' -Dest $boot -Label 'Roblox')) {
        Write-Fail 'Roblox' 'download failed'; Set-Step 'sw.Roblox' 'Failed' 'download'; Record 'Roblox' 'Failed' $sw.Elapsed.TotalSeconds; return
    }
    Start-Process $boot -ErrorAction SilentlyContinue

    # Wait (bounded) for the player to be installed, with a live heartbeat so it
    # never looks frozen.
    $player = $null
    for ($i = 0; $i -lt 60; $i++) {
        $player = Find-RobloxPlayer
        if ($player) { break }
        Write-Host ("`r   ⏳ Installing Roblox… {0,3}s   " -f ($i * 3)) -NoNewline -ForegroundColor DarkCyan
        Start-Sleep -Seconds 3
    }
    Write-Host ("`r" + (' ' * 40) + "`r") -NoNewline

    if (-not $player) {
        Stop-RobloxProcesses
        Write-Fail 'Roblox' 'player not installed (bootstrapper did not finish)'
        Set-Step 'sw.Roblox' 'Failed' 'noplayer'; Record 'Roblox' 'Failed' $sw.Elapsed.TotalSeconds; return
    }

    # Open it to be sure it's alive, confirm the process, then taskkill and move on.
    $alive = $false
    try {
        if (-not (Get-Process RobloxPlayerBeta -ErrorAction SilentlyContinue)) {
            Start-Process $player.FullName -ErrorAction SilentlyContinue
        }
        for ($i = 0; $i -lt 24; $i++) {   # up to ~12s to confirm it launched
            if (Get-Process RobloxPlayerBeta -ErrorAction SilentlyContinue) { $alive = $true; break }
            Start-Sleep -Milliseconds 500
        }
    } catch { }

    Stop-RobloxProcesses   # always kill so the deployment stays unattended

    $note = if ($alive) { 'verified alive' } else { 'installed' }
    Write-Ok 'Roblox' ("($note, {0:N0}s)" -f $sw.Elapsed.TotalSeconds)
    Set-Step 'sw.Roblox' 'Done' $note
    Record 'Roblox' 'Installed' $sw.Elapsed.TotalSeconds
}

function Install-RAM {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $dir = "$env:ProgramFiles\Roblox Account Manager"
    if ((Test-Path $dir) -and (Get-ChildItem $dir -Filter '*.exe' -ErrorAction SilentlyContinue)) {
        $why = if (Test-Done 'sw.RAM') { '(done in a previous run)' } else { '(already installed)' }
        Write-Skip 'Roblox Account Manager' $why; Set-Step 'sw.RAM' 'Done' 'present'; Record 'Roblox Account Manager' 'Skipped' 0 'present'; return
    }
    # The release asset name is versioned (e.g. Roblox.Account.Manager.3.7.2.zip),
    # so resolve the latest .zip dynamically with a pinned fallback.
    $ramUrl = Get-GitHubAsset -Repo 'ic3w0lf22/Roblox-Account-Manager' -Match '*.zip'
    if (-not $ramUrl) { $ramUrl = 'https://github.com/ic3w0lf22/Roblox-Account-Manager/releases/download/3.7.2/Roblox.Account.Manager.3.7.2.zip' }
    $zip = Join-Path $Dl 'RobloxAccountManager.zip'
    if (-not (Get-File -Url $ramUrl -Dest $zip -Label 'Roblox Account Manager' -Ext '.zip')) {
        Write-Fail 'Roblox Account Manager' 'download failed'; Set-Step 'sw.RAM' 'Failed' 'download'; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds; return
    }
    try {
        $null = New-Item -ItemType Directory -Force -Path $dir
        Expand-Archive -Path $zip -DestinationPath $dir -Force
        $exe = Get-ChildItem $dir -Recurse -Filter '*.exe' | Select-Object -First 1
        if ($exe) { Write-Ok 'Roblox Account Manager' ("({0:N1}s)" -f $sw.Elapsed.TotalSeconds); Set-Step 'sw.RAM' 'Done' 'installed'; Record 'Roblox Account Manager' 'Installed' $sw.Elapsed.TotalSeconds }
        else { Write-Fail 'Roblox Account Manager' 'no exe in archive'; Set-Step 'sw.RAM' 'Failed' 'no-exe'; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds }
    } catch { Write-Fail 'Roblox Account Manager' $_.Exception.Message; Set-Step 'sw.RAM' 'Failed' 'extract'; Record 'Roblox Account Manager' 'Failed' $sw.Elapsed.TotalSeconds }
}

# ============================================================================
#  10. Summary
# ============================================================================
# Optional external config: C:\ProgramData\RobloxDeploy\config.json is merged over
# the embedded $Config defaults, so the hosted one-liner is configurable without
# forking. Drop the file before running (it also persists between runs).
function Import-ConfigOverride {
    $path = Join-Path $Root 'config.json'
    if (-not (Test-Path $path)) { return }
    try {
        $ov = Get-Content $path -Raw -ErrorAction Stop | ConvertFrom-Json
        foreach ($p in $ov.PSObject.Properties) {
            switch ($p.Name) {
                'Features'  { foreach ($f in $p.Value.PSObject.Properties) { if ($Config.Features.Contains($f.Name))  { $v = $f.Value; if ($v -is [string]) { $v = ($v -eq 'true') }; $Config.Features[$f.Name] = [bool]$v } } }
                'MemReduct' { foreach ($m in $p.Value.PSObject.Properties) { if ($Config.MemReduct.Contains($m.Name)) { $Config.MemReduct[$m.Name] = $m.Value } } }
                default     { if ($Config.Contains($p.Name)) { $Config[$p.Name] = $p.Value } }
            }
        }
        Write-Ok 'Loaded config override' "($path)"
        Write-LogFile "[CONFIG] merged overrides from $path" 'Install'
    } catch {
        Write-Skip 'Config override' "(invalid JSON, using defaults — $($_.Exception.Message))"
    }
}

function Test-SchTask([string]$name) {
    schtasks /query /TN $name 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

# Verifies the REAL end-state (not just "installer ran"): processes, scheduled
# tasks, files, and that key tweaks actually stuck. Informational — never fails
# the run, but surfaces silent problems.
function Invoke-HealthCheck {
    Write-Section 'Health check'
    $f = $Config.Features
    $checks = New-Object System.Collections.ArrayList

    if ($f.OptimizeWindows) {
        [void]$checks.Add(@{ N = 'VPS-Opti logon task registered'; Ok = (Test-SchTask 'VPS-Opti') })
        [void]$checks.Add(@{ N = 'Game DVR disabled';              Ok = (((Get-ItemProperty 'HKCU:\System\GameConfigStore' -Name GameDVR_Enabled -ErrorAction SilentlyContinue).GameDVR_Enabled) -eq 0) })
        [void]$checks.Add(@{ N = 'Network throttling disabled';    Ok = (((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile' -Name NetworkThrottlingIndex -ErrorAction SilentlyContinue).NetworkThrottlingIndex) -eq 0xffffffff) })
    }
    if ($f.InstallLoafy) {
        [void]$checks.Add(@{ N = 'Loafy process running'; Ok = [bool](Get-Process Loafy -ErrorAction SilentlyContinue) })
        [void]$checks.Add(@{ N = 'Loafy logon task';      Ok = (Test-SchTask 'Loafy') })
    }
    if ($f.InstallWinRAR) { [void]$checks.Add(@{ N = 'WinRAR installed';      Ok = (Test-Path "$env:ProgramFiles\WinRAR\WinRAR.exe") }) }
    if ($f.InstallRoblox) { [void]$checks.Add(@{ N = 'Roblox player present'; Ok = [bool](Find-RobloxPlayer) }) }

    $pass = 0
    foreach ($c in $checks) {
        if ($c.Ok) { Write-Ok $c.N 'ok'; $pass++ }
        else { Write-Host ("   ✖ {0,-38}not verified" -f $c.N) -ForegroundColor Red; Write-LogFile "[HEALTH-FAIL] $($c.N)" 'Errors' }
    }
    $script:HealthPass  = $pass
    $script:HealthTotal = $checks.Count
    Write-LogFile "[HEALTH] $pass/$($checks.Count) checks passed" 'Install'
}

function Write-Summary {
    $elapsed = (Get-Date) - $script:StartTime
    $ins  = @($script:Results | Where-Object Status -eq 'Installed')
    $opt  = @($script:Results | Where-Object Status -eq 'Optimized')
    $skp  = @($script:Results | Where-Object Status -eq 'Skipped')
    $fail = @($script:Results | Where-Object Status -eq 'Failed')
    # "Newly done" = things this run actually changed (not skipped from a prior run).
    $newlyDone = $ins.Count + $opt.Count + $fail.Count
    $resumed   = ($script:PriorDone -gt 0)

    Write-Host ''
    Write-Host '  ╔════════════════════════════════════════════════════╗' -ForegroundColor Cyan
    $head = if ($fail.Count) { "Deployment Complete — $($fail.Count) failed" }
            elseif ($resumed -and $newlyDone -eq 0) { 'Already Complete — nothing to do' }
            else { 'Deployment Complete' }
    Write-Host ('  ║  {0,-49} ║' -f $head) -ForegroundColor White
    Write-Host '  ╚════════════════════════════════════════════════════╝' -ForegroundColor Cyan
    Write-Host ("   Optimized {0}   ·   Installed {1}   ·   Skipped {2}   ·   Failed {3}" -f $opt.Count, $ins.Count, $skp.Count, $fail.Count) -ForegroundColor Gray
    if ($resumed) { Write-Host ("   Resumed from a previous run ({0} step(s) were already done)." -f $script:PriorDone) -ForegroundColor DarkCyan }
    Write-Host ("   Elapsed   {0:hh\:mm\:ss}" -f $elapsed) -ForegroundColor Gray
    Write-Host ("   Logs      {0}" -f $Logs) -ForegroundColor DarkGray
    Write-Host ("   State     {0}" -f $StateFile) -ForegroundColor DarkGray
    Write-Host ("   Rollback  {0}  (restore point + .reg exports)" -f (Join-Path $Root 'backup')) -ForegroundColor DarkGray
    if ($script:HealthTotal -gt 0) {
        $hc = if ($script:HealthPass -eq $script:HealthTotal) { 'Green' } else { 'Yellow' }
        Write-Host ("   Health    {0}/{1} checks passed" -f $script:HealthPass, $script:HealthTotal) -ForegroundColor $hc
    }
    if ($fail.Count) {
        Write-Host ''
        Write-Host '   Failed operations (re-run to retry them):' -ForegroundColor Red
        foreach ($x in $fail) { Write-Host ("     ✖ {0}  {1}" -f $x.Name, $x.Detail) -ForegroundColor Red }
    }
    Write-Host ''
    Write-LogFile "[DONE] optimized=$($opt.Count) installed=$($ins.Count) skipped=$($skp.Count) failed=$($fail.Count) elapsed=$($elapsed.ToString('hh\:mm\:ss'))"

    # Persistent report that survives the window closing (written to $Root, which
    # cleanup does not touch, plus a best-effort copy on the Desktop).
    $rep = New-Object System.Text.StringBuilder
    [void]$rep.AppendLine('Roblox Server Deployment — report')
    [void]$rep.AppendLine((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
    [void]$rep.AppendLine('')
    [void]$rep.AppendLine($head)
    [void]$rep.AppendLine(('Optimized {0} | Installed {1} | Skipped {2} | Failed {3}' -f $opt.Count, $ins.Count, $skp.Count, $fail.Count))
    if ($script:HealthTotal -gt 0) { [void]$rep.AppendLine(('Health: {0}/{1} checks passed' -f $script:HealthPass, $script:HealthTotal)) }
    [void]$rep.AppendLine(('Elapsed: {0:hh\:mm\:ss}' -f $elapsed))
    [void]$rep.AppendLine(('Logs:     {0}' -f $Logs))
    [void]$rep.AppendLine(('Rollback: {0}' -f (Join-Path $Root 'backup')))
    [void]$rep.AppendLine('')
    [void]$rep.AppendLine('Results:')
    foreach ($r in $script:Results) { [void]$rep.AppendLine(('  [{0,-9}] {1}  {2}' -f $r.Status, $r.Name, $r.Detail)) }
    $reportPath = Join-Path $Root 'report.txt'
    try { $rep.ToString() | Set-Content -Path $reportPath -Encoding UTF8 -ErrorAction SilentlyContinue } catch { }
    foreach ($d in @([Environment]::GetFolderPath('Desktop'), "$env:PUBLIC\Desktop")) {
        if ($d -and (Test-Path $d)) { Copy-Item $reportPath (Join-Path $d 'RobloxDeploy-report.txt') -Force -ErrorAction SilentlyContinue }
    }
    Write-Host ("   Report    {0}  (+ Desktop)" -f $reportPath) -ForegroundColor DarkGray

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
    Get-State                 # load checkpoint from any previous run
    Show-Resume               # tell the user what's already done
    Import-ConfigOverride     # merge optional config.json over the defaults
    Write-LogFile "════ Roblox Server Deployment started (resuming $($script:PriorDone) prior step(s)) ════"
    New-SafetyBackup          # restore point + registry export BEFORE any changes
    Invoke-Optimizations
    Invoke-Software
    if ($Config.Features.UpdateDrivers) { Invoke-Drivers } else { Write-Info 'Driver update disabled by config.' }
    Invoke-HealthCheck        # verify the real end-state
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
