using System.IO.Compression;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class SupportBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "support-bundle-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_IncludesGameStateAndPendingRecoveryRecord()
    {
        var gameFolder = Path.Combine(_root, "game");
        var outputFolder = Path.Combine(_root, "output");
        Directory.CreateDirectory(gameFolder);
        File.WriteAllText(Path.Combine(gameFolder, "cardshopmodmanager.journal.json"), "[]");
        File.WriteAllText(Path.Combine(gameFolder, "cardshopmodmanager.modpacks.json"), "[]");
        File.WriteAllText(Path.Combine(gameFolder, "cardshopmodmanager.profiles.json"), "{}");
        var recoveryFolder = Path.Combine(
            gameFolder, ".cardshopmodmanager-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(recoveryFolder);
        File.WriteAllText(Path.Combine(recoveryFolder, "transaction.json"), "{\"Version\":1}");

        var bundlePath = SupportBundle.Create(gameFolder, outputFolder);

        using var bundle = ZipFile.OpenRead(bundlePath);
        var names = bundle.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("journal.json", names);
        Assert.Contains("modpacks.json", names);
        Assert.Contains("profiles.json", names);
        Assert.Contains("recovery/transaction-1.json", names);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
