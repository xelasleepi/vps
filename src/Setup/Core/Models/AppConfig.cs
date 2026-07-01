using System.Text.Json;
using System.Text.Json.Serialization;

namespace Setup.Core.Models;

/// <summary>
/// Strongly-typed view of <c>config.json</c>. Deserialized case-insensitively so
/// the JSON can use camelCase while the C# uses PascalCase. Unknown keys (such
/// as the documentation "//" fields) are ignored.
/// </summary>
public sealed class AppConfig
{
    public bool AutoReboot { get; set; }
    public bool CleanupOnFinish { get; set; } = true;
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Root working directory. Empty ⇒ next to the executable.</summary>
    public string WorkingDirectory { get; set; } = "";

    public FeatureFlags Features { get; set; } = new();
    public DownloadSettings Downloads { get; set; } = new();
    public SoftwareCatalog Software { get; set; } = new();
    public MemReductSettings MemReductSettings { get; set; } = new();

    /// <summary>True when either toggle requests an automatic reboot.</summary>
    [JsonIgnore]
    public bool ShouldReboot => AutoReboot || Features.AutoReboot;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Loads config from <paramref name="path"/>, falling back to defaults on any error.</summary>
    public static AppConfig Load(string path, out string? loadError)
    {
        loadError = null;
        try
        {
            if (!File.Exists(path))
            {
                loadError = $"config.json not found at '{path}', using built-in defaults.";
                return Default();
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg is null)
            {
                loadError = "config.json deserialized to null, using built-in defaults.";
                return Default();
            }
            return cfg;
        }
        catch (Exception ex)
        {
            loadError = $"Failed to parse config.json ({ex.Message}); using built-in defaults.";
            return Default();
        }
    }

    /// <summary>A sane default configuration used when config.json is missing/invalid.</summary>
    public static AppConfig Default() => new()
    {
        Features = new FeatureFlags(),
        Downloads = new DownloadSettings(),
        Software = new SoftwareCatalog(),
        MemReductSettings = new MemReductSettings()
    };
}

/// <summary>Optional-feature toggles mirrored from <c>features</c> in config.json.</summary>
public sealed class FeatureFlags
{
    public bool InstallWinRAR { get; set; } = true;
    public bool InstallVisualCpp { get; set; } = true;
    public bool InstallDotNet { get; set; } = true;
    public bool InstallWebView2 { get; set; } = true;
    public bool InstallDirectX { get; set; } = true;
    public bool InstallMemReduct { get; set; } = true;
    public bool InstallRoblox { get; set; } = true;
    public bool InstallRobloxAccountManager { get; set; } = true;
    public bool OptimizeWindows { get; set; } = true;
    public bool AutoReboot { get; set; }
}

/// <summary>Global download-manager tuning.</summary>
public sealed class DownloadSettings
{
    public int MaxRetries { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 900;
    public bool ResumeIfPossible { get; set; } = true;
    public string UserAgent { get; set; } = "SetupDeployer/1.0";
}

/// <summary>One downloadable/installable payload.</summary>
public sealed class SoftwareItem
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string WingetId { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerFileName { get; set; } = "";
    public string SilentArgs { get; set; } = "";

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasWinget => !string.IsNullOrWhiteSpace(WingetId);
}

/// <summary>The full software catalog (URLs, switches, hashes) from config.json.</summary>
public sealed class SoftwareCatalog
{
    public SoftwareItem WinRar { get; set; } = new();
    public List<SoftwareItem> VisualCppRedistributables { get; set; } = new();
    public SoftwareItem DotNetFramework48 { get; set; } = new();
    public SoftwareItem DotNetDesktopRuntime { get; set; } = new();
    public SoftwareItem WebView2 { get; set; } = new();
    public SoftwareItem DirectX { get; set; } = new();
    public SoftwareItem MemReduct { get; set; } = new();
    public SoftwareItem Roblox { get; set; } = new();
    public SoftwareItem RobloxAccountManager { get; set; } = new();
}

/// <summary>Desired Mem Reduct behavior, applied after install.</summary>
public sealed class MemReductSettings
{
    public bool Autostart { get; set; } = true;
    public bool StartMinimized { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoCleanEnabled { get; set; } = true;
    public int CleanThresholdPercent { get; set; } = 85;
    public int CleanIntervalMinutes { get; set; } = 30;
}
