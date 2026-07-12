using System.Diagnostics;
using Setup.Core.Abstractions;
using Setup.Core.Models;
using Setup.Core.Utils;

namespace Setup.Core.Deployment;

/// <summary>
/// The concrete deployment orchestrator. Runs optimization tasks then installers
/// in order, tracking overall progress, recording every outcome, and never
/// aborting on a single failure (failures are collected and reported at the end).
/// </summary>
public sealed class DeploymentEngine : IDeploymentEngine
{
    private readonly DeploymentContext _ctx;
    private readonly IReadOnlyList<IInstaller> _installers;
    private readonly IReadOnlyList<IOptimizationTask> _optimizations;

    public DeploymentEngine(
        DeploymentContext context,
        IReadOnlyList<IInstaller> installers,
        IReadOnlyList<IOptimizationTask> optimizations)
    {
        _ctx = context;
        _installers = installers;
        _optimizations = optimizations;
    }

    public async Task<DeploymentSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        var log = _ctx.Logger;
        var reporter = _ctx.Reporter;
        var config = _ctx.Config;
        var summary = new DeploymentSummary { LogDirectory = _ctx.Directories.Logs };
        var overall = Stopwatch.StartNew();

        // ---- Plan / progress accounting ---------------------------------
        bool optimize = config.Features.OptimizeWindows;
        int optSteps = optimize ? _optimizations.Count : 0;
        var enabledInstallers = _installers.Where(i => i.IsEnabled(config)).ToList();
        int totalSteps = Math.Max(1, optSteps + enabledInstallers.Count);
        int completed = 0;

        void Advance()
        {
            completed++;
            reporter.SetOverallProgress(Math.Min(100.0, completed * 100.0 / totalSteps));
        }

        reporter.SetPhase(DeploymentPhase.Initializing);
        reporter.SetOverallProgress(0);
        log.Info("════════════════════════════════════════════════════════════", LogCategory.Install);
        log.Info("Roblox Server Deployment — starting unattended run.", LogCategory.Install);
        log.Info($"Working directory : {_ctx.Directories.Root}", LogCategory.Install);
        log.Info($"Administrator     : {AdminHelper.IsAdministrator()}", LogCategory.Install);
        log.Info($"Optimize Windows  : {optimize}", LogCategory.Install);
        log.Info($"Installers enabled: {enabledInstallers.Count} / {_installers.Count}", LogCategory.Install);
        log.Info("════════════════════════════════════════════════════════════", LogCategory.Install);

        // ---- Phase 1: Windows optimization ------------------------------
        if (optimize)
        {
            reporter.SetPhase(DeploymentPhase.Optimizing);
            log.Info("Applying Windows optimizations…", LogCategory.Optimization);

            foreach (var task in _optimizations)
            {
                if (cancellationToken.IsCancellationRequested) break;
                reporter.SetCurrentTask(task.Name);
                OperationResult result;
                try
                {
                    result = await task.ApplyAsync(_ctx, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = OperationResult.Failed(task.Name, TimeSpan.Zero, ex.Message, ex);
                    log.Error($"Optimization '{task.Name}' threw: {ex.Message}", ex, LogCategory.Optimization);
                }

                summary.Record(result, isOptimization: true);
                LogResult(result, LogCategory.Optimization);
                Advance();
            }

            log.Success($"Optimization complete: {summary.Optimizations.Count(o => o.IsSuccess)} applied, " +
                        $"{summary.Optimizations.Count(o => o.IsSkipped)} skipped, " +
                        $"{summary.Optimizations.Count(o => o.IsFailure)} failed.", LogCategory.Optimization);
        }
        else
        {
            log.Warning("Windows optimization disabled by configuration — skipping.", LogCategory.Optimization);
        }

        // ---- Phase 2: Software installation -----------------------------
        reporter.SetPhase(DeploymentPhase.Installing);
        log.Info("Installing software…", LogCategory.Software);

        foreach (var installer in _installers)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (!installer.IsEnabled(config))
            {
                log.Warning($"{installer.Name}: disabled by configuration — skipping.", LogCategory.Software);
                continue; // not counted in progress
            }

            reporter.SetCurrentTask($"Installing {installer.Name}…");
            reporter.SetCurrentFile(null);
            OperationResult result;
            try
            {
                result = await installer.InstallAsync(_ctx, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = OperationResult.Failed(installer.Name, TimeSpan.Zero, ex.Message, ex);
                log.Error($"Installer '{installer.Name}' threw: {ex.Message}", ex, LogCategory.Software);
            }

            summary.Record(result);
            LogResult(result, LogCategory.Software);
            Advance();
        }

        reporter.SetCurrentFile(null);

        // ---- Phase 3: Cleanup -------------------------------------------
        reporter.SetPhase(DeploymentPhase.CleaningUp);
        if (config.CleanupOnFinish)
        {
            reporter.SetCurrentTask("Cleaning up temporary files…");
            log.Info("Cleaning working temp/downloads…", LogCategory.Install);
            long freed = 0;
            freed += FileSystemUtil.TryEmptyDirectory(_ctx.Directories.Temp, out _);
            freed += FileSystemUtil.TryEmptyDirectory(_ctx.Directories.Downloads, out _);
            log.Success($"Cleanup freed {FileSystemUtil.HumanBytes(freed)} of working space.", LogCategory.Install);
        }
        else
        {
            log.Info("CleanupOnFinish disabled — leaving downloads/temp in place.", LogCategory.Install);
        }

        // ---- Finish -----------------------------------------------------
        reporter.SetOverallProgress(100);
        reporter.SetPhase(DeploymentPhase.Complete);
        reporter.SetCurrentTask("Deployment complete.");
        overall.Stop();
        summary.TotalElapsed = overall.Elapsed;

        log.Info("════════════════════════════════════════════════════════════", LogCategory.Install);
        log.Success($"Deployment finished in {summary.TotalElapsed:hh\\:mm\\:ss}. " +
                    $"Installed {summary.Installed.Count}, skipped {summary.Skipped.Count}, failed {summary.Failed.Count}.",
                    LogCategory.Install);

        // ---- Optional reboot --------------------------------------------
        if (config.ShouldReboot && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                summary.RebootScheduled = true;
                log.Warning("AutoReboot enabled — system will restart in 30 seconds.", LogCategory.Install);
                await _ctx.Process.RunAsync(
                    "shutdown",
                    "/r /t 30 /c \"Roblox Server Deployment complete — restarting.\"",
                    timeoutSeconds: 30,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log.Error($"Failed to schedule reboot: {ex.Message}", ex, LogCategory.Install);
            }
        }

        log.Flush();
        return summary;
    }

    private void LogResult(OperationResult result, LogCategory category)
    {
        switch (result.Status)
        {
            case OperationStatus.Success:
                _ctx.Logger.Success(result.StatusLine(), category);
                break;
            case OperationStatus.Skipped:
                _ctx.Logger.Warning(result.StatusLine(), category);
                break;
            case OperationStatus.Failed:
                _ctx.Logger.Error(result.StatusLine(), result.Error, LogCategory.Errors);
                _ctx.Logger.Log(LogLevel.Error, result.StatusLine(), category);
                break;
            default:
                _ctx.Logger.Info(result.StatusLine(), category);
                break;
        }
    }
}
