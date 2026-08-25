using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModpackUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_FindsNewerFileWithSameDisplayName()
    {
        var checker = Checker(
            new NexusFileInfo(100, "cards-1.0.zip", "1.0", 10, "Card set", "OLD_VERSION"),
            new NexusFileInfo(101, "other.zip", "4.0", 10, "Optional artwork", "OPTIONAL"),
            new NexusFileInfo(102, "cards-1.1.zip", "1.1", 10, "Card set", "MAIN"));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("cards", 12, 100))));

        Assert.Equal(ModpackUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(102, result.SuggestedFile?.FileId);
    }

    [Fact]
    public async Task CheckAsync_DoesNotSuggestUnrelatedFileFromSameMod()
    {
        var checker = Checker(
            new NexusFileInfo(100, "cards.zip", "1.0", 10, "Card set", "MAIN"),
            new NexusFileInfo(120, "art.zip", "2.0", 10, "Optional artwork", "OPTIONAL"));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("cards", 12, 100))));

        Assert.Equal(ModpackUpdateStatus.Current, result.Status);
        Assert.Null(result.SuggestedFile);
    }

    [Fact]
    public async Task CheckAsync_ReportsMissingPinnedFile()
    {
        var checker = Checker(new NexusFileInfo(120, "cards.zip", "2.0", 10, "Card set", "MAIN"));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("cards", 12, 100))));

        Assert.Equal(ModpackUpdateStatus.MissingOrArchived, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ReportsPinnedArchivedFile()
    {
        var checker = Checker(new NexusFileInfo(100, "cards.zip", "1.0", 10, "Card set", "ARCHIVED"));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("cards", 12, 100))));

        Assert.Equal(ModpackUpdateStatus.MissingOrArchived, result.Status);
    }

    [Fact]
    public async Task CheckAsync_DoesNotSuggestRetiredReplacement()
    {
        var checker = Checker(
            new NexusFileInfo(100, "cards.zip", "1.0", 10, "Card set", "MAIN"),
            new NexusFileInfo(120, "cards-old.zip", "1.1", 10, "Card set", "OLD_VERSION"));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("cards", 12, 100))));

        Assert.Equal(ModpackUpdateStatus.Current, result.Status);
    }

    [Fact]
    public async Task CheckAsync_ListsEachNexusModOnce()
    {
        var calls = 0;
        var checker = new ModpackUpdateChecker((_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<NexusFileInfo>>(
            [
                new(100, "one.zip", "1.0", 10, "One", "MAIN"),
                new(101, "two.zip", "1.0", 10, "Two", "MAIN")
            ]);
        });

        var results = await checker.CheckAsync(Manifest(Mod("one", 12, 100), Mod("two", 12, 101)));

        Assert.Equal(2, results.Count);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CheckAsync_SkipsEntryWithoutNexusIds()
    {
        var checker = new ModpackUpdateChecker((_, _) =>
            throw new InvalidOperationException("Nexus should not be called."));

        var result = Assert.Single(await checker.CheckAsync(Manifest(Mod("local", null, null))));

        Assert.Equal(ModpackUpdateStatus.NotChecked, result.Status);
    }

    private static ModpackUpdateChecker Checker(params NexusFileInfo[] files) =>
        new((_, _) => Task.FromResult<IReadOnlyList<NexusFileInfo>>(files));

    private static ModListManifest Manifest(params ModEntry[] mods) =>
        new(1, "Test pack", "Test game", mods.ToList());

    private static ModEntry Mod(string id, long? modId, long? fileId) =>
        new(id, id, "1.0", $"{id}.zip", new string('a', 64), "BepInExPlugin", [], [], modId, fileId);
}
