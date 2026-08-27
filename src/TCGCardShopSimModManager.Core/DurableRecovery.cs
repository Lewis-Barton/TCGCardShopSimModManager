using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

internal enum RecoveryKind
{
    Deployment,
    Pack,
    PackSwitch
}

internal sealed record RecoveryFile(string Path, string? BackupFile);

internal sealed record RecoveryState(
    int Version,
    RecoveryKind Kind,
    DateTimeOffset CreatedAt,
    bool Committed,
    List<InstallJournalEntry> OriginalJournal,
    List<InstalledModpack>? OriginalPackJournal,
    string? PackId,
    List<RecoveryFile> Files);

internal sealed class DurableRecoveryTransaction : IDisposable
{
    private const int CurrentVersion = 1;
    private const string RecoveryFolderName = ".cardshopmodmanager-recovery";
    private const string StateFileName = "transaction.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _gameFolderPath;
    private readonly string _transactionRoot;
    private RecoveryState _state;
    private bool _finished;

    private DurableRecoveryTransaction(
        string gameFolderPath, string transactionRoot, RecoveryState state)
    {
        _gameFolderPath = Path.GetFullPath(gameFolderPath);
        _transactionRoot = transactionRoot;
        _state = state;
    }

    public static DurableRecoveryTransaction CaptureDeployment(
        string gameFolderPath, IEnumerable<string> affectedPaths)
    {
        var journal = new JournalStore(gameFolderPath).Load();
        return Capture(
            gameFolderPath,
            RecoveryKind.Deployment,
            journal,
            originalPackJournal: null,
            packId: null,
            affectedPaths);
    }

    public static DurableRecoveryTransaction CapturePack(string gameFolderPath, string packId)
    {
        var journal = new JournalStore(gameFolderPath).Load();
        var packJournal = new ModpackJournalStore(gameFolderPath).Load();
        var paths = journal
            .Where(entry => entry.PackId?.Equals(packId, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(entry => entry.Files)
            .Select(file => ExistingPath(file.Path, gameFolderPath))
            .Where(path => path is not null)
            .Select(path => path!);

        return Capture(
            gameFolderPath,
            RecoveryKind.Pack,
            journal,
            packJournal,
            packId,
            paths);
    }

    public static DurableRecoveryTransaction CapturePackSwitch(string gameFolderPath)
    {
        var journal = new JournalStore(gameFolderPath).Load();
        var packJournal = new ModpackJournalStore(gameFolderPath).Load();
        var paths = journal
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PackId))
            .SelectMany(entry => entry.Files)
            .Select(file => ExistingPath(file.Path, gameFolderPath))
            .Where(path => path is not null)
            .Select(path => path!);

        return Capture(
            gameFolderPath,
            RecoveryKind.PackSwitch,
            journal,
            packJournal,
            packId: null,
            paths);
    }

    public static void RecoverPending(string gameFolderPath)
    {
        var recoveryRoot = RecoveryRoot(gameFolderPath);
        if (!Directory.Exists(recoveryRoot))
            return;

        PathSafety.EnsureContainedWithoutReparsePoints(
            gameFolderPath, recoveryRoot, "Recovery directory");

        var transactions = new List<(string Path, RecoveryState? State)>();
        foreach (var path in Directory.EnumerateDirectories(recoveryRoot))
        {
            PathSafety.EnsureContainedWithoutReparsePoints(
                gameFolderPath, path, "Recovery transaction directory");
            transactions.Add((path, TryReadState(path)));
        }
        transactions = transactions
            .OrderByDescending(item => item.State?.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();

        foreach (var transaction in transactions)
        {
            if (transaction.State is null)
            {
                DeleteRecoveryBestEffort(transaction.Path);
                continue;
            }

            var current = new DurableRecoveryTransaction(
                gameFolderPath, transaction.Path, transaction.State);
            if (transaction.State.Committed)
            {
                current._finished = true;
                current.Dispose();
                continue;
            }

            var errors = current.Rollback();
            if (errors.Count > 0)
                throw new IOException(
                    "An interrupted mod manager operation could not be recovered: " +
                    string.Join("; ", errors));
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(recoveryRoot).Any())
                Directory.Delete(recoveryRoot);
        }
        catch
        {
            // An empty recovery root is harmless.
        }
    }

    public void Commit()
    {
        if (_finished)
            return;

        _state = _state with { Committed = true };
        WriteState(_transactionRoot, _state);
        _finished = true;
        Dispose();
    }

