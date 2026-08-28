namespace TCGCardShopSimModManager.Core;

public sealed record DownloadCacheInfo(long SizeBytes, int FileCount, int PartialFileCount = 0)
{
    public int VerifiedFileCount => FileCount - PartialFileCount;
}

public sealed record DownloadCacheClearResult(
    long FreedBytes,
    int DeletedFiles,
    IReadOnlyList<string> Errors);

/// <summary>Inspects and clears the manager-owned verified download cache.</summary>
public sealed class DownloadCacheManager
{
    private readonly string _cacheDirectory;

    public DownloadCacheManager(string? cacheDirectory = null) =>
        _cacheDirectory = cacheDirectory ?? ModpackInstaller.DefaultDownloadCacheDirectory;

    public DownloadCacheInfo Inspect()
    {
        if (!Directory.Exists(_cacheDirectory))
            return new DownloadCacheInfo(0, 0);

        long size = 0;
        var count = 0;
        var partialCount = 0;
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Extension.Equals(".lock", StringComparison.OrdinalIgnoreCase))
                continue;

            size += file.Length;
            count++;
            if (file.Extension.Equals(".partial", StringComparison.OrdinalIgnoreCase))
                partialCount++;
        }

        return new DownloadCacheInfo(size, count, partialCount);
    }

    public DownloadCacheClearResult Clear()
    {
        if (!Directory.Exists(_cacheDirectory))
            return new DownloadCacheClearResult(0, 0, Array.Empty<string>());

        long freed = 0;
        var deleted = 0;
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    errors.Add($"Skipped linked cache file {file.Name}.");
                    continue;
                }

                var length = file.Length;
                file.Delete();
                freed += length;
                deleted++;
            }
            catch (Exception ex)
            {
                errors.Add($"Could not remove {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new DownloadCacheClearResult(freed, deleted, errors);
    }
}
