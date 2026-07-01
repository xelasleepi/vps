using System.Windows;
using System.Windows.Threading;
using Setup.Core.Deployment;
using Setup.Core.Installers;
using Setup.Core.Models;
using Setup.Core.Optimization;
using Setup.Core.Services;
using Setup.Core.Utils;
using Setup.UI.ViewModels;

namespace Setup;

/// <summary>
/// Application entry point and composition root. Wires configuration, logging,
/// the download manager, the process runner, the view-model (which is also the
/// progress reporter) and the deployment engine, then launches the run.
/// </summary>
public partial class App : Application
{
    private Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global safety nets — this tool must never surface an unhandled crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // ---- Configuration ---------------------------------------------
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var config = AppConfig.Load(configPath, out var configError);

        // ---- Working directories + logging ------------------------------
        var dirs = WorkingDirectories.Create(config.WorkingDirectory);
        var logger = new Logger(dirs);
        _logger = logger;

        if (configError is not null)
            logger.Warning(configError);
        else
            logger.Info($"Loaded configuration from {configPath}.");

        if (!AdminHelper.IsAdministrator())
            logger.Error("Not running as Administrator — some operations will fail. " +
                         "Re-launch elevated for a complete deployment.");

        // ---- Core services ----------------------------------------------
        var downloader = new DownloadManager(logger);
        var process = new ProcessRunner(logger);

        // ---- View-model + window ----------------------------------------
        var vm = new MainViewModel(Dispatcher);
        vm.AttachLogger(logger);
        vm.IsRunning = true;
        vm.StartClock();

        var context = new DeploymentContext
        {
            Config = config,
            Logger = logger,
            Downloader = downloader,
            Process = process,
            Reporter = vm,
            Directories = dirs
        };

        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        // ---- Kick off the deployment (background) -----------------------
        var engine = new DeploymentEngine(context, InstallerCatalog.All(), OptimizationCatalog.All());
        _ = RunDeploymentAsync(engine, vm, logger);
    }

    private static async Task RunDeploymentAsync(DeploymentEngine engine, MainViewModel vm, Logger logger)
    {
        try
        {
            var summary = await Task.Run(() => engine.RunAsync()).ConfigureAwait(true);
            vm.ShowSummary(summary);
        }
        catch (Exception ex)
        {
            logger.Error($"Deployment failed unexpectedly: {ex.Message}", ex);
            vm.SetFatalError($"Deployment failed: {ex.Message}");
        }
        finally
        {
            logger.Flush();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error($"Unhandled UI exception: {e.Exception.Message}", e.Exception);
        e.Handled = true; // keep the app alive so the user can read the log
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _logger?.Error($"Unhandled exception: {ex.Message}", ex);
        _logger?.Flush();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Flush();
        base.OnExit(e);
    }
}
