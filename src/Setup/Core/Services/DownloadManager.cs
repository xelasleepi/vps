using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using Setup.Core.Abstractions;
using Setup.Core.Models;

namespace Setup.Core.Services;

/// <summary>
/// HTTPS download manager with retry logic, optional HTTP-range resume,
/// SHA-256 verification and throttled progress/speed reporting. Downloads are
/// written to a <c>.part</c> sidecar and atomically moved into place on success.
/// Expected network/HTTP failures never escape as exceptions; inspect
/// <see cref="DownloadResult.Success"/> instead.
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    /// <summary>Copy buffer size for streaming the response body.</summary>
    private const int BufferSize = 81920;

    /// <summary>Minimum interval between progress reports.</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Single shared client for the whole process. Timeout is disabled here and
    /// enforced per-attempt via a linked <see cref="CancellationTokenSource"/>.
    /// </summary>
    private static readonly HttpClient SharedClient = CreateClient();

    private readonly ILogger _logger;

    /// <summary>Creates the download manager.</summary>
    /// <param name="logger">Logger used for the Downloads and Errors channels.</param>
    public DownloadManager(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc />
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var overall = Stopwatch.StartNew();
        var name = request.EffectiveDisplayName;
        var maxRetries = Math.Max(1, request.MaxRetries);
        string lastError = "unknown error";
        int attempt = 0;

        // Idempotent short-circuit: an existing final file whose hash already
        // matches needs no work.
        if (!string.IsNullOrWhiteSpace(request.Sha256) && File.Exists(request.DestinationPath))
        {
            try
            {
                var existingHash = await ComputeSha256Async(request.DestinationPath, cancellationToken).ConfigureAwait(false);
                if (HashMatches(existingHash, request.Sha256))
                {
                    _logger.Download($"{name}: already present with matching SHA-256, skipping download.");
                    var len = new FileInfo(request.DestinationPath).Length;
                    return DownloadResult.Ok(request.DestinationPath, 0, overall.Elapsed, len, existingHash, verified: true);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DownloadResult.Fail(request.DestinationPath, 0, overall.Elapsed, "Download cancelled.");
            }
            catch
            {
                // Fall through to a normal download attempt.
            }
        }

        for (attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
                return DownloadResult.Fail(request.DestinationPath, attempt - 1, overall.Elapsed, "Download cancelled.");

            if (attempt > 1)
            {
                var backoff = TimeSpan.FromSeconds(2 * (attempt - 1));
                _logger.Download($"{name}: retrying in {backoff.TotalSeconds:0}s (attempt {attempt}/{maxRetries}).");
                try
                {
                    await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return DownloadResult.Fail(request.DestinationPath, attempt - 1, overall.Elapsed, "Download cancelled.");
                }
            }

            _logger.Download($"{name}: attempt {attempt}/{maxRetries} — {request.Url}");

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, request.TimeoutSeconds)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var result = await DownloadOnceAsync(request, name, attempt, overall, progress, linked.Token).ConfigureAwait(false);
                if (result.Success)
                {
                    _logger.Download($"{name}: completed in {result.Elapsed.TotalSeconds:0.0}s ({FormatBytes(result.BytesDownloaded)}).");
                    return result;
                }

                lastError = result.ErrorMessage ?? "download failed";
                _logger.Download($"{name}: attempt {attempt} failed — {lastError}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.Download($"{name}: cancelled.");
                return DownloadResult.Fail(request.DestinationPath, attempt, overall.Elapsed, "Download cancelled.");
            }
            catch (OperationCanceledException)
            {
                // Per-attempt timeout tripped.
                lastError = $"timed out after {request.TimeoutSeconds}s";
                _logger.Download($"{name}: attempt {attempt} {lastError}.");
                _logger.Error($"{name}: download attempt {attempt} {lastError}.");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.Download($"{name}: attempt {attempt} error — {ex.Message}");
                _logger.Error($"{name}: download attempt {attempt} error.", ex);
            }
        }

        _logger.Error($"{name}: download failed after {maxRetries} attempt(s) — {lastError}");
        return DownloadResult.Fail(request.DestinationPath, maxRetries, overall.Elapsed, lastError);
    }

    /// <summary>Performs a single download attempt (throws on failure to trigger retry).</summary>
    private async Task<DownloadResult> DownloadOnceAsync(
        DownloadRequest request,
        string name,
        int attempt,
        Stopwatch overall,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var partPath = request.DestinationPath + ".part";

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);

        long existingLength = 0;
        bool resuming = false;
        if (request.ResumeIfPossible && File.Exists(partPath))
        {
            try
            {
                existingLength = new FileInfo(partPath).Length;
                resuming = existingLength > 0;
            }
            catch
            {
                existingLength = 0;
                resuming = false;
            }
        }
        else if (File.Exists(partPath))
        {
            // Resume not desired — start clean.
            TryDelete(partPath);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.Url);
        if (!string.IsNullOrWhiteSpace(request.UserAgent))
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
        if (resuming)
            httpRequest.Headers.Range = new RangeHeaderValue(existingLength, null);

        using var response = await SharedClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // Server ignored the range header (returned 200): restart from scratch.
        if (resuming && response.StatusCode == HttpStatusCode.OK)
        {
            _logger.Download($"{name}: server does not support resume, restarting from 0.");
            resuming = false;
            existingLength = 0;
            TryDelete(partPath);
        }
        else if (resuming && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file is already the full length (or stale) — restart.
            _logger.Download($"{name}: range not satisfiable, restarting from 0.");
            resuming = false;
            existingLength = 0;
            TryDelete(partPath);

            using var retryReq = new HttpRequestMessage(HttpMethod.Get, request.Url);
            if (!string.IsNullOrWhiteSpace(request.UserAgent))
                retryReq.Headers.TryAddWithoutValidation("User-Agent", request.UserAgent);
            using var retryResp = await SharedClient
                .SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            retryResp.EnsureSuccessStatusCode();
            return await StreamToFileAsync(request, name, attempt, overall, progress, retryResp, partPath, 0, ct)
                .ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return await StreamToFileAsync(request, name, attempt, overall, progress, response, partPath, existingLength, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Streams the response body into the part file, then verifies and finalizes.</summary>
    private async Task<DownloadResult> StreamToFileAsync(
        DownloadRequest request,
        string name,
        int attempt,
        Stopwatch overall,
        IProgress<DownloadProgress>? progress,
        HttpResponseMessage response,
        string partPath,
        long startOffset,
        CancellationToken ct)
    {
        long? totalBytes = response.Content.Headers.ContentLength is { } len
            ? len + startOffset
            : null;

        long received = startOffset;
        var buffer = new byte[BufferSize];

        var swReport = Stopwatch.StartNew();
        long bytesSinceTick = 0;
        var lastReportElapsed = TimeSpan.Zero;

        var append = startOffset > 0;
        var mode = append ? FileMode.Append : FileMode.Create;

        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            int read;
            while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                received += read;
                bytesSinceTick += read;

                var elapsed = swReport.Elapsed;
                if (elapsed - lastReportElapsed >= ProgressInterval)
                {
                    var intervalSeconds = (elapsed - lastReportElapsed).TotalSeconds;
                    var speed = intervalSeconds > 0 ? bytesSinceTick / intervalSeconds : 0;
                    ReportProgress(progress, name, received, totalBytes, speed, overall.Elapsed);
                    bytesSinceTick = 0;
                    lastReportElapsed = elapsed;
                }
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
        }

        // Final progress tick at 100%.
        ReportProgress(progress, name, received, totalBytes, 0, overall.Elapsed);

        // Verify hash on the completed part file before finalizing.
        string computed;
        try
        {
            computed = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(request.DestinationPath, attempt, overall.Elapsed,
                $"hashing failed: {ex.Message}");
        }

        bool verified = false;
        if (!string.IsNullOrWhiteSpace(request.Sha256))
        {
            if (!HashMatches(computed, request.Sha256))
            {
                // Mismatch — delete and treat as a failed attempt so the retry loop runs.
                TryDelete(partPath);
                return DownloadResult.Fail(request.DestinationPath, attempt, overall.Elapsed,
                    $"SHA-256 mismatch (expected {request.Sha256}, got {computed})");
            }

            verified = true;
        }

        // Move the completed file into place, overwriting any prior copy.
        try
        {
            TryDelete(request.DestinationPath);
            File.Move(partPath, request.DestinationPath);
        }
        catch (Exception ex)
        {
            return DownloadResult.Fail(request.DestinationPath, attempt, overall.Elapsed,
                $"failed to finalize file: {ex.Message}");
        }

        return DownloadResult.Ok(request.DestinationPath, attempt, overall.Elapsed, received, computed, verified);
    }

    private static void ReportProgress(
        IProgress<DownloadProgress>? progress,
        string name,
        long received,
        long? total,
        double speed,
        TimeSpan elapsed)
    {
        if (progress is null) return;

        progress.Report(new DownloadProgress
        {
            FileName = name,
            BytesReceived = received,
            TotalBytes = total,
            SpeedBytesPerSecond = speed,
            Elapsed = elapsed
        });
    }

    /// <summary>Computes the hex-encoded SHA-256 of a file.</summary>
    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static bool HashMatches(string? computed, string? expected)
        => !string.IsNullOrWhiteSpace(computed)
           && !string.IsNullOrWhiteSpace(expected)
           && string.Equals(computed.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.0} {units[unit]}";
    }
}
