using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

public sealed record ModpackSaveProfileInfo(int FileCount, long SizeBytes)
{
    public bool HasSaves => FileCount > 0;
}

public sealed record ModpackSaveStorageInfo(int ProfileCount, int FileCount, long SizeBytes);

public sealed record StoredModpackSaveProfile(
    string PackId,
    int FileCount,
    long SizeBytes);

public sealed record ModpackSaveStorageClearResult(
    long FreedBytes,
    int DeletedFiles,
    IReadOnlyList<string> Errors);

internal sealed record ModpackSaveProfileMetadata(string PackId);

internal sealed record ModpackSaveSwapState(
    int Version,
    string CurrentPackId,
    string TargetPackId);

public sealed class ModpackSaveProfileManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _saveDirectory;
    private readonly string _storageRoot;
    private readonly Func<bool> _isGameRunning;

    public static string DefaultSaveDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "OPNeonGames", "Card Shop Simulator");

    public static string DefaultStorageRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TCGCardShopSimModManager", "save-profiles");

    public ModpackSaveProfileManager(
        string? saveDirectory = null,
        string? storageRoot = null,
        Func<bool>? isGameRunning = null)
    {
        _saveDirectory = Path.GetFullPath(saveDirectory ?? DefaultSaveDirectory);
        _storageRoot = Path.GetFullPath(storageRoot ?? DefaultStorageRoot);
        _isGameRunning = isGameRunning ?? IsGameRunning;
    }

    public ModpackSaveProfileInfo Inspect(string packId)
    {
        if (Directory.Exists(_storageRoot))
            RejectReparsePath(_storageRoot, "Save-profile storage");
        var profileDirectory = ProfileDirectory(packId);
        if (Directory.Exists(profileDirectory))
            RejectReparsePath(profileDirectory, "Save profile");
        var files = SaveFiles(profileDirectory);
        return new ModpackSaveProfileInfo(files.Count, files.Sum(path => new FileInfo(path).Length));
    }

    public ModpackSaveStorageInfo InspectStorage()
    {
        if (!Directory.Exists(_storageRoot))
            return new ModpackSaveStorageInfo(0, 0, 0);
        RejectReparsePath(_storageRoot, "Save-profile storage");

        var profiles = 0;
        var files = 0;
        long size = 0;
        foreach (var directory in ProfileDirectories())
        {
            RejectReparsePath(directory, "Save profile");
            var profileFiles = SaveFiles(directory);
            if (profileFiles.Count == 0)
                continue;
            profiles++;
            files += profileFiles.Count;
            size += profileFiles.Sum(path => new FileInfo(path).Length);
        }
        return new ModpackSaveStorageInfo(profiles, files, size);
    }

    public IReadOnlyList<StoredModpackSaveProfile> ListStoredProfiles()
    {
        if (!Directory.Exists(_storageRoot))
            return Array.Empty<StoredModpackSaveProfile>();
        RejectReparsePath(_storageRoot, "Save-profile storage");

        var profiles = new List<StoredModpackSaveProfile>();
        foreach (var directory in ProfileDirectories())
        {
            RejectReparsePath(directory, "Save profile");
            var files = SaveFiles(directory);
            if (files.Count == 0)
                continue;

            var metadataPath = Path.Combine(directory, "profile.json");
            ModpackSaveProfileMetadata? metadata;
            try
            {
                var metadataFile = new FileInfo(metadataPath);
                if (!metadataFile.Exists ||
                    metadataFile.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    metadataFile.Length > 16 * 1024)
                    throw new InvalidDataException(
                        $"Stored save-profile metadata is missing or unsafe: {metadataPath}");
                metadata = JsonSerializer.Deserialize<ModpackSaveProfileMetadata>(
                    File.ReadAllText(metadataPath), JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                throw new InvalidDataException(
                    $"Stored save-profile metadata is unreadable: {metadataPath}", ex);
            }
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.PackId) ||
                !Path.GetFullPath(ProfileDirectory(metadata.PackId)).Equals(
                    Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Stored save-profile metadata does not match its directory: {metadataPath}");

            profiles.Add(new StoredModpackSaveProfile(
                metadata.PackId,
                files.Count,
                files.Sum(path => new FileInfo(path).Length)));
        }
        return profiles.OrderBy(profile => profile.PackId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ModpackSaveStorageClearResult DeleteStoredProfile(string packId)
    {
        var directory = ProfileDirectory(packId);
        if (!Directory.Exists(directory))
            return new ModpackSaveStorageClearResult(0, 0, Array.Empty<string>());
        RejectReparsePath(_storageRoot, "Save-profile storage");

        using var operationLock = AcquireOperationLock();
        var errors = new List<string>();
        var result = ClearProfileDirectory(directory, errors);
        return new ModpackSaveStorageClearResult(result.FreedBytes, result.DeletedFiles, errors);
    }

    public ModpackSaveStorageClearResult ClearStorage()
    {
        if (!Directory.Exists(_storageRoot))
            return new ModpackSaveStorageClearResult(0, 0, Array.Empty<string>());
        RejectReparsePath(_storageRoot, "Save-profile storage");

        using var operationLock = AcquireOperationLock();
        long freed = 0;
        var deleted = 0;
        var errors = new List<string>();
        foreach (var directory in ProfileDirectories())
        {
            var result = ClearProfileDirectory(directory, errors);
            freed += result.FreedBytes;
            deleted += result.DeletedFiles;
        }
        return new ModpackSaveStorageClearResult(freed, deleted, errors);
    }

    public ModpackSaveProfileTransaction BeginSwap(string currentPackId, string targetPackId)
    {
        if (string.IsNullOrWhiteSpace(currentPackId) || string.IsNullOrWhiteSpace(targetPackId))
            throw new InvalidDataException("Both modpack ids are required for save swapping.");
        if (currentPackId.Equals(targetPackId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save swapping requires two different modpacks.");
        if (_isGameRunning())
            throw new InvalidOperationException(
                "Close TCG Card Shop Simulator before swapping modpack saves.");

        Directory.CreateDirectory(_saveDirectory);
        Directory.CreateDirectory(_storageRoot);
        RejectReparsePath(_saveDirectory, "Game save directory");
        RejectReparsePath(_storageRoot, "Save-profile storage");

        var operationLock = AcquireOperationLock();

        var currentProfile = ProfileDirectory(currentPackId);
        var targetProfile = ProfileDirectory(targetPackId);
        var transactionRoot = Path.Combine(_storageRoot, ".transactions", Guid.NewGuid().ToString("N"));
        try
        {
            RecoverPending(currentPackId);
            Directory.CreateDirectory(currentProfile);
            Directory.CreateDirectory(targetProfile);
            Directory.CreateDirectory(transactionRoot);
            RejectReparsePath(currentProfile, "Current save profile");
            RejectReparsePath(targetProfile, "Target save profile");

            WriteProfileMetadata(currentProfile, currentPackId);
            WriteProfileMetadata(targetProfile, targetPackId);
            Snapshot(_saveDirectory, Path.Combine(transactionRoot, "active"));
            Snapshot(currentProfile, Path.Combine(transactionRoot, "current"));
            Snapshot(targetProfile, Path.Combine(transactionRoot, "target"));
            WriteTransactionState(transactionRoot, currentPackId, targetPackId);

            var transaction = new ModpackSaveProfileTransaction(
                _saveDirectory, currentProfile, targetProfile, transactionRoot, operationLock);
            try
            {
                ReplaceSaveFiles(currentProfile, _saveDirectory);
                ReplaceSaveFiles(_saveDirectory, targetProfile);
                return transaction;
            }
            catch
            {
                var rollbackErrors = transaction.Rollback();
                if (rollbackErrors.Count > 0)
                    throw new IOException(
                        "Save-profile swapping failed and could not be fully rolled back: " +
                        string.Join("; ", rollbackErrors));
                throw;
            }
        }
        catch
        {
            operationLock.Dispose();
            throw;
        }
    }

    private string ProfileDirectory(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId))
            throw new InvalidDataException("Modpack id is required.");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packId.Trim().ToLowerInvariant())))
            .ToLowerInvariant();
        return Path.Combine(_storageRoot, hash);
    }

    private IEnumerable<string> ProfileDirectories() =>
        Directory.EnumerateDirectories(_storageRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => IsProfileDirectoryName(Path.GetFileName(path)));

    private static bool IsProfileDirectoryName(string name) =>
        name.Length == 64 && name.All(Uri.IsHexDigit);

    private static (long FreedBytes, int DeletedFiles) ClearProfileDirectory(
        string directory, List<string> errors)
    {
        long freed = 0;
        var deleted = 0;
        try
        {
            RejectReparsePath(directory, "Save profile");
            foreach (var path in SaveFiles(directory))
            {
                var length = new FileInfo(path).Length;
                File.Delete(path);
                freed += length;
                deleted++;
            }

            var metadata = Path.Combine(directory, "profile.json");
            if (File.Exists(metadata) &&
                !File.GetAttributes(metadata).HasFlag(FileAttributes.ReparsePoint))
                File.Delete(metadata);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch (Exception ex)
        {
            errors.Add($"Could not clear save profile {Path.GetFileName(directory)}: {ex.Message}");
        }
        return (freed, deleted);
    }

    private FileStream AcquireOperationLock()
    {
        Directory.CreateDirectory(_storageRoot);
        var lockPath = Path.Combine(_storageRoot, ".save-profiles.lock");
        try
        {
            return new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new IOException("Another save-profile operation is already running. Try again when it finishes.");
        }
    }

    private static void WriteProfileMetadata(string directory, string packId)
    {
        var path = Path.Combine(directory, "profile.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new ModpackSaveProfileMetadata(packId), JsonOptions));
    }

    private static void WriteTransactionState(
        string transactionRoot, string currentPackId, string targetPackId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ModpackSaveSwapState(1, currentPackId, targetPackId), JsonOptions);
        using var stream = new FileStream(
            Path.Combine(transactionRoot, "transaction.json"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private void RecoverPending(string installedPackId)
    {
        var transactionsRoot = Path.Combine(_storageRoot, ".transactions");
        if (!Directory.Exists(transactionsRoot))
            return;
        RejectReparsePath(transactionsRoot, "Save-profile transactions");

        foreach (var transactionRoot in Directory.EnumerateDirectories(transactionsRoot))
        {
            RejectReparsePath(transactionRoot, "Save-profile transaction");
            var statePath = Path.Combine(transactionRoot, "transaction.json");
            if (!File.Exists(statePath))
            {
                TemporaryDirectory.DeleteBestEffort(transactionRoot);
                continue;
            }

            ModpackSaveSwapState? state;
            try
            {
                state = JsonSerializer.Deserialize<ModpackSaveSwapState>(
                    File.ReadAllText(statePath), JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new IOException(
                    $"An interrupted save-profile marker is unreadable: {statePath}", ex);
            }

            if (state is null || state.Version != 1)
                throw new IOException($"An interrupted save-profile marker is unsupported: {statePath}");

            if (state.TargetPackId.Equals(installedPackId, StringComparison.OrdinalIgnoreCase))
            {
                // The modpack transaction committed before this cleanup ran, so
                // the target saves are already the correct active set.
                TemporaryDirectory.DeleteBestEffort(transactionRoot);
                continue;
            }
            if (!state.CurrentPackId.Equals(installedPackId, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "An interrupted save-profile transaction does not match the installed modpack.");

            var errors = new List<string>();
            RestoreSnapshot(_saveDirectory, Path.Combine(transactionRoot, "active"), errors);
            RestoreSnapshot(
                ProfileDirectory(state.CurrentPackId), Path.Combine(transactionRoot, "current"), errors);
            RestoreSnapshot(
                ProfileDirectory(state.TargetPackId), Path.Combine(transactionRoot, "target"), errors);
            if (errors.Count > 0)
                throw new IOException(
                    "An interrupted save-profile switch could not be recovered: " +
                    string.Join("; ", errors));
            TemporaryDirectory.DeleteBestEffort(transactionRoot);
        }
    }

    private static void RestoreSnapshot(
        string destination, string snapshot, List<string> errors)
    {
        try
        {
            if (!Directory.Exists(snapshot))
                throw new DirectoryNotFoundException($"Recovery snapshot not found: {snapshot}");
            ReplaceSaveFiles(destination, snapshot);
        }
        catch (Exception ex)
        {
            errors.Add($"Could not restore {destination}: {ex.Message}");
        }
    }

    internal static void ReplaceSaveFiles(string destination, string source)
    {
        Directory.CreateDirectory(destination);
        foreach (var existing in SaveFiles(destination))
            File.Delete(existing);
        foreach (var sourceFile in SaveFiles(source))
            CopyFileDurably(sourceFile, Path.Combine(destination, Path.GetFileName(sourceFile)));
    }

    private static void Snapshot(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in SaveFiles(source))
            CopyFileDurably(path, Path.Combine(destination, Path.GetFileName(path)));
    }

    internal static List<string> SaveFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<string>();
        RejectReparsePath(directory, "Save directory");

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Save file cannot be a link: {file.Name}");
            if (file.Extension.Equals(".gd", StringComparison.OrdinalIgnoreCase) &&
                (file.Name.StartsWith("savedGames_Release", StringComparison.OrdinalIgnoreCase) ||
                 file.Name.StartsWith("savedGames_BackupFile", StringComparison.OrdinalIgnoreCase)))
                files.Add(path);
        }
        return files;
    }

    private static void CopyFileDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.WriteThrough);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void RejectReparsePath(string path, string label)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    $"{label} cannot pass through a symbolic link or junction: {current.FullName}");
        }
    }

    private static bool IsGameRunning()
    {
        try
        {
            return Process.GetProcessesByName(
                Path.GetFileNameWithoutExtension(SteamLocator.GameExecutableName)).Length > 0;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ModpackSaveProfileTransaction : IDisposable
{
    private readonly string _saveDirectory;
    private readonly string _currentProfile;
    private readonly string _targetProfile;
    private readonly string _transactionRoot;
    private readonly FileStream _operationLock;
    private bool _finished;

    internal ModpackSaveProfileTransaction(
        string saveDirectory,
        string currentProfile,
        string targetProfile,
        string transactionRoot,
        FileStream operationLock)
    {
        _saveDirectory = saveDirectory;
        _currentProfile = currentProfile;
        _targetProfile = targetProfile;
        _transactionRoot = transactionRoot;
        _operationLock = operationLock;
    }

    public void Commit()
    {
        if (_finished)
            return;
        _finished = true;
        Cleanup();
    }

    public IReadOnlyList<string> Rollback()
    {
        if (_finished)
            return Array.Empty<string>();

        var errors = new List<string>();
        Restore(_saveDirectory, "active", errors);
        Restore(_currentProfile, "current", errors);
        Restore(_targetProfile, "target", errors);
        if (errors.Count == 0)
        {
            _finished = true;
            Cleanup();
        }
        return errors;
    }

    public void Dispose()
    {
        if (!_finished)
            Rollback();
        if (!_finished)
            _operationLock.Dispose();
    }

    private void Restore(string destination, string snapshotName, List<string> errors)
    {
        try
        {
            ModpackSaveProfileManager.ReplaceSaveFiles(
                destination, Path.Combine(_transactionRoot, snapshotName));
        }
        catch (Exception ex)
        {
            errors.Add($"Could not restore {destination}: {ex.Message}");
        }
    }

    private void Cleanup()
    {
        _operationLock.Dispose();
        TemporaryDirectory.DeleteBestEffort(_transactionRoot);
    }
}
