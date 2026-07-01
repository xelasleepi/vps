# Roblox Server Deployment

A **one-line, fully unattended** Windows provisioning tool for a dedicated
**Tiny10 x64 23H2** machine. It runs **natively in PowerShell** — no GUI, no
installer to click, no .NET required — self-elevates, then silently optimizes
Windows and installs the software stack with a clean, colored terminal UI and
full logging. Idempotent and safe to re-run.

## Install (one-liner)

Open **PowerShell** and run:

```powershell
irm https://raw.githubusercontent.com/xelasleepi/vps/main/install.ps1 | iex
```

That's it. If you're not already elevated, it raises a UAC prompt and continues
in an admin window; everything after that is automatic. What you'll see:

```
  ╔════════════════════════════════════════════════════╗
  ║            Roblox Server Deployment                ║
  ╚════════════════════════════════════════════════════╝
   Tiny10 x64 · unattended · silent

  ── Optimizing Windows ────────────────────────────
   ✔ Disable background services           (5 of 7 present)
   ✔ Power plan → Ultimate Performance (never sleep/hibernate)
   ✔ Clean Temp / Windows Temp / Prefetch  (~412.7 MB freed)

  ── Installing Software ───────────────────────────
   ⏬ WinRAR                     3.8 MB   downloaded
   ✔ WinRAR                                (3.2s)
   ↷ Edge WebView2 Runtime                 (already installed)
   ✔ Roblox                                (58s)

  ╔════════════════════════════════════════════════════╗
  ║  Deployment Complete                               ║
  ╚════════════════════════════════════════════════════╝
   Installed 8   ·   Skipped 2   ·   Failed 0
   Elapsed   00:02:37
   Logs      C:\ProgramData\RobloxDeploy\logs
```

Colors: green = success, yellow = skipped, red = failed, cyan = downloads.

## What it does

**Optimizes Windows** — disables SysMain, Windows Search, Delivery Optimization,
Xbox services, Game Bar / Game DVR, Consumer Experience & suggestions, background
apps, automatic Store updates, hibernation, Fast Startup, scheduled maintenance &
defrag; sets the Ultimate/High-Performance power plan with never-sleep and USB/PCIe
power management off; applies background-services scheduling, best-performance
visual effects and Explorer tweaks (show extensions/hidden files, open This PC, no
recent/frequent); cleans Temp, Windows Temp and Prefetch.

**loafy-optimizer core** — ported from the **Loafy Optimizer** "Esports" profile
(SAFE + MODERATE tweaks): `NetworkThrottlingIndex` off, Nagle off + TCP autotune,
`SystemResponsiveness=10`, global timer-resolution request, MMCSS **Games** task
priorities + **Game Mode** on, `TdrDelay`, CPU power-throttling off + core unpark +
aggressive boost, prefetch/superfetch/large-cache/clear-pagefile off, TRIM on +
NTFS last-access & 8.3 off, telemetry to Basic + Cortana/Widgets off and the
esports service set disabled (DiagTrack, dmwappushservice, WER, Fax, RemoteRegistry,
WMPNetworkSvc, MapsBroker), `MenuShowDelay=50`, faster kill-timeouts and mouse
acceleration off. Re-applies **every run** (cheap + idempotent).

> One deliberate deviation from the esports profile: processor scheduling stays on
> **Background Services** (`Win32PrioritySeparation=24`) instead of the profile's
> foreground `0x26`, because Roblox Account Manager runs **many** instances rather
> than one foreground game — background scheduling shares CPU better across them.

**Stays optimized after reboots** — writes `vps-opti.bat` and registers a
highest-privilege **logon scheduled task `VPS-Opti`** that re-asserts the
drift-prone tweaks (disabled services, key registry values, power plan) at every
startup, so a Windows update can't quietly undo them.

**Installs software** (detect → skip if present → silent install → verify):
WinRAR · Visual C++ Redistributables 2005–2022 (x86 + x64) · .NET Framework 4.8 ·
.NET Desktop Runtime 8 · Edge WebView2 · DirectX June 2010 runtime · **Loafy** ·
**Roblox** · **Roblox Account Manager**.

**Loafy** (the Roblox instance optimizer) is downloaded as a 2.1 MB Native-AOT
`Loafy.exe` (no .NET runtime needed) to `C:\ProgramData\Loafy` and registered as
a highest-privilege **logon task** that runs hidden in the background — it
continuously classifies each `RobloxPlayerBeta.exe` and applies per-instance CPU
affinity, EcoQoS, working-set caps, memory trimming and GPU priority. It replaces
Mem Reduct here (smarter + Roblox-aware); set `InstallMemReduct = $true` in
`$Config` if you want the generic RAM cleaner too. Loafy is built fresh from
`C:\Users\loaf\loafy` (Native AOT) for each release.

