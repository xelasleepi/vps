using Setup.Core.Abstractions;

namespace Setup.Core.Installers;

/// <summary>
/// Central registry of every software installer, in deployment order. Runtimes
/// and prerequisites are installed first (so applications that depend on them
/// succeed), with the end-user applications last.
/// </summary>
public static class InstallerCatalog
{
    /// <summary>
    /// Returns all installers in the canonical install order. The deployment
    /// engine iterates this list, honoring each installer's
    /// <see cref="IInstaller.IsEnabled"/> flag.
    /// </summary>
    public static IReadOnlyList<IInstaller> All() => new IInstaller[]
    {
        // Runtimes / prerequisites first.
        new WinRarInstaller(),
        new VisualCppInstaller(),
        new DotNetFrameworkInstaller(),
        new DotNetDesktopRuntimeInstaller(),
        new WebView2Installer(),
        new DirectXInstaller(),
        // Applications last.
        new MemReductInstaller(),
        new RobloxInstaller(),
        new RobloxAccountManagerInstaller()
    };
}
