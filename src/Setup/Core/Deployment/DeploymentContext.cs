using Setup.Core.Abstractions;
using Setup.Core.Models;

namespace Setup.Core.Deployment;

/// <summary>
/// The shared services and state handed to every installer and optimization
/// task. This is the single dependency object flowed through the whole run, so
/// components stay decoupled and unit-testable.
/// </summary>
public sealed class DeploymentContext
{
    public required AppConfig Config { get; init; }
    public required ILogger Logger { get; init; }
    public required IDownloadManager Downloader { get; init; }
    public required IProcessRunner Process { get; init; }
    public required IProgressReporter Reporter { get; init; }
    public required WorkingDirectories Directories { get; init; }

    /// <summary>Builds a <see cref="DownloadRequest"/> for a catalog item using global download settings.</summary>
    public DownloadRequest BuildDownload(SoftwareItem item)
    {
        var fileName = string.IsNullOrWhiteSpace(item.InstallerFileName)
            ? SafeFileName(item.Name)
            : item.InstallerFileName;

        return new DownloadRequest
        {
            Url = item.Url,
            DestinationPath = Path.Combine(Directories.Downloads, fileName),
            DisplayName = item.Name,
            Sha256 = string.IsNullOrWhiteSpace(item.Sha256) ? null : item.Sha256,
            MaxRetries = Config.Downloads.MaxRetries,
            TimeoutSeconds = Config.Downloads.TimeoutSeconds,
            ResumeIfPossible = Config.Downloads.ResumeIfPossible,
            UserAgent = Config.Downloads.UserAgent
        };
    }

    private static string SafeFileName(string name)
    {
        var cleaned = string.Concat(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        cleaned = cleaned.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "installer.exe" : cleaned + ".exe";
    }
}
