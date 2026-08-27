using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace TCGCardShopSimModManager.Core;

public sealed record DownloadOptions
{
    public string CacheDirectory { get; init; } =
        Path.Combine(Path.GetTempPath(), "cardshopmodmanager-downloads");

    public int MaxAttempts { get; init; } = 3;

    /// <summary>Reserved headroom the free-disk check demands on top of the file size.</summary>
    public long MinimumFreeSpaceBytes { get; init; } = 25L * 1024 * 1024; // 25 MiB

    /// <summary>Base delay for the exponential backoff between attempts.</summary>
    public int RetryBaseDelayMs { get; init; } = 250;
}

/// <summary>
/// Makes a mod's bytes land safely on disk, no matter where they came from.
///
/// Safety contract — a cancelled, failed or corrupt download never leaves an
/// apparently-valid file behind:
///   1. Bytes are written to "<name>.partial" and only renamed to the final
///      name after the whole file has passed its SHA-256 check.
///   2. An existing partial is resumed via the source (HTTP Range), or the
///      download starts fresh.
///   3. Every failure between attempts deletes the partial, and the run fails.
///   4. Verified downloads are copied into a local cache, so a later request
///      never touches the network again.
/// </summary>
public sealed class ModDownloader
{
    private readonly IModSource _source;
    private readonly DownloadOptions _options;

    public ModDownloader(IModSource source, DownloadOptions options)
    {
        _source = source;
        _options = options;
    }

    public async Task<DownloadResult> DownloadAsync(
        ModReference mod,
        string destinationDirectory,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);
        Directory.CreateDirectory(_options.CacheDirectory);

        if (mod.Sha256.Length != 64 || mod.Sha256.Any(character => !Uri.IsHexDigit(character)))
            return new DownloadResult(
                false, null, "Expected SHA-256 must contain exactly 64 hexadecimal characters.", FromCache: false);

        string destinationPath;
        try
        {
            destinationPath = ContainedPath(destinationDirectory, mod.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException)
        {
            return new DownloadResult(false, null, ex.Message, FromCache: false);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var partialPath = destinationPath + ".partial";
        var cachePath = Path.Combine(_options.CacheDirectory, CacheKey(mod));

        // 1. Verified cache hit — no source contact at all.
        if (File.Exists(cachePath) && HashMatches(cachePath, mod.Sha256))
        {
            MaterializeCachedFile(cachePath, destinationPath);
            return new DownloadResult(true, destinationPath, null, FromCache: true);
        }

        // 2. Already present and verified — nothing to do.
        if (File.Exists(destinationPath))
        {
            if (HashMatches(destinationPath, mod.Sha256))
                return new DownloadResult(true, destinationPath, null, FromCache: false);

            // Present but corrupt/stale — replace it rather than trust it.
            File.Delete(destinationPath);
        }

        // 3. Attempt loop: fresh run, or resume any partial from the last attempt.
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            try
            {
                var existingPartial = GetLength(partialPath);
                using var opened = await _source.OpenAsync(mod, existingPartial > 0 ? existingPartial : null, cancellationToken);

                EnsureFreeSpace(opened.TotalBytes, destinationDirectory);

                await WriteToFileAsync(opened, partialPath, cancellationToken, onProgress);

                if (!HashMatches(partialPath, mod.Sha256))
                    throw new DownloadException("Downloaded file does not match the expected SHA-256 (corrupt download).");

                // Verify before commit: only now does the final name appear.
                File.Move(partialPath, destinationPath);

                try
                {
                    MaterializeCachedFile(destinationPath, cachePath);
                }
                catch
                {
                    // The download succeeded; the cache is a convenience, not a
                    // requirement, so a cache write failure is not fatal.
                }

                return new DownloadResult(true, destinationPath, null, FromCache: false);
            }
            catch (OperationCanceledException)
            {
                TryDelete(partialPath);
                return new DownloadResult(false, null, "Download cancelled.", FromCache: false);
            }
            catch (DownloadException ex) when (!ex.Retryable)
            {
                TryDelete(partialPath);
                Diagnostic.Write($"download failed for {mod.Id}: {ex.Message}", "download");
                return new DownloadResult(false, null, ex.Message, FromCache: false);
            }
            catch (Exception ex) when (attempt < _options.MaxAttempts)
            {
                TryDelete(partialPath);

                // Respect an explicit "wait this long" (rate limits) before
                // falling back to our own exponential backoff.
                var delay = ex is DownloadException { RetryAfterSeconds: > 0 } rateLimited
                    ? TimeSpan.FromSeconds(rateLimited.RetryAfterSeconds)
                    : TimeSpan.FromMilliseconds(BackoffMs(attempt));

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                TryDelete(partialPath);
                return new DownloadResult(false, null, $"Download failed: {ex.Message}", FromCache: false);
            }
        }

        return new DownloadResult(false, null, "Download failed after multiple attempts.", FromCache: false);
    }

    private async Task WriteToFileAsync(
        DownloadStream opened,
        string partialPath,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? onProgress)
    {
        var mode = opened.StartOffset > 0 ? FileMode.Append : FileMode.Create;
        await using var file = new FileStream(partialPath, mode, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        var downloaded = opened.StartOffset;
        while (true)
        {
            var read = await opened.Content.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            onProgress?.Invoke(new DownloadProgress(downloaded, opened.TotalBytes));
        }
    }

    private long GetLength(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool HashMatches(string filePath, string expectedSha256)
    {
        if (!File.Exists(filePath))
            return false;

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant().Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void MaterializeCachedFile(string sourcePath, string destinationPath)
    {
        try
        {
            File.Delete(destinationPath);
            if (!TryCreateHardLink(destinationPath, sourcePath))
                throw new IOException("The filesystem could not create a hard link.");
        }
        catch
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingPath) =>
        OperatingSystem.IsWindows() && CreateHardLink(linkPath, existingPath, IntPtr.Zero);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName, string existingFileName, IntPtr securityAttributes);

    private void EnsureFreeSpace(long? totalBytes, string path)
    {
        if (totalBytes is null)
            return; // Unknown size — the writer still fails loudly on a full disk.

        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        try
        {
            var freeSpace = new DriveInfo(root).AvailableFreeSpace;
            if (freeSpace < totalBytes.Value + _options.MinimumFreeSpaceBytes)
                throw new DownloadException(
                    $"Not enough free disk space on '{root}': need {totalBytes.Value} bytes, only {freeSpace} free.",
                    retryable: false);
        }
        catch (DownloadException)
        {
            throw;
        }
        catch
        {
            // Can't determine free space (network drive, unusual root) — proceed
            // and let the write itself fail if the disk really is full.
        }
    }

    private int BackoffMs(int attempt)
    {
        // 1st retry: 250ms, 2nd: 500ms, etc.
        return _options.RetryBaseDelayMs * (1 << (attempt - 1));
    }

    private static string CacheKey(ModReference mod) =>
        $"{mod.Sha256.ToLowerInvariant()}{Path.GetExtension(mod.FileName).ToLowerInvariant()}";

    private static string ContainedPath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Download filename must be a relative path: {relativePath}");

        var root = Path.GetFullPath(rootPath);
        var destination = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Download filename escapes the destination folder: {relativePath}");

        return destination;
    }
}
