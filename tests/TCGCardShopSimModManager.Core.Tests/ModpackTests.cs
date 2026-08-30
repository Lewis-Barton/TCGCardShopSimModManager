using System.IO.Compression;
using System.Security.Cryptography;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModpackSummaryTests
{
    [Fact]
    public void IsId_MatchesCanonicalAndFormerIds_Bug009()
    {
        var pack = new ModpackSummary("my-pack", "Pack", "desc", "logo.png", "manifest.json", "1.0.0",
            FormerIds: new List<string> { "mypack", "my_pack" });

        Assert.True(pack.IsId("my-pack"));
        Assert.True(pack.IsId("mypack"));      // legacy, case-insensitive
        Assert.True(pack.IsId("MY_PACK"));       // legacy, different case
        Assert.False(pack.IsId("other-pack"));
    }

    [Fact]
    public void IsId_WithoutFormerIds_MatchesOnlyCanonical()
    {
        var pack = new ModpackSummary("my-pack", "Pack", "desc", "logo.png", "manifest.json", "1.0.0");

        Assert.True(pack.IsId("my-pack"));
        Assert.False(pack.IsId("mypack"));
    }

    [Fact]
    public void CatalogOrdering_SortsDatesNamesAndSizesWithMissingValuesLast()
    {
        var packs = new[]
        {
            Pack("zulu", "Zulu", "2026-01-01", 200),
            Pack("unknown", "Unknown", null, null),
            Pack("alpha", "alpha", "2026-03-01", 100)
        };

        Assert.Equal(["zulu", "unknown", "alpha"],
            ModpackCatalogOrdering.Sort(packs, ModpackSortOrder.Catalog).Select(pack => pack.Id));
        Assert.Equal(["alpha", "zulu", "unknown"],
            ModpackCatalogOrdering.Sort(packs, ModpackSortOrder.RecentlyUpdated).Select(pack => pack.Id));
        Assert.Equal(["alpha", "unknown", "zulu"],
            ModpackCatalogOrdering.Sort(packs, ModpackSortOrder.Name).Select(pack => pack.Id));
        Assert.Equal(["alpha", "zulu", "unknown"],
            ModpackCatalogOrdering.Sort(packs, ModpackSortOrder.SmallestDownload).Select(pack => pack.Id));
        Assert.Equal(["zulu", "alpha", "unknown"],
            ModpackCatalogOrdering.Sort(packs, ModpackSortOrder.LargestDownload).Select(pack => pack.Id));
    }

    private static ModpackSummary Pack(string id, string name, string? updated, long? size) =>
        new(id, name, "desc", "logo.png", "manifest.json", "1.0.0", updated,
            DownloadSize: size);
}

public sealed class ModpackTests : IDisposable
{
    private readonly string _root;
    private readonly LocalHttpServer _server = new();

    public ModpackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "modpack-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task IndexReader_FetchesIndex_AndResolvesUrls()
    {
        var archiveBytes = MakePayload(200);
        var sha = Sha(archiveBytes);
        var manifestJson = ManifestJson("Pack One", "ExampleMod.zip", sha);

        _server.Provider = request => request.Path switch
        {
            "/index.json" => Json(IndexJson()),
            "/p1/manifest.json" => Json(manifestJson),
            "/p1/logo.png" => new HttpResponse(200, new byte[] { 1, 2, 3 }, null),
            "/p1/ExampleMod.zip" => new HttpResponse(200, archiveBytes, null),
            _ => new HttpResponse(404, Array.Empty<byte>(), null)
        };

        var baseUrl = _server.Url("");
        var reader = new ModpackIndexReader();
        var index = await reader.FetchIndexAsync(baseUrl);

        var pack = Assert.Single(index.Packs);
        Assert.Equal("p1", pack.Id);
        Assert.False(pack.Featured);
        Assert.True(pack.Nsfw);
        Assert.Equal(123456, pack.DownloadSize);
        Assert.Equal(new[] { "starter", "qol" }, pack.Tags);
        Assert.Equal(new[] { "bepinex", "example-mod" }, pack.ModIds);
        Assert.Equal(_server.Url("p1/manifest.json"), reader.ManifestUrl(pack, baseUrl));
        Assert.Equal(_server.Url("p1/logo.png"), reader.LogoUrl(pack, baseUrl));

        var manifest = await reader.FetchManifestAsync(pack, baseUrl);
        Assert.Equal("Pack One", manifest.Name);
        Assert.Single(manifest.Mods);
    }

    [Fact]
    public async Task IndexReader_RetriesRateLimitResponse()
    {
        var requests = 0;
        _server.Provider = _ => ++requests == 1
            ? new HttpResponse(429, Array.Empty<byte>(), null, RetryAfter: "0")
            : Json(IndexJson());
        var cachePath = Path.Combine(_root, "retry-index.json");
        var reader = new ModpackIndexReader(cachePath: cachePath, retryBaseDelayMs: 1);

        var index = await reader.FetchIndexAsync(_server.Url(""));

        Assert.Single(index.Packs);
        Assert.Equal(2, requests);
        Assert.False(reader.LastFetchUsedCache);
        Assert.True(File.Exists(cachePath));
    }

