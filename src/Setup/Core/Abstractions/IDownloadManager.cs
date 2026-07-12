using Setup.Core.Models;

namespace Setup.Core.Abstractions;

/// <summary>
/// Reusable HTTPS download manager: retry logic, optional HTTP-range resume,
/// SHA-256 verification, progress and speed reporting, and per-attempt timeouts.
/// </summary>
public interface IDownloadManager
{
    /// <summary>
    /// Downloads <see cref="DownloadRequest.Url"/> to
    /// <see cref="DownloadRequest.DestinationPath"/>, retrying up to
    /// <see cref="DownloadRequest.MaxRetries"/> times. Never throws for expected
    /// network failures — inspect <see cref="DownloadResult.Success"/> instead.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
