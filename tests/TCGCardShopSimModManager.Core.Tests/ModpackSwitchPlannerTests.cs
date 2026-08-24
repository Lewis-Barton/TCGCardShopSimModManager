using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModpackSwitchPlannerTests
{
    [Fact]
    public void Create_ClassifiesRetainedUpdatedRemovedAndAddedMods()
    {
        var installed = new[]
        {
            Installed("shared", "Shared", "same"),
            Installed("updated", "Updated", "old"),
            Installed("removed", "Removed", "removed"),
            Installed(null, "Legacy", null)
        };
        var target = new[]
        {
            Mod("shared", "Shared", "same"),
            Mod("updated", "Updated", "new"),
            Mod("added", "Added", "added")
        };

        var plan = ModpackSwitchPlanner.Create(installed, target);

        Assert.Equal(["Shared"], plan.Retained);
        Assert.Equal(["Updated"], plan.Updated);
        Assert.Equal(["Removed", "Legacy"], plan.Removed);
        Assert.Equal(["Added"], plan.Added);
    }

    private static InstallJournalEntry Installed(string? id, string name, string? hash) =>
        new(name, DateTimeOffset.UtcNow, new List<JournalFileEntry>(),
            PackId: "old-pack", ModId: id, ArchiveSha256: hash);

    private static ModEntry Mod(string id, string name, string hash) =>
        new(id, name, "1.0.0", $"{id}.zip", hash, "BepInExPlugin",
            new List<string>(), new List<string>());
}
