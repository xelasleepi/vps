using Setup.Core.Abstractions;

namespace Setup.Core.Optimization;

/// <summary>
/// The master list of Windows optimization tasks, assembled in the order the
/// deployment engine should run them when <c>features.OptimizeWindows</c> is
/// enabled. Ordering: services first (fastest wins, frees resources for the rest),
/// then gaming, privacy, power, system and Explorer tweaks, and finally cleanup —
/// cleanup runs last so freed handles from disabled services/scheduled tasks are
/// released before the folders are emptied.
/// </summary>
public static class OptimizationCatalog
{
    /// <summary>
    /// Returns every optimization task in execution order. The list is rebuilt on
    /// each call (tasks are cheap, stateless value objects) so callers may enumerate
    /// it freely.
    /// </summary>
    public static IReadOnlyList<IOptimizationTask> All() => new IOptimizationTask[]
    {
        // 1) Services — disable resource-hungry / unnecessary services first.
        DisableServicesTask.SysMain(),
        DisableServicesTask.WindowsSearch(),
        DisableServicesTask.DeliveryOptimization(),
        DisableServicesTask.XboxServices(),

        // 2) Gaming — Game Bar / Game DVR capture.
        new DisableGameDvrTask(),

        // 3) Privacy — consumer experience, background apps, store updates.
        new DisableConsumerExperienceTask(),
        new DisableBackgroundAppsTask(),
        new DisableAutomaticAppUpdatesTask(),

        // 4) Power — performance plan and always-on / low-latency settings.
        new HighPerformancePowerPlanTask(),
        new DisableSleepAndHibernateTask(),
        new DisableLinkPowerManagementTask(),

        // 5) System — scheduling, visual effects, maintenance/defrag.
        new SystemPerformanceTweaksTask(),

        // 6) Explorer — extensions, hidden files, This PC, no recent/frequent.
        new ExplorerPreferencesTask(),

        // 7) Cleanup — LAST: empty temp folders and the update cache.
        new CleanTempFoldersTask(),
        new CleanWindowsUpdateCacheTask(),
    };
}