    [Fact]
    public async Task IndexReader_ReadsSavedCatalogWithoutNetworkRequest()
    {
        var cachePath = Path.Combine(_root, "immediate-index.json");
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return Json(IndexJson());
        };
        var writer = new ModpackIndexReader(cachePath: cachePath);
        await writer.FetchIndexAsync(_server.Url(""));
        var reader = new ModpackIndexReader(cachePath: cachePath);

        var cached = reader.ReadCachedIndex();

        Assert.NotNull(cached);
        Assert.Single(cached.Packs);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void IndexReader_BundledCatalogContainsPublishedPacks()
    {
        var index = new ModpackIndexReader().ReadBundledIndex();

        Assert.Contains(index.Packs, pack => pack.Id == "real-tcg-overhaul");
        Assert.Contains(index.Packs, pack => pack.Id == "cardverse-overhaul");
    }

    [Fact]
    public async Task IndexReader_UsesLastGoodCacheAfterRetriesFail()
    {
        var cachePath = Path.Combine(_root, "fallback-index.json");
        _server.Provider = _ => Json(IndexJson());
        var initialReader = new ModpackIndexReader(cachePath: cachePath, retryBaseDelayMs: 1);
        await initialReader.FetchIndexAsync(_server.Url(""));

        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return new HttpResponse(429, Array.Empty<byte>(), null, RetryAfter: "0");
        };
        var fallbackReader = new ModpackIndexReader(
            cachePath: cachePath, maxAttempts: 3, retryBaseDelayMs: 1);

        var index = await fallbackReader.FetchIndexAsync(_server.Url(""));

