namespace Setup.Core.Models;

/// <summary>Describes a file to download. Consumed by the download manager.</summary>
public sealed class DownloadRequest
{
    /// <summary>Source HTTPS URL (redirects are followed).</summary>
    public required string Url { get; init; }

    /// <summary>Absolute path the file is written to.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Friendly name used in progress/log output (defaults to the file name).</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Optional expected SHA-256 (hex). When set, the file is verified after download.</summary>
    public string? Sha256 { get; init; }

    /// <summary>Number of download attempts before giving up.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Per-attempt timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 900;

    /// <summary>Attempt HTTP range resume when a partial file already exists.</summary>
    public bool ResumeIfPossible { get; init; } = true;

    /// <summary>Optional User-Agent header.</summary>
    public string? UserAgent { get; init; }

    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Path.GetFileName(DestinationPath) : DisplayName;
}

/// <summary>Snapshot of download progress, reported via <see cref="IProgress{T}"/>.</summary>
public sealed class DownloadProgress
{
    public string FileName { get; init; } = "";
    public long BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan Elapsed { get; init; }

    /// <summary>0–100, or 0 when the total size is unknown.</summary>
    public double Percent => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value * 100.0, 0, 100)
        : 0;

    /// <summary>Estimated time remaining, or null when unknown.</summary>
    public TimeSpan? Eta
    {
        get
        {
            if (TotalBytes is not > 0 || SpeedBytesPerSecond <= 1) return null;
            var remaining = TotalBytes.Value - BytesReceived;
            if (remaining <= 0) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(remaining / SpeedBytesPerSecond);
        }
    }
}

/// <summary>Outcome of a download attempt sequence.</summary>
public sealed class DownloadResult
{
    public bool Success { get; init; }
    public string FilePath { get; init; } = "";
    public string? ErrorMessage { get; init; }
    public int Attempts { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long BytesDownloaded { get; init; }

    /// <summary>SHA-256 of the downloaded file (hex), when computed.</summary>
    public string? ComputedSha256 { get; init; }

    /// <summary>True when a hash was requested and matched.</summary>
    public bool HashVerified { get; init; }

    public static DownloadResult Ok(string path, int attempts, TimeSpan elapsed, long bytes, string? sha, bool verified)
        => new() { Success = true, FilePath = path, Attempts = attempts, Elapsed = elapsed, BytesDownloaded = bytes, ComputedSha256 = sha, HashVerified = verified };

    public static DownloadResult Fail(string path, int attempts, TimeSpan elapsed, string error)
        => new() { Success = false, FilePath = path, Attempts = attempts, Elapsed = elapsed, ErrorMessage = error };
}
