using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModpackSaveProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cardshop-save-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SwapStoresCurrentSavesAndRestoresTargetProfile()
    {
        var saves = Path.Combine(_root, "saves");
        var storage = Path.Combine(_root, "storage");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "savedGames_Release0.gd"), "pack-a");
        File.WriteAllText(Path.Combine(saves, "savedGames_KeybindSetting.gd"), "keep");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);
        using (var firstSwap = manager.BeginSwap("pack-a", "pack-b"))
            firstSwap.Commit();

        Assert.Empty(ModpackSaveProfileManager.SaveFiles(saves));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(saves, "savedGames_KeybindSetting.gd")));
        File.WriteAllText(Path.Combine(saves, "savedGames_Release0.gd"), "pack-b");

        using (var returnSwap = manager.BeginSwap("pack-b", "pack-a"))
            returnSwap.Commit();

        Assert.Equal("pack-a", File.ReadAllText(Path.Combine(saves, "savedGames_Release0.gd")));
        Assert.True(manager.Inspect("pack-b").HasSaves);
    }

    [Fact]
    public void DisposingUncommittedSwapRestoresActiveAndStoredProfiles()
    {
        var saves = Path.Combine(_root, "rollback-saves");
        var storage = Path.Combine(_root, "rollback-storage");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "savedGames_Release0.gd"), "stored-b");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);
        using (var seed = manager.BeginSwap("pack-b", "pack-a"))
            seed.Commit();
        File.WriteAllText(Path.Combine(saves, "savedGames_Release0.gd"), "active-a");

        using (manager.BeginSwap("pack-a", "pack-b"))
        {
            Assert.Equal("stored-b", File.ReadAllText(
                Path.Combine(saves, "savedGames_Release0.gd")));
        }

        Assert.Equal("active-a", File.ReadAllText(Path.Combine(saves, "savedGames_Release0.gd")));
        Assert.False(manager.Inspect("pack-a").HasSaves);
    }

    [Fact]
    public void SwapRefusesWhileGameIsRunning()
    {
        var manager = new ModpackSaveProfileManager(
            Path.Combine(_root, "running-saves"), Path.Combine(_root, "running-storage"), () => true);

        var error = Assert.Throws<InvalidOperationException>(() => manager.BeginSwap("pack-a", "pack-b"));

        Assert.Contains("Close TCG Card Shop Simulator", error.Message);
    }

    [Fact]
    public void NextSwapRecoversInterruptedSaveTransactionFirst()
    {
        var saves = Path.Combine(_root, "recovery-saves");
        var storage = Path.Combine(_root, "recovery-storage");
        Directory.CreateDirectory(saves);
        var activeSave = Path.Combine(saves, "savedGames_Release0.gd");
        File.WriteAllText(activeSave, "original");
        var transaction = Path.Combine(storage, ".transactions", "pending");
        var activeSnapshot = Path.Combine(transaction, "active");
        Directory.CreateDirectory(activeSnapshot);
        Directory.CreateDirectory(Path.Combine(transaction, "current"));
        Directory.CreateDirectory(Path.Combine(transaction, "target"));
        File.Copy(activeSave, Path.Combine(activeSnapshot, Path.GetFileName(activeSave)));
        File.WriteAllText(
            Path.Combine(transaction, "transaction.json"),
            "{\"Version\":1,\"CurrentPackId\":\"pack-a\",\"TargetPackId\":\"pack-b\"}");
        File.WriteAllText(activeSave, "interrupted-target");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);

        using (manager.BeginSwap("pack-a", "pack-b"))
        {
        }

        Assert.Equal("original", File.ReadAllText(activeSave));
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public void PendingSaveCleanupKeepsTargetSavesWhenPackSwitchCommitted()
    {
        var saves = Path.Combine(_root, "committed-saves");
        var storage = Path.Combine(_root, "committed-storage");
        Directory.CreateDirectory(saves);
        var activeSave = Path.Combine(saves, "savedGames_Release0.gd");
        File.WriteAllText(activeSave, "target-progress");
        var transaction = Path.Combine(storage, ".transactions", "pending");
        Directory.CreateDirectory(Path.Combine(transaction, "active"));
        Directory.CreateDirectory(Path.Combine(transaction, "current"));
        Directory.CreateDirectory(Path.Combine(transaction, "target"));
        File.WriteAllText(
            Path.Combine(transaction, "transaction.json"),
            "{\"Version\":1,\"CurrentPackId\":\"pack-a\",\"TargetPackId\":\"pack-b\"}");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);

        using (manager.BeginSwap("pack-b", "pack-c"))
        {
        }

        Assert.Equal("target-progress", File.ReadAllText(activeSave));
        Assert.False(Directory.Exists(transaction));
    }

    [Fact]
    public void StorageInspectionAndClearCoverOnlyOwnedSaveProfiles()
    {
        var saves = Path.Combine(_root, "storage-management-saves");
        var storage = Path.Combine(_root, "storage-management");
        Directory.CreateDirectory(saves);
        File.WriteAllBytes(Path.Combine(saves, "savedGames_Release0.gd"), new byte[128]);
        File.WriteAllText(Path.Combine(saves, "savedGames_KeybindSetting.gd"), "keep");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);
        using (var swap = manager.BeginSwap("pack-a", "pack-b"))
            swap.Commit();
        var unexpected = Path.Combine(storage, "unexpected-folder");
        Directory.CreateDirectory(unexpected);
        File.WriteAllText(Path.Combine(unexpected, "keep.txt"), "keep");

        Assert.Equal(new ModpackSaveStorageInfo(1, 1, 128), manager.InspectStorage());
        var result = manager.ClearStorage();

        Assert.Equal(128, result.FreedBytes);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Empty(result.Errors);
        Assert.Equal(new ModpackSaveStorageInfo(0, 0, 0), manager.InspectStorage());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unexpected, "keep.txt")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(saves, "savedGames_KeybindSetting.gd")));
    }

    [Fact]
    public void StorageClearRefusesToRaceAnActiveSaveSwap()
    {
        var saves = Path.Combine(_root, "locked-storage-saves");
        var storage = Path.Combine(_root, "locked-storage");
        Directory.CreateDirectory(saves);
        File.WriteAllText(Path.Combine(saves, "savedGames_Release0.gd"), "pack-a");
        var manager = new ModpackSaveProfileManager(saves, storage, () => false);
        using var swap = manager.BeginSwap("pack-a", "pack-b");

        var error = Assert.Throws<IOException>(manager.ClearStorage);

        Assert.Contains("Another save-profile operation", error.Message);
    }

    public void Dispose() => TemporaryDirectory.DeleteBestEffort(_root);
}
