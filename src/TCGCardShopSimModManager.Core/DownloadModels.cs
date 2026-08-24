namespace TCGCardShopSimModManager.Core;

/// <summary>
/// What identifies a downloadable file. Mirrors the manifest fields that matter
/// for fetching.
/// </summary>
public sealed record ModReference(
    string Id,
    string FileName,
    string Sha256,
    string? Version,
    long? NexusModId = null,
    long? NexusFileId = null,
    string? DownloadUrl = null);

public sealed record DownloadProgress(long DownloadedBytes, long? TotalBytes);

public enum ModpackInstallStage
{
    Downloading,
    Preparing,
    Planning,
    Installing
}

public sealed record ModpackInstallProgress(
    ModpackInstallStage Stage,
    string? ModName,
    int ModIndex,
    int ModCount,
    long DownloadedBytes = 0,
    long? TotalBytes = null,
    bool FromCache = false);

public sealed record DownloadResult(
    bool Success,
    string? DestinationPath,
    string? Error,
    bool FromCache);

/// <summary>
/// A controlled download failure. <see cref="Retryable"/> decides whether the
/// downloader should clean the partial file and try again.
/// </summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string message, bool retryable = true, int retryAfterSeconds = 0)
        : base(message)
    {
        Retryable = retryable;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public bool Retryable { get; }

    /// <summary>When a rate limit asked us to wait, how long it wants.</summary>
    public int RetryAfterSeconds { get; }
}
