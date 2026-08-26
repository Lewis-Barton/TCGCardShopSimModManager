using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

public sealed class JournalStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AtomicJsonFile<List<InstallJournalEntry>> _file;
    private readonly string _gameFolderPath;

    public JournalStore(string gameFolderPath)
    {
        _gameFolderPath = Path.GetFullPath(gameFolderPath);
        _file = new AtomicJsonFile<List<InstallJournalEntry>>(
            Path.Combine(gameFolderPath, "cardshopmodmanager.journal.json"),
            Options, () => new List<InstallJournalEntry>(), recoverCorrupt: true);
    }

    public List<InstallJournalEntry> Load()
    {
        return _file.UpdateIfChanged(entries =>
        {
            var resolved = ResolveForUse(entries);
            var stored = PrepareForStorage(resolved);
            var pathsMatch = entries.SelectMany(entry => entry.Files).Select(file => file.Path)
                .SequenceEqual(stored.SelectMany(entry => entry.Files).Select(file => file.Path),
                    StringComparer.Ordinal);
            return (stored, resolved, !pathsMatch);
        });
    }

    public void Save(List<InstallJournalEntry> entries)
    {
        _file.Write(PrepareForStorage(entries));
    }

    public void Add(InstallJournalEntry entry)
    {
        _file.Update(entries =>
        {
            entries = ResolveForUse(entries);
            var resolvedEntry = ResolveForUse([entry]).Single();
            entries.RemoveAll(e =>
                (!string.IsNullOrWhiteSpace(resolvedEntry.ModId) &&
                 !string.IsNullOrWhiteSpace(e.ModId) &&
                 e.ModId.Equals(resolvedEntry.ModId, StringComparison.OrdinalIgnoreCase)) ||
                (string.IsNullOrWhiteSpace(e.ModId) &&
                 e.ModName.Equals(resolvedEntry.ModName, StringComparison.OrdinalIgnoreCase)));
            entries.Add(resolvedEntry);
            return (PrepareForStorage(entries), true);
        });
    }

    public void Remove(string modName)
    {
        _file.Update(entries =>
        {
            entries = ResolveForUse(entries);
            entries.RemoveAll(e => e.ModName == modName);
            return (PrepareForStorage(entries), true);
        });
    }

    private List<InstallJournalEntry> ResolveForUse(List<InstallJournalEntry> entries)
    {
        return entries.Select(entry => entry with
        {
            Files = entry.Files.Select(file => file with
            {
                Path = ResolvePath(file.Path, entry.ModName)
            }).ToList()
        }).ToList();
    }

    private List<InstallJournalEntry> PrepareForStorage(List<InstallJournalEntry> entries)
    {
        var resolved = ResolveForUse(entries);
        return resolved.Select(entry => entry with
        {
            Files = entry.Files.Select(file => file with
            {
                Path = Path.GetRelativePath(_gameFolderPath, file.Path)
            }).ToList()
        }).ToList();
    }

    private string ResolvePath(string storedPath, string modName)
    {
        string fullPath;
        try
        {
            fullPath = Path.IsPathRooted(storedPath)
                ? Path.GetFullPath(storedPath)
                : Path.GetFullPath(storedPath, _gameFolderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Install journal contains an invalid path for {modName}.", ex);
        }

        if (!IsContained(fullPath) && Path.IsPathRooted(storedPath))
            fullPath = TryRebaseLegacyPath(fullPath) ?? fullPath;

        if (!IsContained(fullPath))
            throw new InvalidDataException(
                $"Install journal path for {modName} escapes the game folder: {storedPath}");

        PathSafety.EnsureContainedWithoutReparsePoints(
            _gameFolderPath, fullPath, $"Install journal path for {modName}");
        return fullPath;
    }

    private string? TryRebaseLegacyPath(string fullPath)
    {
        var gameFolderName = Path.GetFileName(
            _gameFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(gameFolderName))
            return null;

        var parts = fullPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var gameFolderIndex = Array.FindLastIndex(parts,
            part => part.Equals(gameFolderName, StringComparison.OrdinalIgnoreCase));
        if (gameFolderIndex < 0 || gameFolderIndex == parts.Length - 1)
            return null;

        var relative = Path.Combine(parts[(gameFolderIndex + 1)..]);
        var candidate = Path.GetFullPath(relative, _gameFolderPath);
        return IsContained(candidate) ? candidate : null;
    }

    private bool IsContained(string fullPath)
    {
        var prefix = Path.EndsInDirectorySeparator(_gameFolderPath)
            ? _gameFolderPath
            : _gameFolderPath + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
