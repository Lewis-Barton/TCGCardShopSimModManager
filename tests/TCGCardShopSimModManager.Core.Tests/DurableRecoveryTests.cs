using System.Security.Cryptography;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class DurableRecoveryTests : IDisposable
{
    private readonly string _root;

    public DurableRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "recovery-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void NextOperationRemovesUnjournaledFileFromInterruptedDeployment()
    {
        var target = Path.Combine(_root, "BepInEx", "plugins", "New Mod", "new.dll");
        _ = DurableRecoveryTransaction.CaptureDeployment(_root, [target]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "partial install");

        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.False(File.Exists(target));
        Assert.Empty(new JournalStore(_root).Load());
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void NextOperationRestoresFileAndJournalFromInterruptedUpdate()
    {
        var target = Path.Combine(_root, "BepInEx", "plugins", "Example", "example.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "version one");
        var original = Entry("example", "Example", "1.0.0", target);
        new JournalStore(_root).Save([original]);
        _ = DurableRecoveryTransaction.CaptureDeployment(_root, [target]);

        File.WriteAllText(target, "version two");
        new JournalStore(_root).Save([Entry("example", "Example", "2.0.0", target)]);

        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.Equal("version one", File.ReadAllText(target));
        Assert.Equal("1.0.0", Assert.Single(new JournalStore(_root).Load()).Version);
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void CommittedDeploymentIsNotRolledBack()
    {
        var target = Path.Combine(_root, "BepInEx", "plugins", "Example", "example.dll");
        var transaction = DurableRecoveryTransaction.CaptureDeployment(_root, [target]);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "installed");
        new JournalStore(_root).Save([Entry("example", "Example", "1.0.0", target)]);
        transaction.Commit();

        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.Equal("installed", File.ReadAllText(target));
        Assert.Single(new JournalStore(_root).Load());
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void NextOperationRestoresInterruptedPackFilesAndBothJournals()
    {
        var requiredPath = Path.Combine(_root, "BepInEx", "plugins", "Required", "required.dll");
        var optionalPath = Path.Combine(_root, "BepInEx", "plugins", "Optional", "optional.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(requiredPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(optionalPath)!);
        File.WriteAllText(requiredPath, "required v1");
        File.WriteAllText(optionalPath, "optional v1");
        var originalJournal = new List<InstallJournalEntry>
        {
            Entry("required", "Required", "1.0.0", requiredPath, "pack"),
            Entry("optional", "Optional", "1.0.0", optionalPath, "pack")
        };
        new JournalStore(_root).Save(originalJournal);
        new ModpackJournalStore(_root).Record("pack", "1.0.0", "Pack", ["optional"]);
        _ = DurableRecoveryTransaction.CapturePack(_root, "pack");

        File.WriteAllText(requiredPath, "required v2");
        File.Delete(optionalPath);
        new JournalStore(_root).Save(
            [Entry("required", "Required", "2.0.0", requiredPath, "pack")]);
        new ModpackJournalStore(_root).Record("pack", "2.0.0", "Pack", []);

        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.Equal("required v1", File.ReadAllText(requiredPath));
        Assert.Equal("optional v1", File.ReadAllText(optionalPath));
        Assert.Equal(2, new JournalStore(_root).Load().Count);
        var pack = Assert.Single(new ModpackJournalStore(_root).Load());
        Assert.Equal("1.0.0", pack.PackVersion);
        Assert.Equal(["optional"], pack.SelectedOptionalModIds);
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void PackRecoveryDoesNotRewriteUnchangedLockedOriginalFile()
    {
        var target = Path.Combine(_root, "winhttp.dll");
        File.WriteAllText(target, "original loader");
        var original = Entry("bepinex", "BepInEx", "1.0.0", target, "pack");
        new JournalStore(_root).Save([original]);
        new ModpackJournalStore(_root).Record("pack", "1.0.0", "Pack", []);
        _ = DurableRecoveryTransaction.CapturePack(_root, "pack");

        using var locked = new FileStream(
            target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.Equal("original loader", File.ReadAllText(target));
        Assert.Single(new JournalStore(_root).Load());
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void PackSwitchRecoveryDoesNotRewriteUnchangedLockedOriginalFile()
    {
        var target = Path.Combine(_root, "winhttp.dll");
        File.WriteAllText(target, "original loader");
        var original = Entry("bepinex", "BepInEx", "1.0.0", target, "pack");
        new JournalStore(_root).Save([original]);
        new ModpackJournalStore(_root).Record("pack", "1.0.0", "Pack", []);
        _ = DurableRecoveryTransaction.CapturePackSwitch(_root);

        using var locked = new FileStream(
            target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var operation = GameOperationLock.Acquire(_root, TimeSpan.Zero);

        Assert.Equal("original loader", File.ReadAllText(target));
        Assert.Single(new JournalStore(_root).Load());
        AssertRecoveryStorageClean();
    }

    [Fact]
    public void TamperedRecoveryRecordCannotTouchFileOutsideManagedStorage()
    {
        var target = Path.Combine(_root, "BepInEx", "plugins", "Example", "example.dll");
        _ = DurableRecoveryTransaction.CaptureDeployment(_root, [target]);
        var external = Path.Combine(Path.GetDirectoryName(_root)!, "recovery-external.txt");
        File.WriteAllText(external, "untouched");
        var statePath = Directory.EnumerateFiles(
            Path.Combine(_root, ".cardshopmodmanager-recovery"),
            "transaction.json",
            SearchOption.AllDirectories).Single();
        var json = File.ReadAllText(statePath).Replace(
            target.Replace("\\", "\\\\"),
            external.Replace("\\", "\\\\"));
        File.WriteAllText(statePath, json);

        Assert.Throws<IOException>(() =>
            GameOperationLock.Acquire(_root, TimeSpan.Zero));

        Assert.Equal("untouched", File.ReadAllText(external));
        File.Delete(external);
    }

    private void AssertRecoveryStorageClean()
    {
        var recoveryRoot = Path.Combine(_root, ".cardshopmodmanager-recovery");
        Assert.True(
            !Directory.Exists(recoveryRoot) ||
            !Directory.EnumerateFileSystemEntries(recoveryRoot).Any());
    }

    private static InstallJournalEntry Entry(
        string id, string name, string version, string path, string? packId = null) =>
        new(
            name,
            DateTimeOffset.UtcNow,
            [new JournalFileEntry(path, Sha(path))],
            PackId: packId,
            ModId: id,
            Version: version,
            ArchiveSha256: new string(version[0], 64));

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