    public List<string> Rollback()
    {
        if (_finished || _state.Committed)
            return new List<string>();

        var errors = new List<string>();
        if (_state.Kind == RecoveryKind.Pack)
            DeleteCurrentPackFiles(errors);
        else if (_state.Kind == RecoveryKind.PackSwitch)
            DeleteCurrentHostedPackFiles(errors);

        foreach (var file in _state.Files.AsEnumerable().Reverse())
        {
            try
            {
                ValidateManagedPath(_gameFolderPath, file.Path, "Recovery target");
                if (file.BackupFile is not null)
                {
                    var backupPath = BackupPath(file.BackupFile);
                    if (!File.Exists(file.Path) || !FilesMatch(backupPath, file.Path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
                        File.Copy(backupPath, file.Path, overwrite: true);
                    }
                }
                else if (File.Exists(file.Path))
                    File.Delete(file.Path);
            }
            catch (Exception ex)
            {
                errors.Add($"Could not restore {file.Path}: {ex.Message}");
            }
        }

        try { new JournalStore(_gameFolderPath).Save(_state.OriginalJournal); }
        catch (Exception ex) { errors.Add($"Could not restore the install journal: {ex.Message}"); }

        if (_state.OriginalPackJournal is not null)
        {
            try { new ModpackJournalStore(_gameFolderPath).Save(_state.OriginalPackJournal); }
            catch (Exception ex) { errors.Add($"Could not restore the pack journal: {ex.Message}"); }
        }

        if (errors.Count == 0)
            Commit();
        return errors;
    }

    public void Dispose()
    {
        if (_finished || _state.Committed)
            DeleteRecoveryBestEffort(_transactionRoot);
    }

    private static DurableRecoveryTransaction Capture(
        string gameFolderPath,
        RecoveryKind kind,
        List<InstallJournalEntry> originalJournal,
        List<InstalledModpack>? originalPackJournal,
        string? packId,
        IEnumerable<string> affectedPaths)
    {
        var gameRoot = Path.GetFullPath(gameFolderPath);
        var transactionRoot = Path.Combine(
            RecoveryRoot(gameRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(transactionRoot, "files"));
        PathSafety.EnsureContainedWithoutReparsePoints(
            gameRoot, transactionRoot, "Recovery transaction directory");

        try
        {
            var files = new List<RecoveryFile>();
            foreach (var path in affectedPaths
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ValidateManagedPath(gameRoot, path, "Recovery target");
                if (!File.Exists(path))
                {
                    files.Add(new RecoveryFile(path, null));
                    continue;
                }

                var backupFile = $"files/{files.Count + 1}";
                var backupPath = Path.Combine(
                    transactionRoot, "files", (files.Count + 1).ToString());
                File.Copy(path, backupPath);
                using (var backup = new FileStream(
                           backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                    backup.Flush(flushToDisk: true);
                files.Add(new RecoveryFile(path, backupFile));
            }

            var state = new RecoveryState(
                CurrentVersion,
                kind,
                DateTimeOffset.UtcNow,
                Committed: false,
                originalJournal,
                originalPackJournal,
                packId,
                files);
            WriteState(transactionRoot, state);
            return new DurableRecoveryTransaction(gameRoot, transactionRoot, state);
        }
        catch
        {
            DeleteRecoveryBestEffort(transactionRoot);
            throw;
        }
    }

    private void DeleteCurrentPackFiles(List<string> errors)
    {
        try
        {
            var originalPaths = _state.Files
                .Select(file => Path.GetFullPath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = new JournalStore(_gameFolderPath).Load()
                .Where(entry => entry.PackId?.Equals(
                    _state.PackId, StringComparison.OrdinalIgnoreCase) == true);
            foreach (var file in entries.SelectMany(entry => entry.Files))
            {
                if (file.PreserveOnUninstall)
                    continue;
                if (!originalPaths.Contains(Path.GetFullPath(file.Path)))
                    TryDelete(file.Path, errors);
                if (DisabledPath(file.Path, _gameFolderPath) is { } disabledPath)
                    if (!originalPaths.Contains(Path.GetFullPath(disabledPath)))
                        TryDelete(disabledPath, errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Could not inspect the changed pack files: {ex.Message}");
        }
    }

    private void DeleteCurrentHostedPackFiles(List<string> errors)
    {
        try
        {
            var originalPaths = _state.Files
                .Select(file => Path.GetFullPath(file.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = new JournalStore(_gameFolderPath).Load()
                .Where(entry => !string.IsNullOrWhiteSpace(entry.PackId));
            foreach (var file in entries.SelectMany(entry => entry.Files))
            {
                if (file.PreserveOnUninstall)
                    continue;
                if (!originalPaths.Contains(Path.GetFullPath(file.Path)))
                    TryDelete(file.Path, errors);
                if (DisabledPath(file.Path, _gameFolderPath) is { } disabledPath)
                    if (!originalPaths.Contains(Path.GetFullPath(disabledPath)))
                        TryDelete(disabledPath, errors);
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Could not inspect the changed hosted-pack files: {ex.Message}");
        }
    }

    private string BackupPath(string backupFile)
    {
        var path = Path.GetFullPath(Path.Combine(
            _transactionRoot, backupFile.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _transactionRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery backup path escapes its transaction directory.");
        PathSafety.EnsureContainedWithoutReparsePoints(
            _transactionRoot, path, "Recovery backup path");
        return path;
    }

    private static RecoveryState? TryReadState(string transactionRoot)
    {
        var statePath = Path.Combine(transactionRoot, StateFileName);
        if (!File.Exists(statePath))
            return null;

        PathSafety.EnsureContainedWithoutReparsePoints(
            transactionRoot, statePath, "Recovery state file");

        try
        {
            var state = JsonSerializer.Deserialize<RecoveryState>(
                File.ReadAllText(statePath), Options);
            if (state is null || state.Version != CurrentVersion)
                throw new InvalidDataException("Unsupported recovery record version.");
            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Recovery record is corrupt: {statePath}", ex);
        }
    }

    private static void WriteState(string transactionRoot, RecoveryState state)
    {
        Directory.CreateDirectory(transactionRoot);
        var statePath = Path.Combine(transactionRoot, StateFileName);
        PathSafety.EnsureContainedWithoutReparsePoints(
            transactionRoot, statePath, "Recovery state file");
        var temporaryPath = statePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* best effort */ }
        }
    }

    private static string RecoveryRoot(string gameFolderPath) =>
        Path.Combine(Path.GetFullPath(gameFolderPath), RecoveryFolderName);

    private static void DeleteRecoveryBestEffort(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return;

            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         path, "*", SearchOption.AllDirectories))
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                    return;

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A committed record may remain for cleanup on the next operation.
        }
    }

    private static void ValidateManagedPath(
        string gameFolderPath, string path, string description)
    {
        if (IsUnder(gameFolderPath, path))
        {
            PathSafety.EnsureContainedWithoutReparsePoints(gameFolderPath, path, description);
            return;
        }

        foreach (var disabledRoot in new[]
                 {
                     ModInstaller.DisabledRootFor(gameFolderPath),
                     ModInstaller.DisabledRoot
                 })
        {
            if (!IsUnder(disabledRoot, path))
                continue;
            PathSafety.EnsureContainedWithoutReparsePoints(disabledRoot, path, description);
            return;
        }

        throw new InvalidDataException($"{description} is outside managed storage: {path}");
    }

    private static bool IsUnder(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidatePath)
            .StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExistingPath(string path, string gameFolderPath)
    {
        if (File.Exists(path))
            return path;
        var disabledPath = DisabledPath(path, gameFolderPath);
        return disabledPath is not null && File.Exists(disabledPath) ? disabledPath : null;
    }

    private static string? DisabledPath(string path, string gameFolderPath)
    {
        var gameRoot = Path.GetFullPath(gameFolderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = fullPath[gameRoot.Length..].Replace('\\', '/').Split('/');
        if (parts.Length < 3 ||
            !parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
            !(parts[1].Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
              parts[1].Equals("patchers", StringComparison.OrdinalIgnoreCase)))
            return null;

        var suffix = parts.Skip(2).ToArray();
        var primary = Path.Combine(
            new[] { ModInstaller.DisabledRootFor(gameFolderPath) }.Concat(suffix).ToArray());
        if (File.Exists(primary))
            return primary;

        var legacy = Path.Combine(new[] { ModInstaller.DisabledRoot }.Concat(suffix).ToArray());
        return File.Exists(legacy) ? legacy : primary;
    }

    private void TryDelete(string path, List<string> errors)
    {
        try
        {
            ValidateManagedPath(_gameFolderPath, path, "Recovery target");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add($"Could not remove changed file {path}: {ex.Message}");
        }
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
            return false;

        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return System.Security.Cryptography.SHA256.HashData(first)
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }
}
