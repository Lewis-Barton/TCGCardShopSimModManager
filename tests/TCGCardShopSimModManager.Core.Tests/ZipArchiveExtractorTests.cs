using System.IO.Compression;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ZipArchiveExtractorTests : IDisposable
{
    private readonly string _root;
    private readonly string _destination;

    public ZipArchiveExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "zip-tests-" + Guid.NewGuid().ToString("N"));
        _destination = Path.Combine(_root, "out");
        Directory.CreateDirectory(_destination);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Supports_AcceptsCommonArchiveExtensions()
    {
        var extractor = new ZipArchiveExtractor();
        Assert.Equal(".zip", extractor.FileExtension);
        Assert.True(ArchiveExtractor.IsSupportedArchive("mods/pack.ZIP"));
        foreach (var extension in new[] { ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" })
            Assert.True(ArchiveExtractor.IsSupportedArchive("mods/pack" + extension));
        Assert.False(ArchiveExtractor.IsSupportedArchive("mods/pack.exe"));
    }

    [Fact]
    public void DefaultProtection_AllowsLargeGameModArchives()
    {
        Assert.True(ArchiveProtectionSettings.Default.MaxSingleFileBytes >= 32L * 1024 * 1024 * 1024);
        Assert.True(ArchiveProtectionSettings.Default.MaxTotalBytes >= 64L * 1024 * 1024 * 1024);
        Assert.True(ArchiveProtectionSettings.Default.MaxEntries >= 100000);
    }

    [Fact]
    public void Extract_ReturnsFilesWithRelativePaths()
    {
        var zip = CreateZip(entries: ("BepInEx/plugins/Mod.dll", "dll-bytes"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        var source = Assert.Single(result.Sources);
        Assert.Equal("BepInEx/plugins/Mod.dll", source.RelativePath);
        Assert.True(File.Exists(source.AbsolutePath));
        Assert.Empty(result.RejectedEntries);
    }

    [Fact]
    public void Extract_SkipsDirectoryEntries()
    {
        var zip = CreateZip(
            ("BepInEx/", ""),
            ("BepInEx/plugins/", ""),
            ("BepInEx/plugins/Mod.dll", "dll-bytes"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        Assert.Single(result.Sources);
        Assert.Equal("BepInEx/plugins/Mod.dll", result.Sources[0].RelativePath);
    }

    [Fact]
    public void Extract_RejectsParentDirectoryTraversal()
    {
        // A hostile archive smuggling an entry out of the extraction folder.
        var zip = CreateZip(("../evil.dll", "oops"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        Assert.Empty(result.Sources);
        Assert.Contains(result.RejectedEntries, r => r.Contains("unsafe path"));
        Assert.False(File.Exists(Path.Combine(_destination, "../evil.dll")));
    }

    [Fact]
    public void Extract_RejectsRootedAndBackslashTraversal()
    {
        var zip = CreateZip(
            ("C:/evil.dll", "oops"),
            ("..\\..\\evil2.dll", "oops"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        Assert.Empty(result.Sources);
        Assert.Equal(2, result.RejectedEntries.Count);
    }

    [Theory]
    [InlineData("payload.dll:stream")]
    [InlineData("CON.dll")]
    [InlineData("folder./payload.dll")]
    [InlineData("folder /payload.dll")]
    public void Extract_RejectsWindowsPathAliases(string entryName)
    {
        var zip = CreateZip((entryName, "oops"));

        var result = new ZipArchiveExtractor().Extract(
            zip, _destination, ArchiveProtectionSettings.Default);

        Assert.Empty(result.Sources);
        Assert.Contains(result.RejectedEntries, entry => entry.Contains("unsafe path"));
    }

    [Fact]
    public void Extract_RejectsSymbolicLinkEntries()
    {
        var zipPath = Path.Combine(_root, "symlink.zip");
        using (var file = File.Create(zipPath))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link.dll");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000); // Unix S_IFLNK | 0777
            using var writer = new StreamWriter(entry.Open());
            writer.Write("target-file.dll");
        }

        var result = new ZipArchiveExtractor().Extract(zipPath, _destination, ArchiveProtectionSettings.Default);

        Assert.Empty(result.Sources);
        Assert.Contains(result.RejectedEntries, r => r.Contains("symbolic-link"));
    }

    [Fact]
    public void Extract_RejectsUnexpectedExecutables()
    {
        var zip = CreateZip(
            ("Mod.dll", "dll-bytes"),
            ("evil.exe", "trample"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        var source = Assert.Single(result.Sources); // only the DLL gets through
        Assert.Equal("Mod.dll", source.RelativePath);
        Assert.Contains(result.RejectedEntries, r => r.Contains("rejected file type") && r.Contains("evil.exe"));
        Assert.False(File.Exists(Path.Combine(_destination, "evil.exe")));
    }

    [Fact]
    public void Extract_RejectsFileOverTheSizeCap()
    {
        var settings = ArchiveProtectionSettings.Default with { MaxSingleFileBytes = 8 };
        var zip = CreateZip(("big.bin", new string('x', 100)));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, settings);

        Assert.Empty(result.Sources);
        Assert.Contains(result.RejectedEntries, r => r.Contains("too large"));
    }

    [Fact]
    public void Extract_RejectsNestedZip()
    {
        // A nested archive would bypass every protection check below, so it is
        // refused rather than written out as a payload.
        var zip = CreateZip(
            ("BepInEx/plugins/Mod/mod.dll", "dll-bytes"),
            ("BepInEx/plugins/Mod/inner.zip", "zip-bytes"));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default);

        var source = Assert.Single(result.Sources); // only the DLL makes it through
        Assert.Equal("BepInEx/plugins/Mod/mod.dll", source.RelativePath);
        Assert.Contains(result.RejectedEntries, r => r.Contains("rejected file type") && r.Contains("inner.zip"));
    }

    [Fact]
    public void Extract_FlagsTruncationWhenSizeCapHit()
    {
        var settings = ArchiveProtectionSettings.Default with { MaxTotalBytes = 20 };
        var zip = CreateZip(
            ("a.txt", new string('x', 10)),
            ("b.txt", new string('x', 10)),
            ("c.txt", new string('x', 10)));

        var result = new ZipArchiveExtractor().Extract(zip, _destination, settings);

        Assert.True(result.Truncated);
        Assert.Contains(result.RejectedEntries, r => r.Contains("exceeds limit"));
    }

    [Fact]
    public void Extract_RejectsDuplicateEntriesWithinOneArchive()
    {
        // Two entries with the same name: the first lands, the second is a
        // duplicate and cannot silently overwrite it.
        var path = Path.Combine(_root, "dup.zip");
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var content in new[] { "first", "second" })
            {
                var entry = archive.CreateEntry("same.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        var result = new ZipArchiveExtractor().Extract(path, _destination, ArchiveProtectionSettings.Default);

        var source = Assert.Single(result.Sources);
        Assert.Equal("first", File.ReadAllText(source.AbsolutePath));
        Assert.Contains(result.RejectedEntries, r => r.Contains("duplicate or conflicting"));
    }

    [Fact]
    public void Extract_DoesNotReportDirectoryWriteFailureAsDuplicate()
    {
        var zip = CreateZip(("blocked/file.txt", "content"));
        File.WriteAllText(Path.Combine(_destination, "blocked"), "not a directory");

        var exception = Assert.Throws<IOException>(() =>
            new ZipArchiveExtractor().Extract(zip, _destination, ArchiveProtectionSettings.Default));

        Assert.DoesNotContain("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "zip-tests-" + Guid.NewGuid().ToString("N") + ".zip");
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

        return path;
    }
}
