using System.Text.Json;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class GameOperationLockTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cardshop-lock-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Acquire_RefusesASecondOperationForTheSameGame()
    {
        Directory.CreateDirectory(_root);
        using var first = GameOperationLock.Acquire(_root);

        var error = Assert.Throws<IOException>(() =>
            GameOperationLock.Acquire(Path.Combine(_root, "."), TimeSpan.Zero));

        Assert.Contains("Another mod manager operation", error.Message);
    }

    [Fact]
    public void Acquire_AllowsAnotherOperationAfterRelease()
    {
        Directory.CreateDirectory(_root);
        using (GameOperationLock.Acquire(_root))
        {
        }

        using var next = GameOperationLock.Acquire(_root, TimeSpan.Zero);
    }

    [Fact]
    public void Acquire_PreservesRecoveryFailureInsteadOfReportingLockContention()
    {
        Directory.CreateDirectory(_root);
        var transactionRoot = Path.Combine(
            _root, ".cardshopmodmanager-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transactionRoot);
        var state = new RecoveryState(
            1,
            RecoveryKind.Deployment,
            DateTimeOffset.UtcNow,
            Committed: false,
            new List<InstallJournalEntry>(),
            OriginalPackJournal: null,
            PackId: null,
            new List<RecoveryFile>
            {
                new(Path.Combine(_root, "missing-target.dll"), "files/missing-backup")
            });
        File.WriteAllText(
            Path.Combine(transactionRoot, "transaction.json"),
            JsonSerializer.Serialize(state));

        var error = Assert.Throws<IOException>(() =>
            GameOperationLock.Acquire(_root, TimeSpan.Zero));

        Assert.Contains("interrupted mod manager operation could not be recovered", error.Message);
        Assert.DoesNotContain("Another mod manager operation", error.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