**Hardware & drivers** — detects CPU / GPU(s) / board / RAM, flags any device
missing a driver (`ConfigManagerErrorCode`), and installs the latest **signed**
drivers via Windows Update (covers GPU/NIC/chipset). VM-aware: on a hypervisor it
skips desktop-GPU advice (those are guest tools). Toggle with `UpdateDrivers`.

For a **bare-metal NVIDIA GPU** it goes further and fetches the latest **Game Ready
Driver straight from NVIDIA** (`UpdateNvidiaDriver`): it queries NVIDIA's driver API
for the current version, compares it to the installed version (only downloading the
~900 MB package when it's actually newer), builds the correct desktop/notebook DCH
URL, and silent-installs (`-s -noreboot -clean`). AMD/Intel have no clean public
driver API, so those stay on Windows Update + a link to the vendor page.

**Every action is logged** to `C:\ProgramData\RobloxDeploy\logs\`:
`install.log`, `errors.log`, `downloads.log`, `optimization.log`, `software.log`.

## Reliability

- **Download manager** — HTTPS with redirect, 3× retry with backoff, inline
  progress + speed, optional SHA-256 verification.
- **Resilient** — every step reports `SUCCESS` / `SKIPPED` / `FAILED` with elapsed
  time; a failure never aborts the run, it's collected and shown in the summary.
- **Smart re-run (no flags needed)** — just run the one-liner again anytime.
  Installed software is detected and skipped (`↷ … already installed`), only
  failures are retried, and installs are checkpointed to
  `C:\ProgramData\RobloxDeploy\state.json` so a mid-run close/reboot resumes
  cleanly. Optimizations simply re-apply (they're instant), so the box is always
  fully tuned. There's an escape hatch — `$env:DEPLOY_RESET='1'` forces installs
  to re-download — but you rarely need it.
- **Roblox verify** — installs Roblox, launches it to confirm the player process
  is actually alive, then force-kills it (`taskkill /F`) and continues, so the
  run stays unattended and never hangs.
- **Reversible** — before touching anything it exports the registry subtrees it
  will modify to `C:\ProgramData\RobloxDeploy\backup\*.reg` and (once, where
  supported) creates a System Restore point. Undo a tweak by double-clicking the
  matching `.reg`.
- **Integrity-checked exe** — the elevated `Loafy.exe` is verified against a
  SHA-256 sidecar published with the release, so a corrupted/tampered download is
  rejected.
- **Timeout-guarded installs** — every installer runs with a hard timeout; a
  silent installer that hangs is killed and verified, never freezing the run.
- **Plays nice with Loafy** — the optimizer deliberately leaves Windows Power
  Throttling **on** so Loafy's EcoQoS can still park idle Roblox instances.

## Configuration

Defaults are embedded in [`install.ps1`](install.ps1) under `$Config` — flip any
feature off, change the Mem Reduct thresholds, or set `AutoReboot = $true`:

```powershell
$Config = @{
    AutoReboot      = $false
    CleanupOnFinish = $true
    Features = @{
        OptimizeWindows = $true
        InstallWinRAR   = $true
        InstallVisualCpp = $true
        InstallDotNet   = $true
        InstallWebView2 = $true
        InstallDirectX  = $true
        InstallMemReduct = $true
        InstallRoblox   = $true
        InstallRobloxAccountManager = $true
    }
}
```

To customize the hosted one-liner, edit `install.ps1` and push — the raw URL always
serves `main`.

## Notes

- Runs elevated (required to change services / power / registry and install
  software). The single UAC prompt is the only interaction.
- **Roblox** installs per-user into the elevated account's profile; the script
  waits for `RobloxPlayerBeta.exe` then closes the auto-launched client.
- Services already removed by Tiny10 report **SKIPPED**, not failed.
- Evergreen download URLs (VC++ `aka.ms`, WebView2) ship without pinned hashes
  since their bytes change; add a `-Sha256` to `Install-Item` calls to pin.

---

### Also in this repo: a C# / WPF edition

`src/Setup/` contains an alternative implementation of the same deployment as a
.NET 8 WPF app (`Setup.exe`) with a windowed terminal-style UI. It's fully built
and tested, but the **PowerShell one-liner above is the recommended way to run
the deployment** on Tiny10. Build it with `dotnet build RobloxDeploy.sln -c Release`
if you want the GUI edition.
