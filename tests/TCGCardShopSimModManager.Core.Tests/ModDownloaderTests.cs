using System.Security.Cryptography;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModDownloaderTests : IDisposable
{
    private readonly string _root;
    private readonly LocalHttpServer _server = new();

    public ModDownloaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Downloads_Verifies_AndLandsFile()
    {
        var payload = MakePayload(1024);
        var mod = Ref("mod.bytes", payload);
        _server.Provider = _ => Http200(payload);

        var result = await Run(_server, mod, _root);

        Assert.True(result.Success);
        Assert.False(result.FromCache);
        var dest = Path.Combine(_root, "mod.bytes");
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".partial"));
    }

    [Fact]
    public async Task AlreadyPresentAndVerified_SkipsTheSource()
    {
        var payload = MakePayload(64);
        var mod = Ref("mod.bytes", payload);
        File.WriteAllBytes(Path.Combine(_root, "mod.bytes"), payload);
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return Http200(payload);
        };

        var result = await Run(_server, mod, _root);

        Assert.True(result.Success);
        Assert.Equal(0, requests); // never contacted the server
    }

    [Fact]
    public async Task CorruptSource_FailsCleanly_NoPartialNoFinal()
    {
        var good = MakePayload(256);
        var mod = new ModReference("corrupt", "mod.bytes", Sha(MakePayload(128)), null); // expected hash of different data
        _server.Provider = _ => Http200(good);

        var result = await Run(_server, mod, _root);

        Assert.False(result.Success);
        Assert.Contains("SHA-256", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes")));
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes.partial")));
    }

    [Fact]
    public async Task Cancellation_RemovesPartial_AndLeavesNoFinalFile()
    {
        var payload = MakePayload(200_000); // bigger than the copy buffer
        var mod = Ref("mod.bytes", payload);
        _server.Provider = _ => Http200(payload);
        using var cts = new CancellationTokenSource();

        var result = await Run(_server, mod, _root, onProgress: _ => cts.Cancel(), ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes")));
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes.partial")));
    }

    [Fact]
    public async Task Resume_ContinuesPartialFile_UsingHttpRange()
    {
        var payload = MakePayload(64_000);
        var mod = Ref("mod.bytes", payload);

        // Seed a partial file with the first 16 KiB as if a download was cut short.
        var partialPath = Path.Combine(_root, "mod.bytes.partial");
        File.WriteAllBytes(partialPath, payload[..16_000]);

        var sawRangeRequest = false;
        _server.Provider = request =>
        {
            if (request.RangeStart is 16000)
            {
                sawRangeRequest = true;
                return new HttpResponse(206, payload[16000..], $"bytes 16000-63999/{payload.Length}");
            }
            return Http200(payload);
        };

        var result = await Run(_server, mod, _root);

        Assert.True(result.Success, result.Error);
        Assert.True(sawRangeRequest, "The server should have been asked for a resumable range.");
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "mod.bytes")));
    }

    [Fact]
    public async Task TransientServerErrors_AreRetried_AndItRecovers()
    {
        var payload = MakePayload(256);
        var mod = Ref("mod.bytes", payload);
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return requests <= 2
                ? new HttpResponse(500, Array.Empty<byte>(), null)
                : Http200(payload);
        };

        var result = await Run(_server, mod, _root);

        Assert.True(result.Success, result.Error);
        Assert.Equal(3, requests);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "mod.bytes")));
    }

    [Fact]
    public async Task CacheHit_SecondDownload_DoesNotTouchTheSource()
    {
        var payload = MakePayload(128);
        var mod = Ref("mod.bytes", payload);
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return Http200(payload);
        };

        var first = await Run(_server, mod, _root);
        Assert.True(first.Success);

        var second = await Run(_server, mod, _root);

        Assert.True(second.Success);
        Assert.True(second.FromCache);
        Assert.Equal(1, requests); // only the first download hit the server
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "mod.bytes")));
    }

    [Fact]
    public async Task VerifiedCacheAndWorkspaceShareFileStorage()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var payload = MakePayload(128);
        var mod = Ref("mod.bytes", payload);
        _server.Provider = _ => Http200(payload);

        var result = await Run(_server, mod, _root);

        Assert.True(result.Success, result.Error);
        var destination = Path.Combine(_root, "mod.bytes");
        var cacheFile = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".cache")));
        File.WriteAllBytes(destination, MakePayload(64));
        Assert.Equal(File.ReadAllBytes(destination), File.ReadAllBytes(cacheFile));
    }

    [Fact]
    public async Task InsufficientDiskSpace_FailsFast_WithoutDownloading()
    {
        var mod = Ref("mod.bytes", MakePayload(32));
        _server.Provider = _ => new HttpResponse(
            200,
            MakePayload(32),
            null,
            ContentLengthOverride: long.MaxValue / 2); // claims an impossible size

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await Run(_server, mod, _root, ct: cts.Token);

        Assert.False(result.Success);
        Assert.Contains("free disk space", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes")));
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes.partial")));
    }

    [Fact]
    public async Task LocalFileSource_OfflinePipeline_Works()
    {
        var sourceDir = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDir);
        var payload = MakePayload(256);
        var mod = Ref("mod.bytes", payload);
        File.WriteAllBytes(Path.Combine(sourceDir, "mod.bytes"), payload);

        var downloader = new ModDownloader(
            new LocalFileSource(sourceDir),
            new DownloadOptions { RetryBaseDelayMs = 10 });

        var result = await downloader.DownloadAsync(mod, _root);

        Assert.True(result.Success, result.Error);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(_root, "mod.bytes")));
    }

    [Fact]
    public async Task LocalFileSource_MissingFile_FailsNonRetryable()
    {
        var mod = Ref("mod.bytes", MakePayload(32));
        var downloader = new ModDownloader(
            new LocalFileSource(Path.Combine(_root, "does-not-exist")),
            new DownloadOptions { MaxAttempts = 2, RetryBaseDelayMs = 10 });

        var result = await downloader.DownloadAsync(mod, _root);

        Assert.False(result.Success);
        Assert.Contains("Source file not found", result.Error);
        Assert.False(File.Exists(Path.Combine(_root, "mod.bytes")));
    }

    [Theory]
    [InlineData("../outside.bytes")]
    [InlineData("sub/../../outside.bytes")]
    public async Task DownloadFilename_CannotEscapeDestination(string fileName)
    {
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return Http200(MakePayload(32));
        };

        var result = await Run(_server, Ref(fileName, MakePayload(32)), _root);

        Assert.False(result.Success);
        Assert.Contains("escapes", result.Error);
        Assert.Equal(0, requests);
        Assert.False(File.Exists(Path.Combine(_root, "..", "outside.bytes")));
    }

    [Fact]
    public async Task InvalidExpectedHash_CannotChooseCachePath()
    {
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return Http200(MakePayload(32));
        };
        var mod = new ModReference("test-mod", "mod.bytes", "../outside", null);

        var result = await Run(_server, mod, _root);

        Assert.False(result.Success);
        Assert.Contains("64 hexadecimal", result.Error);
        Assert.Equal(0, requests);
    }

    // --- helpers -----------------------------------------------------------

    private static async Task<DownloadResult> Run(
        LocalHttpServer server,
        ModReference mod,
        string destination,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        return await new ModDownloader(
            new HttpModSource(m => server.Url(m.FileName)),
            new DownloadOptions
            {
                RetryBaseDelayMs = 10,
                CacheDirectory = Path.Combine(destination, ".cache")
            })
            .DownloadAsync(mod, destination, onProgress, ct);
    }

    private static HttpResponse Http200(byte[] body) => new(200, body, null);

    private static byte[] MakePayload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private static ModReference Ref(string fileName, byte[] content) =>
        new("test-mod", fileName, Sha(content), null);

    private static string Sha(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(content)).ToLowerInvariant();
    }
}
