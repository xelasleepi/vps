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

**Installs software** (detect → skip if present → silent install → verify):
WinRAR · Visual C++ Redistributables 2005–2022 (x86 + x64) · .NET Framework 4.8 ·
.NET Desktop Runtime 8 · Edge WebView2 · DirectX June 2010 runtime · **Mem Reduct**
(configured to autostart minimized to tray with auto-clean) · **Roblox** ·
**Roblox Account Manager**.

**Every action is logged** to `C:\ProgramData\RobloxDeploy\logs\`:
`install.log`, `errors.log`, `downloads.log`, `optimization.log`, `software.log`.

## Reliability

- **Download manager** — HTTPS with redirect, 3× retry with backoff, inline
  progress + speed, optional SHA-256 verification.
- **Resilient** — every step reports `SUCCESS` / `SKIPPED` / `FAILED` with elapsed
  time; a failure never aborts the run, it's collected and shown in the summary.
- **Idempotent** — re-running skips anything already installed/applied.
- **Smart resume** — every finished step is checkpointed to
  `C:\ProgramData\RobloxDeploy\state.json`. If you close it midway or the box
  reboots, just run the one-liner again: it shows what's already done
  (`↷ … (done in a previous run)`), retries only what failed, and continues.
  Start completely fresh with `$env:DEPLOY_RESET='1'; irm … | iex`.

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