        Assert.Single(index.Packs);
        Assert.Equal(3, requests);
        Assert.True(fallbackReader.LastFetchUsedCache);
    }

    [Fact]
    public async Task ModSource_UsesDownloadUrl_WhenPresent()
    {
        var archiveBytes = MakePayload(200);
        var mod = Ref("archive.zip", archiveBytes, downloadUrl: _server.Url("archive.zip"));

        // Fallback points at an empty folder, so success proves the DownloadUrl path was used.
        var fallback = new LocalFileSource(Path.Combine(_root, "empty"));
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);

        var result = await Download(mod, new ModpackModSource("tcgcardshopsimulator", fallback));
        Assert.True(result.Success, result.Error);
        Assert.Equal(archiveBytes, File.ReadAllBytes(Path.Combine(_root, "archive.zip")));
    }

    [Fact]
    public async Task ModSource_FallsBack_WhenNoDownloadUrlOrNexus()
    {
        var archiveBytes = MakePayload(200);
        var mod = Ref("archive.zip", archiveBytes); // no source at all
        var fallbackDir = Path.Combine(_root, "fallback");
        Directory.CreateDirectory(fallbackDir);
        File.WriteAllBytes(Path.Combine(fallbackDir, "archive.zip"), archiveBytes);

        var result = await Download(mod, new ModpackModSource("tcgcardshopsimulator", new LocalFileSource(fallbackDir)));
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public async Task ModpackInstaller_DownloadsAndInstalls_HostedPack()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var sha = Sha(archiveBytes);
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);

        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", sha, "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("ExampleMod.zip"));
        var manifest = new ModListManifest(1, "Test Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);

        var report = await new ModpackInstaller(gameFolder).InstallAsync(manifest);
        Assert.True(report.Success, string.Join("\n", report.Lines));

        var installed = Path.Combine(gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll");
        Assert.True(File.Exists(installed));
        Assert.Contains(new JournalStore(gameFolder).Load(), e => e.ModName == "Example Mod");
    }

    [Fact]
    public async Task ModpackInstaller_ReportsDownloadAndInstallProgress()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);
        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", Sha(archiveBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("ExampleMod.zip"));
        var manifest = new ModListManifest(
            1, "Test Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "progress-game");
        Directory.CreateDirectory(gameFolder);
        var updates = new List<ModpackInstallProgress>();

        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest, progress: new RecordingProgress<ModpackInstallProgress>(updates.Add));

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.Contains(updates, update =>
            update.Stage == ModpackInstallStage.Downloading &&
            update.ModName == "Example Mod" &&
            update.ModIndex == 1 &&
            update.ModCount == 1);
        Assert.Contains(updates, update =>
            update.Stage == ModpackInstallStage.Downloading &&
            update.DownloadedBytes == archiveBytes.Length &&
            update.TotalBytes == archiveBytes.Length);
        Assert.Contains(updates, update =>
            update.Stage == ModpackInstallStage.Preparing);
        Assert.Contains(updates, update =>
            update.Stage == ModpackInstallStage.Planning &&
            update.ModName == "Example Mod" &&
            update.ModIndex == 1 &&
            update.ModCount == 1);
        Assert.Equal(ModpackInstallStage.Installing, updates[^1].Stage);
        Assert.Equal("Example Mod", updates[^1].ModName);
        Assert.Equal(1, updates[^1].ModIndex);
        Assert.Equal(1, updates[^1].ModCount);
    }

    [Fact]
    public async Task ModpackInstaller_RejectsUnsafeManifestBeforeDownloading()
    {
        var requests = 0;
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        _server.Provider = _ =>
        {
            requests++;
            return new HttpResponse(200, archiveBytes, null);
        };
        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "../outside.zip", Sha(archiveBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("outside.zip"));
        var manifest = new ModListManifest(
            1, "Unsafe Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "unsafe-game");
        Directory.CreateDirectory(gameFolder);

        var report = await new ModpackInstaller(gameFolder).InstallAsync(manifest);

        Assert.False(report.Success);
        Assert.Contains(report.Lines, line => line.Contains("archive path is unsafe"));
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task ModpackInstaller_InstallsRequiredButSkipsUnselectedOptionalMod()
    {
        var requiredBytes = MakeZip(("Required.dll", "required"));
        var optionalBytes = MakeZip(("Optional.dll", "optional"));
        var optionalRequests = 0;
        _server.Provider = request =>
        {
            if (request.Path.EndsWith("optional.zip", StringComparison.OrdinalIgnoreCase))
            {
                optionalRequests++;
                return new HttpResponse(200, optionalBytes, null);
            }
            return new HttpResponse(200, requiredBytes, null);
        };

        var required = new ModEntry(
            "required", "Required Mod", "1.0.0", "required.zip", Sha(requiredBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("required.zip"));
        var optional = new ModEntry(
            "optional", "Optional Mod", "1.0.0", "optional.zip", Sha(optionalBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("optional.zip"), Required: false);
        var manifest = new ModListManifest(
            1, "Selectable Pack", "tcgcardshopsimulator", [required, optional]);
        var gameFolder = Path.Combine(_root, "selected-game");
        Directory.CreateDirectory(gameFolder);
        var pack = new ModpackSummary(
            "selectable", "Selectable Pack", "Test", "logo.png", "manifest.json", "1.0.0");

        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest, pack: pack, selectedOptionalIds: Array.Empty<string>());

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.True(File.Exists(Path.Combine(
            gameFolder, "BepInEx", "plugins", "Required Mod", "Required.dll")));
        Assert.False(File.Exists(Path.Combine(
            gameFolder, "BepInEx", "plugins", "Optional Mod", "Optional.dll")));
        Assert.Equal(0, optionalRequests);
        Assert.Empty(Assert.Single(
            new ModpackJournalStore(gameFolder).Load()).SelectedOptionalModIds!);
    }

    [Fact]
    public async Task ModpackInstaller_DeselectingOptionalModRemovesPreviousInstall()
    {
        var requiredBytes = MakeZip(("Required.dll", "required"));
        var optionalBytes = MakeZip(("Optional.dll", "optional"));
        _server.Provider = request => request.Path.EndsWith("optional.zip", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponse(200, optionalBytes, null)
            : new HttpResponse(200, requiredBytes, null);
        var required = new ModEntry(
            "required", "Required Mod", "1.0.0", "required.zip", Sha(requiredBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("required.zip"));
        var optional = new ModEntry(
            "optional", "Optional Mod", "1.0.0", "optional.zip", Sha(optionalBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("optional.zip"), Required: false);
        var manifest = new ModListManifest(
            1, "Selectable Pack", "tcgcardshopsimulator", [required, optional]);
        var gameFolder = Path.Combine(_root, "deselection-game");
        Directory.CreateDirectory(gameFolder);
        var firstPack = new ModpackSummary(
            "selectable", "Selectable Pack", "Test", "logo.png", "manifest.json", "1.0.0");
        var installer = new ModpackInstaller(gameFolder);
        Assert.True((await installer.InstallAsync(
            manifest, pack: firstPack, selectedOptionalIds: ["optional"])).Success);

        var report = await installer.InstallAsync(
            manifest,
            pack: firstPack with { Version = "1.1.0" },
            selectedOptionalIds: Array.Empty<string>());

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.False(File.Exists(Path.Combine(
            gameFolder, "BepInEx", "plugins", "Optional Mod", "Optional.dll")));
        Assert.DoesNotContain(new JournalStore(gameFolder).Load(), entry => entry.ModId == "optional");
        var installedPack = Assert.Single(new ModpackJournalStore(gameFolder).Load());
        Assert.Equal("1.1.0", installedPack.PackVersion);
        Assert.Empty(installedPack.SelectedOptionalModIds!);
    }

    [Fact]
    public async Task ModpackInstaller_UnsafeDeselectionRollsBackPackState()
    {
        var requiredBytes = MakeZip(("Required.dll", "required"));
        var optionalBytes = MakeZip(("Optional.dll", "optional"));
        _server.Provider = request => request.Path.EndsWith("optional.zip", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponse(200, optionalBytes, null)
            : new HttpResponse(200, requiredBytes, null);
        var required = new ModEntry(
            "required", "Required Mod", "1.0.0", "required.zip", Sha(requiredBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("required.zip"));
        var optional = new ModEntry(
            "optional", "Optional Mod", "1.0.0", "optional.zip", Sha(optionalBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("optional.zip"), Required: false);
        var manifest = new ModListManifest(
            1, "Selectable Pack", "tcgcardshopsimulator", [required, optional]);
        var gameFolder = Path.Combine(_root, "deselection-rollback-game");
        Directory.CreateDirectory(gameFolder);
        var firstPack = new ModpackSummary(
            "selectable", "Selectable Pack", "Test", "logo.png", "manifest.json", "1.0.0");
        var installer = new ModpackInstaller(gameFolder);
        Assert.True((await installer.InstallAsync(
            manifest, pack: firstPack, selectedOptionalIds: ["optional"])).Success);
        var optionalPath = Path.Combine(
            gameFolder, "BepInEx", "plugins", "Optional Mod", "Optional.dll");
        File.WriteAllText(optionalPath, "user change");

        var report = await installer.InstallAsync(
            manifest,
            pack: firstPack with { Version = "1.1.0" },
            selectedOptionalIds: Array.Empty<string>());

        Assert.False(report.Success);
        Assert.Equal("user change", File.ReadAllText(optionalPath));
        Assert.Contains(new JournalStore(gameFolder).Load(), entry => entry.ModId == "optional");
        var installedPack = Assert.Single(new ModpackJournalStore(gameFolder).Load());
        Assert.Equal("1.0.0", installedPack.PackVersion);
        Assert.Equal(["optional"], installedPack.SelectedOptionalModIds);
    }

    [Fact]
    public async Task ModpackInstaller_PreflightPasses_WhenTotalSizeDeclared()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var sha = Sha(archiveBytes);
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);

        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", sha, "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("ExampleMod.zip"));
        // Declaring totalSize exercises the pre-flight path; 1024 bytes is well
        // within the test machine's free space, so the install should proceed.
        var manifest = new ModListManifest(1, "Cleanup Pack", "tcgcardshopsimulator", new List<ModEntry> { mod }, TotalSize: 1024);

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);

        var report = await new ModpackInstaller(gameFolder).InstallAsync(manifest);
        Assert.True(report.Success, string.Join("\n", report.Lines));

        var installed = Path.Combine(gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll");
        Assert.True(File.Exists(installed));
    }

    [Fact]
    public async Task ModpackInstaller_DoesNotDeleteCallerOwnedCache()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var sha = Sha(archiveBytes);
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);

        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", sha, "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("ExampleMod.zip"));
        var manifest = new ModListManifest(1, "Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "game");
        var cacheDirectory = Path.Combine(_root, "shared-cache");
        Directory.CreateDirectory(gameFolder);
        Directory.CreateDirectory(cacheDirectory);
        var unrelatedFile = Path.Combine(cacheDirectory, "keep.txt");
        File.WriteAllText(unrelatedFile, "belongs to caller");

        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest, cacheDirectory: cacheDirectory);

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.True(File.Exists(unrelatedFile));
        Assert.True(Directory.Exists(cacheDirectory));
    }

    [Fact]
    public async Task ModpackInstaller_RetryUsesVerifiedCacheAfterPlanningFailure()
    {
        var archiveBytes = MakeZip(("readme.txt", "documentation only"));
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return new HttpResponse(200, archiveBytes, null);
        };
        var mod = new ModEntry(
            "docs-only", "Docs Only", null, "docs.zip", Sha(archiveBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("docs.zip"));
        var manifest = new ModListManifest(
            1, "Retry Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "retry-game");
        var verifiedCache = Path.Combine(_root, "verified-cache");
        Directory.CreateDirectory(gameFolder);
        var installer = new ModpackInstaller(gameFolder);

        var first = await installer.InstallAsync(
            manifest, verifiedCacheDirectory: verifiedCache);
        var second = await installer.InstallAsync(
            manifest, verifiedCacheDirectory: verifiedCache);

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Equal(1, requests);
        Assert.Single(Directory.GetFiles(verifiedCache));
    }

    [Fact]
    public async Task ModpackInstaller_RestoresMissingWorkspaceArchiveBeforePlanning()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var requests = 0;
        _server.Provider = _ =>
        {
            requests++;
            return new HttpResponse(200, archiveBytes, null);
        };
        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", Sha(archiveBytes),
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("ExampleMod.zip"));
        var manifest = new ModListManifest(
            1, "Workspace Recovery Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "workspace-recovery-game");
        var workspace = Path.Combine(_root, "workspace-recovery-downloads");
        var verifiedCache = Path.Combine(_root, "workspace-recovery-cache");
        Directory.CreateDirectory(gameFolder);
        var removed = false;
        var progress = new RecordingProgress<ModpackInstallProgress>(update =>
        {
            if (removed || update.Stage != ModpackInstallStage.Preparing)
                return;

            File.Delete(Path.Combine(workspace, mod.Archive));
            removed = true;
        });

        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest,
            cacheDirectory: workspace,
            progress: progress,
            verifiedCacheDirectory: verifiedCache);

        Assert.True(removed);
        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.Equal(1, requests);
        Assert.True(File.Exists(Path.Combine(
            gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll")));
    }

    [Fact]
    public async Task ModpackInstaller_CancellationDuringInstallRollsBackCompletedMods()
    {
        var firstBytes = MakeZip(("First.dll", "first"));
        var secondBytes = MakeZip(("Second.dll", "second"));
        _server.Provider = request => request.Path.Contains("First", StringComparison.Ordinal)
            ? new HttpResponse(200, firstBytes, null)
            : new HttpResponse(200, secondBytes, null);
        var first = new ModEntry(
            "first", "First", null, "First.zip", Sha(firstBytes), "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("First.zip"));
        var second = new ModEntry(
            "second", "Second", null, "Second.zip", Sha(secondBytes), "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("Second.zip"));
        var manifest = new ModListManifest(
            1, "Cancellation Pack", "tcgcardshopsimulator", new List<ModEntry> { first, second });
        var gameFolder = Path.Combine(_root, "cancel-install-game");
        Directory.CreateDirectory(gameFolder);
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress<ModpackInstallProgress>(update =>
        {
            if (update.Stage == ModpackInstallStage.Installing && update.ModIndex == 2)
                cancellation.Cancel();
        });

        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest, progress: progress, cancellationToken: cancellation.Token);

        Assert.False(report.Success);
        Assert.Contains(report.Lines, line => line.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Lines, line => line == "Deployment rollback completed.");
        Assert.False(File.Exists(Path.Combine(
            gameFolder, "BepInEx", "plugins", "First", "First.dll")));
        Assert.Empty(new JournalStore(gameFolder).Load());
    }

    [Fact]
    public async Task ModpackInstaller_ManifestNameCannotChooseCleanupDirectory()
    {
        var archiveBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var sha = Sha(archiveBytes);
        _server.Provider = _ => new HttpResponse(200, archiveBytes, null);

        var protectedDirectory = Path.Combine(_root, "do-not-delete");
        Directory.CreateDirectory(protectedDirectory);
        var sentinel = Path.Combine(protectedDirectory, "keep.txt");
        File.WriteAllText(sentinel, "keep");

        var mod = new ModEntry(
            "example-mod", "Example Mod", null, "ExampleMod.zip", sha, "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url("ExampleMod.zip"));
        var manifest = new ModListManifest(
            1, protectedDirectory, "tcgcardshopsimulator", new List<ModEntry> { mod });
        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);

        var report = await new ModpackInstaller(gameFolder).InstallAsync(manifest);

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void EnforceBepInExFirst_MakesBepInExAResolverDependency()
    {
        var bepInEx = new ModEntry("bepinex", "BepInEx", "5.4.23", "bepinex.zip", "irrelevant",
            ModListConventions.BepInExInstallType, new List<string>(), new List<string>());
        var mod = new ModEntry("example-mod", "Example Mod", "1.0.0", "mod.zip", "irrelevant",
            "BepInExPlugin", new List<string>(), new List<string>());
        var manifest = new ModListManifest(1, "Pack", "tcgcardshopsimulator", new List<ModEntry> { bepInEx, mod });

        var normalized = ModpackInstaller.EnforceBepInExFirst(manifest);
        var allIds = new HashSet<string>(normalized.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var ordered = new ModListResolver().Resolve(normalized, allIds);

        Assert.True(ordered.IsValid);
        Assert.Equal("bepinex", ordered.OrderedMods[0].Id);
    }

    [Fact]
    public void EnforceBepInExFirst_LeavesPacksWithoutBepInExUnchanged()
    {
        var mod = new ModEntry("example-mod", "Example Mod", "1.0.0", "mod.zip", "irrelevant",
            "BepInExPlugin", new List<string>(), new List<string>());
        var manifest = new ModListManifest(1, "Pack", "tcgcardshopsimulator", new List<ModEntry> { mod });

        var normalized = ModpackInstaller.EnforceBepInExFirst(manifest);
        Assert.Single(normalized.Mods);
        Assert.Empty(normalized.Mods[0].Dependencies);
    }

    [Fact]
    public async Task ModpackInstaller_InstallsBepInExFirstAndRecordsPack()
    {
        var bepInExBytes = MakeZip(("BepInEx/core/doorstop.dll", "bepinex-bytes"));
        var bepInExSha = Sha(bepInExBytes);
        var modBytes = MakeZip(("ExampleMod.dll", "dll-bytes"));
        var modSha = Sha(modBytes);

        _server.Provider = request => request.Path switch
        {
            "/bepinex.zip" => new HttpResponse(200, bepInExBytes, null),
            "/mod.zip" => new HttpResponse(200, modBytes, null),
            _ => new HttpResponse(404, Array.Empty<byte>(), null)
        };

        var bepInEx = new ModEntry("bepinex", "BepInEx", "5.4.23", "bepinex.zip", bepInExSha,
            ModListConventions.BepInExInstallType, new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("bepinex.zip"));
        var mod = new ModEntry("example-mod", "Example Mod", "1.0.0", "mod.zip", modSha,
            "BepInExPlugin", new List<string>(), new List<string>(),
            DownloadUrl: _server.Url("mod.zip"));
        var manifest = new ModListManifest(1, "Pack One", "tcgcardshopsimulator", new List<ModEntry> { bepInEx, mod });

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);

        var summary = new ModpackSummary("p1", "Pack One", "desc", "logo.png", "manifest.json", "1.0.0");
        var report = await new ModpackInstaller(gameFolder).InstallAsync(manifest, pack: summary);
        Assert.True(report.Success, string.Join("\n", report.Lines));

        // BepInEx (the framework) landed under BepInEx/, and the plugin under plugins/.
        Assert.True(File.Exists(Path.Combine(gameFolder, "BepInEx", "core", "doorstop.dll")));
        Assert.True(File.Exists(Path.Combine(gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll")));

        // The installed pack version is recorded so the app can flag updates.
        var recorded = new ModpackJournalStore(gameFolder).Load();
        var entry = Assert.Single(recorded);
        Assert.Equal("p1", entry.PackId);
        Assert.Equal("1.0.0", entry.PackVersion);
        Assert.All(new JournalStore(gameFolder).Load(), mod => Assert.Equal("p1", mod.PackId));
    }

    [Fact]
    public async Task ModpackInstaller_UpdateChangesFilesBeforeRecordingNewPackVersion()
    {
        var firstBytes = MakeZip(("ExampleMod.dll", "version-one"));
        var secondBytes = MakeZip(("ExampleMod.dll", "version-two"));
        var currentBytes = firstBytes;
        _server.Provider = _ => new HttpResponse(200, currentBytes, null);

        ModListManifest Manifest(byte[] archive, string version) => new(
            1,
            "Pack One",
            "tcgcardshopsimulator",
            new List<ModEntry>
            {
                new("example-mod", "Example Mod", version, "ExampleMod.zip", Sha(archive),
                    "BepInExPlugin", new List<string>(), new List<string>(),
                    DownloadUrl: _server.Url("ExampleMod.zip"))
            });

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        var installer = new ModpackInstaller(gameFolder);
        var firstPack = new ModpackSummary("p1", "Pack One", "desc", "logo.png", "manifest.json", "1.0.0");
        Assert.True((await installer.InstallAsync(Manifest(firstBytes, "1.0.0"), pack: firstPack)).Success);

        currentBytes = secondBytes;
        var secondPack = firstPack with { Version = "2.0.0" };
        var report = await installer.InstallAsync(Manifest(secondBytes, "2.0.0"), pack: secondPack);

        Assert.True(report.Success, string.Join("\n", report.Lines));
        var installed = Path.Combine(gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll");
        Assert.Equal("version-two", File.ReadAllText(installed));
        Assert.Equal("2.0.0", Assert.Single(new ModpackJournalStore(gameFolder).Load()).PackVersion);
        Assert.Equal("2.0.0", Assert.Single(new JournalStore(gameFolder).Load()).Version);
    }

    [Fact]
    public async Task ModpackInstaller_RefusesDifferentPackWithoutExplicitSwitch()
    {
        var archive = MakeZip(("First.dll", "first"));
        _server.Provider = _ => new HttpResponse(200, archive, null);
        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        var installer = new ModpackInstaller(gameFolder);
        var firstManifest = Manifest("first", "First", "First.zip", archive);
        var secondManifest = Manifest("second", "Second", "Second.zip", archive);

        Assert.True((await installer.InstallAsync(firstManifest,
            pack: Summary("first-pack", "First pack"))).Success);

        var report = await installer.InstallAsync(secondManifest,
            pack: Summary("second-pack", "Second pack"));

        Assert.False(report.Success);
        Assert.Contains(report.Lines, line => line.Contains("already installed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("first-pack", Assert.Single(new ModpackJournalStore(gameFolder).Load()).PackId);
    }

    [Fact]
    public async Task ModpackInstaller_SwitchRetainsMatchingModsAndRemovesUnusedMods()
    {
        var sharedArchive = MakeZip(("Shared.dll", "shared"));
        var oldArchive = MakeZip(("Old.dll", "old"));
        var newArchive = MakeZip(("New.dll", "new"));
        var archives = new Dictionary<string, byte[]>
        {
            ["/Shared.zip"] = sharedArchive,
            ["/Old.zip"] = oldArchive,
            ["/New.zip"] = newArchive
        };
        _server.Provider = request => archives.TryGetValue(request.Path, out var bytes)
            ? new HttpResponse(200, bytes, null)
            : new HttpResponse(404, Array.Empty<byte>(), null);

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        var installer = new ModpackInstaller(gameFolder);
        var first = new ModListManifest(1, "First pack", "tcgcardshopsimulator",
            new List<ModEntry>
            {
                Entry("shared", "Shared", "Shared.zip", sharedArchive),
                Entry("old", "Old", "Old.zip", oldArchive)
            });
        var second = new ModListManifest(1, "Second pack", "tcgcardshopsimulator",
            new List<ModEntry>
            {
                Entry("shared", "Shared", "Shared.zip", sharedArchive),
                Entry("new", "New", "New.zip", newArchive)
            });
        Assert.True((await installer.InstallAsync(first,
            pack: Summary("first-pack", "First pack"))).Success);
        var sharedPath = Path.Combine(gameFolder, "BepInEx", "plugins", "Shared", "Shared.dll");
        var installedAt = new JournalStore(gameFolder).Load().Single(entry => entry.ModId == "shared").InstalledAt;

        var report = await installer.InstallAsync(second,
            pack: Summary("second-pack", "Second pack"), switchInstalledPack: true);

        Assert.True(report.Success, string.Join("\n", report.Lines));
        Assert.True(File.Exists(sharedPath));
        Assert.False(File.Exists(Path.Combine(gameFolder, "BepInEx", "plugins", "Old", "Old.dll")));
        Assert.True(File.Exists(Path.Combine(gameFolder, "BepInEx", "plugins", "New", "New.dll")));
        var entries = new JournalStore(gameFolder).Load();
        Assert.Equal(installedAt, entries.Single(entry => entry.ModId == "shared").InstalledAt);
        Assert.All(entries, entry => Assert.Equal("second-pack", entry.PackId));
        Assert.Equal("second-pack", Assert.Single(new ModpackJournalStore(gameFolder).Load()).PackId);
    }

    [Fact]
    public async Task ModpackInstaller_FailedSwitchRestoresOriginalPack()
    {
        var oldArchive = MakeZip(("Old.dll", "old"));
        var rejectedArchive = MakeZip(("blocked.exe", "blocked"));
        var archives = new Dictionary<string, byte[]>
        {
            ["/Old.zip"] = oldArchive,
            ["/Rejected.zip"] = rejectedArchive
        };
        _server.Provider = request => archives.TryGetValue(request.Path, out var bytes)
            ? new HttpResponse(200, bytes, null)
            : new HttpResponse(404, Array.Empty<byte>(), null);

        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        var installer = new ModpackInstaller(gameFolder);
        var first = Manifest("old", "Old", "Old.zip", oldArchive);
        var rejected = Manifest("rejected", "Rejected", "Rejected.zip", rejectedArchive);
        Assert.True((await installer.InstallAsync(first,
            pack: Summary("first-pack", "First pack"))).Success);
        var oldPath = Path.Combine(gameFolder, "BepInEx", "plugins", "Old", "Old.dll");

        var report = await installer.InstallAsync(rejected,
            pack: Summary("second-pack", "Second pack"), switchInstalledPack: true);

        Assert.False(report.Success);
        Assert.True(File.Exists(oldPath));
        Assert.Equal("old", File.ReadAllText(oldPath));
        var journal = Assert.Single(new JournalStore(gameFolder).Load());
        Assert.Equal("old", journal.ModId);
        Assert.Equal("first-pack", journal.PackId);
        Assert.Equal("first-pack", Assert.Single(new ModpackJournalStore(gameFolder).Load()).PackId);
    }

    [Fact]
    public async Task ModpackInstaller_SwitchCanKeepSeparateSaveProfiles()
    {
        var firstArchive = MakeZip(("First.dll", "first"));
        var secondArchive = MakeZip(("Second.dll", "second"));
        var archives = new Dictionary<string, byte[]>
        {
            ["/First.zip"] = firstArchive,
            ["/Second.zip"] = secondArchive
        };
        _server.Provider = request => new HttpResponse(200, archives[request.Path], null);
        var gameFolder = Path.Combine(_root, "save-switch-game");
        var saveFolder = Path.Combine(_root, "active-saves");
        var saveStorage = Path.Combine(_root, "save-storage");
        Directory.CreateDirectory(gameFolder);
        Directory.CreateDirectory(saveFolder);
        var activeSave = Path.Combine(saveFolder, "savedGames_Release0.gd");
        File.WriteAllText(activeSave, "first-progress");
        var saveProfiles = new ModpackSaveProfileManager(saveFolder, saveStorage, () => false);
        var installer = new ModpackInstaller(gameFolder, saveProfiles: saveProfiles);
        var first = Manifest("first", "First", "First.zip", firstArchive);
        var second = Manifest("second", "Second", "Second.zip", secondArchive);
        Assert.True((await installer.InstallAsync(first,
            pack: Summary("first-pack", "First pack"))).Success);

        var toSecond = await installer.InstallAsync(second,
            pack: Summary("second-pack", "Second pack"),
            switchInstalledPack: true,
            swapSaveProfile: true);

        Assert.True(toSecond.Success, string.Join("\n", toSecond.Lines));
        Assert.False(File.Exists(activeSave));
        Assert.True(saveProfiles.Inspect("first-pack").HasSaves);
        File.WriteAllText(activeSave, "second-progress");

        var backToFirst = await installer.InstallAsync(first,
            pack: Summary("first-pack", "First pack"),
            switchInstalledPack: true,
            swapSaveProfile: true);

        Assert.True(backToFirst.Success, string.Join("\n", backToFirst.Lines));
        Assert.Equal("first-progress", File.ReadAllText(activeSave));
        Assert.True(saveProfiles.Inspect("second-pack").HasSaves);
    }

    [Fact]
    public async Task ModpackInstaller_FailedSwitchRestoresActiveSaveProfile()
    {
        var oldArchive = MakeZip(("Old.dll", "old"));
        var rejectedArchive = MakeZip(("blocked.exe", "blocked"));
        var archives = new Dictionary<string, byte[]>
        {
            ["/Old.zip"] = oldArchive,
            ["/Rejected.zip"] = rejectedArchive
        };
        _server.Provider = request => new HttpResponse(200, archives[request.Path], null);
        var gameFolder = Path.Combine(_root, "failed-save-switch-game");
        var saveFolder = Path.Combine(_root, "failed-active-saves");
        var saveStorage = Path.Combine(_root, "failed-save-storage");
        Directory.CreateDirectory(gameFolder);
        Directory.CreateDirectory(saveFolder);
        var activeSave = Path.Combine(saveFolder, "savedGames_Release0.gd");
        File.WriteAllText(activeSave, "original-progress");
        var saveProfiles = new ModpackSaveProfileManager(saveFolder, saveStorage, () => false);
        var installer = new ModpackInstaller(gameFolder, saveProfiles: saveProfiles);
        Assert.True((await installer.InstallAsync(
            Manifest("old", "Old", "Old.zip", oldArchive),
            pack: Summary("first-pack", "First pack"))).Success);

        var report = await installer.InstallAsync(
            Manifest("rejected", "Rejected", "Rejected.zip", rejectedArchive),
            pack: Summary("second-pack", "Second pack"),
            switchInstalledPack: true,
            swapSaveProfile: true);

        Assert.False(report.Success);
        Assert.Equal("original-progress", File.ReadAllText(activeSave));
        Assert.False(saveProfiles.Inspect("first-pack").HasSaves);
        Assert.Equal("first-pack", Assert.Single(new ModpackJournalStore(gameFolder).Load()).PackId);
    }

    private ModListManifest Manifest(string id, string name, string archiveName, byte[] archive) =>
        new(1, name, "tcgcardshopsimulator", new List<ModEntry> { Entry(id, name, archiveName, archive) });

    private ModEntry Entry(string id, string name, string archiveName, byte[] archive) =>
        new(id, name, "1.0.0", archiveName, Sha(archive), "BepInExPlugin",
            new List<string>(), new List<string>(), DownloadUrl: _server.Url(archiveName));

    private static ModpackSummary Summary(string id, string name) =>
        new(id, name, "desc", "logo.png", "manifest.json", "1.0.0");

    [Fact]
    public void ModpackVersion_IsNewer_Cases()
    {
        Assert.True(ModpackVersion.IsNewer("1.0.0", "1.1.0"));
        Assert.True(ModpackVersion.IsNewer("1.0", "1.0.1"));
        Assert.False(ModpackVersion.IsNewer("1.0.0", "1.0.0"));
        Assert.False(ModpackVersion.IsNewer("1.1.0", "1.0.0"));
        Assert.False(ModpackVersion.IsNewer(null, "1.0.0")); // nothing installed -> no flag
    }

    [Fact]
    public void ModpackVersion_IsNewer_ToleratesPrefixesAndComponentCounts_Bug006_Bug007()
    {
        // BUG-006: v-prefixed and pre-release versions must be detected when newer.
        Assert.True(ModpackVersion.IsNewer("1.2.0", "v1.3.0"));
        Assert.True(ModpackVersion.IsNewer("1.2.0", "1.3.0-beta"));
        Assert.True(ModpackVersion.IsNewer("v1.2.0", "v1.3.0"));
        Assert.False(ModpackVersion.IsNewer("v1.2.0", "v1.2.0")); // equal despite prefix

        // BUG-007: differing component counts must not spuriously flag an update.
        Assert.False(ModpackVersion.IsNewer("1.0", "1.0.0"));
        Assert.False(ModpackVersion.IsNewer("1.0.0", "1.0"));

        // Garbled versions are never "newer".
        Assert.False(ModpackVersion.IsNewer("garbage", "1.0.0"));
        Assert.False(ModpackVersion.IsNewer("1.0.0", "garbage"));
    }

    [Fact]
    public void ModpackJournalStore_RecordsAndReadsBack_ReplacingOnRerecord()
    {
        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        var store = new ModpackJournalStore(gameFolder);

        store.Record("p1", "1.0.0", "Pack One");
        store.Record("p2", "2.0.0", "Pack Two");
        var loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, e => e.PackId == "p1" && e.PackVersion == "1.0.0");
        Assert.Contains(loaded, e => e.PackId == "p2" && e.PackVersion == "2.0.0");

        // Re-recording the same pack replaces rather than duplicates.
        store.Record("p1", "1.1.0", "Pack One");
        loaded = store.Load();
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, e => e.PackId == "p1" && e.PackVersion == "1.1.0");

        store.Record("p1", "1.2.0", "Pack One", ["optional-a", "optional-b"]);
        var updated = store.Load().Single(entry => entry.PackId == "p1");
        Assert.Equal(["optional-a", "optional-b"], updated.SelectedOptionalModIds);
    }

    [Fact]
    public void UpdateDetection_FlagsNewerPublishedVersion()
    {
        var gameFolder = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameFolder);
        new ModpackJournalStore(gameFolder).Record("p1", "1.0.0", "Pack One");

        var installed = new ModpackJournalStore(gameFolder).Load()
            .FirstOrDefault(p => p.PackId == "p1");
        Assert.NotNull(installed);
        Assert.True(ModpackVersion.IsNewer(installed!.PackVersion, "1.1.0"));
        Assert.False(ModpackVersion.IsNewer(installed.PackVersion, "1.0.0"));
    }

    // --- helpers -----------------------------------------------------------

    private async Task<DownloadResult> Download(ModReference mod, IModSource source) =>
        await new ModDownloader(source, new DownloadOptions { RetryBaseDelayMs = 10 })
            .DownloadAsync(mod, _root);

    private static ModReference Ref(string fileName, byte[] content, string? downloadUrl = null) =>
        new("test-mod", fileName, Sha(content), null, DownloadUrl: downloadUrl);

    private static string IndexJson() =>
        "{\"version\":1,\"packs\":[{\"id\":\"p1\",\"name\":\"Pack One\"," +
        "\"shortDescription\":\"desc\",\"logo\":\"p1/logo.png\",\"manifest\":\"p1/manifest.json\"," +
        "\"version\":\"1.0.0\",\"updated\":\"2026-08-12\",\"source\":\"https://example.com/\"," +
        "\"featured\":false,\"nsfw\":true,\"downloadSize\":123456," +
        "\"tags\":[\"starter\",\"qol\"],\"modIds\":[\"bepinex\",\"example-mod\"]}]}";

    private static string ManifestJson(string name, string archive, string sha) =>
        "{\"manifestVersion\":1,\"name\":\"" + name + "\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":null," +
        "\"archive\":\"" + archive + "\",\"sha256\":\"" + sha + "\",\"installType\":\"BepInExPlugin\"," +
        "\"dependencies\":[],\"conflicts\":[]}]}";

    private static HttpResponse Json(string body) =>
        new(200, System.Text.Encoding.UTF8.GetBytes(body), null);

    private static byte[] MakePayload(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i % 251);
        return bytes;
    }

    private sealed class RecordingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "modpack-tests-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return File.ReadAllBytes(path);
    }

    private static string Sha(byte[] content)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(content)).ToLowerInvariant();
    }
}
