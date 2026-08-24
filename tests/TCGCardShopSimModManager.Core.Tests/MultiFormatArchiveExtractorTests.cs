using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class MultiFormatArchiveExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "multi-archive-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Extract_TarGzAppliesProtectionRules()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "pack.tar.gz");
        CreateTarGz(archive,
            ("BepInEx/plugins/Example/Example.dll", "plugin"),
            ("BepInEx/plugins/Example/setup.exe", "executable"));
        var destination = Path.Combine(_root, "out");

        var result = ArchiveExtractor.Extract(archive, destination);

        var source = Assert.Single(result.Sources);
        Assert.Equal("BepInEx/plugins/Example/Example.dll", source.RelativePath);
        Assert.Equal("plugin", File.ReadAllText(source.AbsolutePath));
        Assert.Contains(result.RejectedEntries,
            entry => entry.Contains("setup.exe") && entry.Contains("rejected file type"));
        Assert.False(File.Exists(Path.Combine(destination, "BepInEx/plugins/Example/setup.exe")));
    }

    [Fact]
    public void Extract_TarRejectsDuplicateEntriesWithoutDeletingFirstFile()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "duplicate.tar");
        CreateTar(archive, ("same.txt", "first"), ("same.txt", "second"));
        var destination = Path.Combine(_root, "out");

        var result = ArchiveExtractor.Extract(archive, destination);

        var source = Assert.Single(result.Sources);
        Assert.Equal("first", File.ReadAllText(source.AbsolutePath));
        Assert.Contains(result.RejectedEntries, entry => entry.Contains("duplicate or conflicting"));
    }

    [Fact]
    public void Extract_TarRejectsParentDirectoryTraversal()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "traversal.tar");
        CreateTar(archive, ("../outside.dll", "unsafe"));
        var destination = Path.Combine(_root, "out");

        var result = ArchiveExtractor.Extract(archive, destination);

        Assert.Empty(result.Sources);
        Assert.Contains(result.RejectedEntries, entry => entry.Contains("unsafe path"));
        Assert.False(File.Exists(Path.Combine(_root, "outside.dll")));
    }

    [Fact]
    public void Extract_SevenZipReturnsFiles()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "pack.7z");
        using (var file = File.Create(archive))
        using (var writer = WriterFactory.OpenWriter(
                   file,
                   ArchiveType.SevenZip,
                   new SevenZipWriterOptions(CompressionType.LZMA2)))
        using (var source = new MemoryStream(Encoding.UTF8.GetBytes("plugin")))
        {
            writer.Write("BepInEx/plugins/Example.dll", source, DateTime.UtcNow);
        }
        var destination = Path.Combine(_root, "out");

        var result = ArchiveExtractor.Extract(archive, destination);

        var extracted = Assert.Single(result.Sources);
        Assert.Equal("BepInEx/plugins/Example.dll", extracted.RelativePath);
        Assert.Equal("plugin", File.ReadAllText(extracted.AbsolutePath));
        Assert.Empty(result.RejectedEntries);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void CreateTarGz(string path, params (string Name, string Content)[] entries)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        WriteTar(gzip, entries);
    }

    private static void CreateTar(string path, params (string Name, string Content)[] entries)
    {
        using var file = File.Create(path);
        WriteTar(file, entries);
    }

    private static void WriteTar(Stream stream, IEnumerable<(string Name, string Content)> entries)
    {
        using var writer = new TarWriter(stream, leaveOpen: true);
        foreach (var (name, content) in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes)
            });
        }
    }
}
