namespace TCGCardShopSimModManager.Core;

public enum ModInventoryState
{
    /// <summary>Present in the game folder but never journaled by us (installed by hand, or by another tool).</summary>
    Unknown,
    /// <summary>In the game folder and matching the journal.</summary>
    Installed,
    /// <summary>Moved to the disabled folder by us; the journal still holds its original paths.</summary>
    Disabled,
    /// <summary>A file differs from the journal (tampered, updated by hand, or partially deleted).</summary>
    Modified
}

public sealed record DiscoveredMod(
    string ModName,
    ModInventoryState State,
    int FileCount,
    string? ActiveRoot);

public enum ModInventorySortOrder
{
    Name,
    State,
    Location
}

public static class ModInventoryOrdering
{
    public static IReadOnlyList<DiscoveredMod> FilterAndSort(
        IEnumerable<DiscoveredMod> mods,
        string? search,
        ModInventoryState? state,
        ModInventorySortOrder order)
    {
        var term = search?.Trim();
        var visible = mods
            .Where(mod => string.IsNullOrWhiteSpace(term) ||
                mod.ModName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (mod.ActiveRoot?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(mod => state is null || mod.State == state);

        return Sort(visible, order);
    }

    public static IReadOnlyList<DiscoveredMod> Sort(
        IEnumerable<DiscoveredMod> mods,
        ModInventorySortOrder order)
    {
        return order switch
        {
            ModInventorySortOrder.State => mods
                .OrderBy(mod => StateRank(mod.State))
                .ThenBy(mod => mod.ModName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.ActiveRoot, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ModInventorySortOrder.Location => mods
                .OrderBy(mod => mod.ActiveRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.ModName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.State)
                .ToList(),
            _ => mods
                .OrderBy(mod => mod.ModName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.ActiveRoot, StringComparer.OrdinalIgnoreCase)
                .ThenBy(mod => mod.State)
                .ToList()
        };
    }

    private static int StateRank(ModInventoryState state) => state switch
    {
        ModInventoryState.Installed => 0,
        ModInventoryState.Disabled => 1,
        ModInventoryState.Modified => 2,
        _ => 3
    };
}

/// <summary>
/// Builds inventory from journal ownership first, then adds physical content
/// which no journal claims. This keeps one managed mod together even when its
/// files span several roots, while unmanaged folders retain their locations so
/// same-named folders are never silently merged.
/// </summary>
public static class ModDiscovery
{
    private static readonly (string Relative, string Label)[] FolderRoots =
    {
        ("BepInEx/plugins", "BepInEx/plugins"),
        ("BepInEx/patchers", "BepInEx/patchers")
    };

    public static List<DiscoveredMod> Discover(
        string gameFolderPath,
        string? disabledRoot = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var legacyDisabledRoot = disabledRoot is null ? ModInstaller.DisabledRoot : null;
        disabledRoot ??= ModInstaller.DisabledRootFor(gameFolderPath);
        var journal = new JournalStore(gameFolderPath).Load();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var discovered = new List<DiscoveredMod>();

        foreach (var entry in journal)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in entry.Files)
            {
                claimed.Add(Normalize(file.Path));
                if (DisabledPath(file.Path, gameFolderPath, disabledRoot, legacyDisabledRoot) is { } disabledPath)
                    claimed.Add(Normalize(disabledPath));
            }

            discovered.Add(FromJournal(
                entry, gameFolderPath, disabledRoot, legacyDisabledRoot, cancellationToken));
        }

        foreach (var (relative, label) in FolderRoots)
            AddUnmanagedFolders(
                discovered, claimed, Path.Combine(gameFolderPath, ToNative(relative)), label,
                cancellationToken);

        AddUnmanagedFramework(discovered, claimed, gameFolderPath, cancellationToken);
        AddUnmanagedFolders(discovered, claimed, disabledRoot, "Disabled storage", cancellationToken);

        return discovered
            .OrderBy(mod => mod.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.ActiveRoot, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DiscoveredMod FromJournal(
        InstallJournalEntry entry,
        string gameFolderPath,
        string disabledRoot,
        string? legacyDisabledRoot,
        CancellationToken cancellationToken)
    {
        var active = 0;
        var disabled = 0;
        var modified = entry.Files.Count == 0;

        foreach (var expected in entry.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var activeExists = File.Exists(expected.Path);
            var disabledPath = DisabledPath(
                expected.Path, gameFolderPath, disabledRoot, legacyDisabledRoot);
            var disabledExists = disabledPath is not null && File.Exists(disabledPath);

            if (activeExists && disabledExists)
            {
                modified = true;
                active++;
                disabled++;
                continue;
            }

            if (activeExists)
            {
                active++;
                modified |= !HashMatches(expected.Path, expected.Sha256, cancellationToken);
            }
            else if (disabledExists)
            {
                disabled++;
                modified |= !HashMatches(disabledPath!, expected.Sha256, cancellationToken);
            }
            else
            {
                modified = true;
            }
        }

        var state = modified || (active > 0 && disabled > 0)
            ? ModInventoryState.Modified
            : disabled == entry.Files.Count
                ? ModInventoryState.Disabled
                : ModInventoryState.Installed;

        return new DiscoveredMod(entry.ModName, state, active + disabled, JournalLocation(entry, gameFolderPath));
    }

    private static void AddUnmanagedFolders(
        List<DiscoveredMod> discovered,
        HashSet<string> claimed,
        string root,
        string label,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return file;
                })
                .Where(file => !claimed.Contains(Normalize(file)))
                .ToList();
            if (files.Count == 0)
                continue;

            discovered.Add(new DiscoveredMod(
                $"{Path.GetFileName(folder)} (unmanaged, {label})",
                ModInventoryState.Unknown,
                files.Count,
                label));
        }

        var looseFiles = Directory.EnumerateFiles(root)
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return file;
            })
            .Count(file => !claimed.Contains(Normalize(file)));
        if (looseFiles > 0)
        {
            discovered.Add(new DiscoveredMod(
                $"Loose files (unmanaged, {label})",
                ModInventoryState.Unknown,
                looseFiles,
                label));
        }
    }

    private static void AddUnmanagedFramework(
        List<DiscoveredMod> discovered,
        HashSet<string> claimed,
        string gameFolderPath,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(gameFolderPath, "BepInEx", "core");
        if (!Directory.Exists(root))
            return;

        var count = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return file;
            })
            .Count(file => !claimed.Contains(Normalize(file)));
        if (count > 0)
        {
            discovered.Add(new DiscoveredMod(
                "BepInEx framework files (unmanaged)",
                ModInventoryState.Unknown,
                count,
                "BepInEx/core"));
        }
    }

    private static string JournalLocation(InstallJournalEntry entry, string gameFolderPath)
    {
        var game = Normalize(gameFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var roots = entry.Files.Select(file =>
        {
            var full = Normalize(file.Path);
            if (!full.StartsWith(game, StringComparison.OrdinalIgnoreCase))
                return "Outside game folder";

            var parts = full[game.Length..].Replace('\\', '/').Split('/');
            if (parts.Length == 1)
                return "Game root";
            if (parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && parts.Length > 1)
                return $"BepInEx/{parts[1]}";
            return parts[0];
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return roots.Count switch
        {
            0 => "Journal",
            1 => roots[0],
            _ => "Multiple locations"
        };
    }

    private static string? DisabledPath(
        string filePath,
        string gameFolderPath,
        string disabledRoot,
        string? legacyDisabledRoot)
    {
        var game = Normalize(gameFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Normalize(filePath);
        if (!full.StartsWith(game, StringComparison.OrdinalIgnoreCase))
            return null;

        var sections = full[game.Length..].Replace('\\', '/').Split('/');
        if (sections.Length < 3 ||
            !sections[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
            !(sections[1].Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
              sections[1].Equals("patchers", StringComparison.OrdinalIgnoreCase)))
            return null;

        var suffix = sections.Skip(2).ToArray();
        var primary = Path.Combine(new[] { disabledRoot }.Concat(suffix).ToArray());
        if (File.Exists(primary) || legacyDisabledRoot is null)
            return primary;

        var legacy = Path.Combine(new[] { legacyDisabledRoot }.Concat(suffix).ToArray());
        return File.Exists(legacy) ? legacy : primary;
    }

    private static string ToNative(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static string Normalize(string path) => Path.GetFullPath(path);

    private static bool HashMatches(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        using var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        using var stream = File.OpenRead(path);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.AppendData(buffer, 0, read);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var actual = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
