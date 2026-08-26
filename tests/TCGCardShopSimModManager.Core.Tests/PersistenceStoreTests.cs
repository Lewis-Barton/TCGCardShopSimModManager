using System.Text.Json;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class PersistenceStoreTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(
        Path.GetTempPath(), "persistence-tests-" + Guid.NewGuid().ToString("N"));

    public PersistenceStoreTests() => Directory.CreateDirectory(_gameFolder);

    public void Dispose()
    {
        if (Directory.Exists(_gameFolder))
            Directory.Delete(_gameFolder, recursive: true);
    }

    [Fact]
    public async Task JournalStore_ConcurrentAddsRetainEveryEntry()
    {
        var tasks = Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            new JournalStore(_gameFolder).Add(new InstallJournalEntry(
                $"Mod {index}", DateTimeOffset.UtcNow, new List<JournalFileEntry>(), ModId: $"mod-{index}"))));

        await Task.WhenAll(tasks);

        Assert.Equal(20, new JournalStore(_gameFolder).Load().Count);
        AssertValidJson("cardshopmodmanager.journal.json", JsonValueKind.Array);
    }

    [Fact]
    public void JournalStore_StoresRelativePathsAndResolvesThemForUse()
    {
        var installedPath = Path.Combine(_gameFolder, "BepInEx", "plugins", "Example.dll");
        var store = new JournalStore(_gameFolder);

        store.Add(new InstallJournalEntry(
            "Example", DateTimeOffset.UtcNow,
            [new JournalFileEntry(installedPath, "hash")]));

        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_gameFolder, "cardshopmodmanager.journal.json")));
        var storedPath = document.RootElement[0].GetProperty("Files")[0].GetProperty("Path").GetString();
        Assert.Equal(Path.Combine("BepInEx", "plugins", "Example.dll"), storedPath);
        Assert.Equal(installedPath, store.Load().Single().Files.Single().Path);
    }

    [Fact]
    public void JournalStore_RebasesLegacyAbsolutePathsAfterGameFolderMoves()
    {
        var previousGameFolder = Path.Combine(
            Path.GetTempPath(), "old-library", Path.GetFileName(_gameFolder));
        var previousPath = Path.Combine(previousGameFolder, "BepInEx", "plugins", "Example.dll");
        var journalPath = Path.Combine(_gameFolder, "cardshopmodmanager.journal.json");
        File.WriteAllText(journalPath, JsonSerializer.Serialize(new[]
        {
            new InstallJournalEntry(
                "Example", DateTimeOffset.UtcNow,
                [new JournalFileEntry(previousPath, "hash")])
        }));

        var loadedPath = new JournalStore(_gameFolder).Load().Single().Files.Single().Path;

        Assert.Equal(Path.Combine(_gameFolder, "BepInEx", "plugins", "Example.dll"), loadedPath);
        using var migrated = JsonDocument.Parse(File.ReadAllText(journalPath));
        Assert.Equal(
            Path.Combine("BepInEx", "plugins", "Example.dll"),
            migrated.RootElement[0].GetProperty("Files")[0].GetProperty("Path").GetString());
    }

    [Fact]
    public async Task ModpackJournalStore_ConcurrentRecordsRetainEveryPack()
    {
        var tasks = Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            new ModpackJournalStore(_gameFolder).Record($"pack-{index}", "1.0.0", $"Pack {index}")));

        await Task.WhenAll(tasks);

        Assert.Equal(20, new ModpackJournalStore(_gameFolder).Load().Count);
        AssertValidJson("cardshopmodmanager.modpacks.json", JsonValueKind.Array);
    }

    [Fact]
    public async Task ProfilesStore_ConcurrentEnablesRetainEveryId()
    {
        var tasks = Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            new ProfilesStore(_gameFolder).Enable($"mod-{index}")));

        await Task.WhenAll(tasks);

        Assert.Equal(20, new ProfilesStore(_gameFolder).EnabledIdsOrAll()!.Count);
        AssertValidJson("cardshopmodmanager.profiles.json", JsonValueKind.Object);
    }

    [Fact]
    public void ProfilesStore_CorruptFileFailsClosed()
    {
        var path = Path.Combine(_gameFolder, "cardshopmodmanager.profiles.json");
        File.WriteAllText(path, "{ not valid json");

        Assert.Throws<JsonException>(() => new ProfilesStore(_gameFolder).Load());
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Save_ReplacesFileAndKeepsPreviousVersion()
    {
        var store = new ProfilesStore(_gameFolder);
        store.Enable("mod-a");
        var path = store.FilePath;
        var previous = File.ReadAllText(path);

        store.Enable("mod-b");

        Assert.Equal(previous, File.ReadAllText(path + ".bak"));
        Assert.Empty(Directory.GetFiles(_gameFolder, "*.tmp"));
    }

    private void AssertValidJson(string fileName, JsonValueKind expectedKind)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_gameFolder, fileName)));
        Assert.Equal(expectedKind, document.RootElement.ValueKind);
    }
}
