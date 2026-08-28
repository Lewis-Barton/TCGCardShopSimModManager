using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class DownloadCacheManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cardshop-cache-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingCacheIsEmptyAndCanBeCleared()
    {
        var manager = new DownloadCacheManager(_root);

        Assert.Equal(new DownloadCacheInfo(0, 0), manager.Inspect());
        var result = manager.Clear();

        Assert.Equal(0, result.FreedBytes);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void InspectAndClearCountOnlyCacheFiles()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "first.zip"), new byte[128]);
        File.WriteAllBytes(Path.Combine(_root, "second.7z"), new byte[256]);
        Directory.CreateDirectory(Path.Combine(_root, "unexpected-folder"));
        File.WriteAllText(Path.Combine(_root, "unexpected-folder", "keep.txt"), "keep");
        var manager = new DownloadCacheManager(_root);

        Assert.Equal(new DownloadCacheInfo(384, 2), manager.Inspect());
        var result = manager.Clear();

        Assert.Equal(384, result.FreedBytes);
        Assert.Equal(2, result.DeletedFiles);
        Assert.Empty(result.Errors);
        Assert.True(Directory.Exists(Path.Combine(_root, "unexpected-folder")));
        Assert.Equal(new DownloadCacheInfo(0, 0), manager.Inspect());
    }

    [Fact]
    public void InspectReportsPartialDownloadsAndIgnoresLockFiles()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "verified.zip"), new byte[128]);
        File.WriteAllBytes(Path.Combine(_root, "archive.zip.partial"), new byte[64]);
        File.WriteAllText(Path.Combine(_root, "archive.zip.lock"), string.Empty);
        var manager = new DownloadCacheManager(_root);

        var info = manager.Inspect();

        Assert.Equal(new DownloadCacheInfo(192, 2, 1), info);
        Assert.Equal(1, info.VerifiedFileCount);
        var result = manager.Clear();
        Assert.Equal(3, result.DeletedFiles);
        Assert.False(File.Exists(Path.Combine(_root, "archive.zip.lock")));
    }

    public void Dispose() => TemporaryDirectory.DeleteBestEffort(_root);
}
