using System;
using System.Security.Cryptography;
using System.Text;

namespace TCGCardShopSimModManager.Core;

public sealed class ModInstaller
{
    private readonly JournalStore _journal;
    private readonly ModpackJournalStore _modpackJournal;
    private readonly string _gameFolderPath;
    private readonly string _disabledRoot;
    private readonly string? _legacyDisabledRoot;
    private readonly bool _operationLockHeld;

    /// <summary>
    /// Where disabled mods are parked while turned off. Lives beside the mod
    /// manager's own executable — NOT inside the game folder — so the game stays
    /// clean and BepInEx never loads the files. The folder is created on demand
    /// when a mod is first disabled. Defaults to <see cref="DisabledRoot"/>.
    /// Tests pass an explicit path so disabled mods stay inside the test's
    /// scratch folder and never touch the real install.
    /// </summary>
    public ModInstaller(string gameFolderPath, string? disabledRoot = null)
        : this(gameFolderPath, disabledRoot, operationLockHeld: false)
    {
    }

    internal ModInstaller(string gameFolderPath, string? disabledRoot, bool operationLockHeld)
    {
        _gameFolderPath = gameFolderPath;
        _journal = new JournalStore(gameFolderPath);
        _modpackJournal = new ModpackJournalStore(gameFolderPath);
        _disabledRoot = disabledRoot ?? DisabledRootFor(gameFolderPath);
        _legacyDisabledRoot = disabledRoot is null ? DisabledRoot : null;
        _operationLockHeld = operationLockHeld;
    }

    /// <summary>
    /// The default home for disabled mods: a folder next to this executable.
    /// Returning an absolute path means discovery and the installer agree on
    /// where disabled files live without the game folder being involved.
    /// </summary>
    public static string DisabledRoot =>
        Path.Combine(AppContext.BaseDirectory, "cardshopmodmanager-disabled");

    public static string DisabledRootFor(string gameFolderPath)
    {
        var normalized = Path.GetFullPath(gameFolderPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant()[..16];
        return Path.Combine(DisabledRoot, hash);
    }

    /// <summary>
    /// Build the file-by-file plan for a mod: verify the source hash, extract it
    /// into <paramref name="extractionRoot"/> (safely), and classify the layout.
    /// Throws on hash mismatch, corrupt archive, or an archive with nothing to install.
    /// </summary>
    public InstallPlan CreatePlan(ModEntry mod, string sourceDirectory, string extractionRoot,
        ArchiveProtectionSettings? settings = null)
    {
        var sourcePath = Path.Combine(sourceDirectory, mod.Archive);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Source file not found: {sourcePath}");

        var sourceHash = ComputeSha256(sourcePath);
        if (!sourceHash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Hash mismatch for {mod.Archive}: expected {mod.Sha256}, got {sourceHash}");

        if (ArchiveExtractor.IsSupportedArchive(sourcePath))
        {
            var result = ArchiveExtractor.Extract(sourcePath, extractionRoot, settings ?? ArchiveProtectionSettings.Default);
            if (result.Truncated)
            {
                var detail = result.RejectedEntries.Count > 0
                    ? RejectionSummary(result.RejectedEntries)
                    : "extraction stopped early";
                throw new InvalidDataException(
                    $"{mod.Archive}: extraction was truncated ({detail}) — refusing to install a partial copy.");
            }

            if (result.Sources.Count == 0)
            {
                var detail = result.RejectedEntries.Count > 0
                    ? RejectionSummary(result.RejectedEntries)
                    : "the archive is empty";
                throw new InvalidDataException($"{mod.Archive}: nothing could be extracted ({detail}).");
            }

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sources = result.Sources
                .Where(source =>
                {
                    var exclusion = MatchingArchiveExclusion(
                        source.RelativePath, mod.ExcludedArchivePaths);
                    if (exclusion is null)
                        return true;
                    excluded.Add($"{exclusion} (excluded by manifest)");
                    return false;
                })
                .ToList();
            var plan = new ArchiveClassifier().BuildPlan(mod, sources, result.RejectedEntries);
            return plan with { SkippedEntries = plan.SkippedEntries.Concat(excluded).ToList() };
        }

        // A plain loose file (e.g. a bare DLL) is treated as a one-file mod.
        var looseSource = new List<ExtractedSource> { new(mod.Archive, sourcePath) };
        return new ArchiveClassifier().BuildPlan(mod, looseSource);
    }

    private static string RejectionSummary(IReadOnlyList<string> rejected)
    {
        const int shownLimit = 10;
        var summary = string.Join("; ", rejected.Take(shownLimit));
        return rejected.Count <= shownLimit
            ? summary
            : $"{summary}; ... and {rejected.Count - shownLimit} more rejected entries";
    }

    public InstallResult Install(ModEntry mod, string sourceDirectory)
    {
        try
        {
            using var operation = EnterOperation();
            return InstallCore(mod, sourceDirectory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new InstallResult(false, ex.Message, null);
        }
    }

    private InstallResult InstallCore(ModEntry mod, string sourceDirectory)
    {
        if (mod.InstallType != "BepInExPlugin" && mod.InstallType != ModListConventions.BepInExInstallType)
            return new InstallResult(false, $"Unsupported install type: {mod.InstallType}", null);

        var workDir = Path.Combine(Path.GetTempPath(), "cardshopmodmanager-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var installedPaths = new List<string>();
        var changedPaths = new List<string>();
        var preservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var backups = new List<(string Original, string Backup)>();
        try
        {
            var plan = CreatePlan(mod, sourceDirectory, workDir);
            var rejected = plan.RejectedEntries;
            var skipped = plan.SkippedEntries;
            var existingEntry = FindJournalEntry(mod);
            var ownedPaths = existingEntry?.Files
                .ToDictionary(f => Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, JournalFileEntry>(StringComparer.OrdinalIgnoreCase);

            if (plan.Files.Count == 0)
                return new InstallResult(false,
                    $"{mod.Archive}: nothing to install (all content was documentation/OS junk)", null,
                    rejected, skipped);

            // An update may replace files owned by the previous journal entry, but
            // only while they still match what we installed. Everything else keeps
            // the normal no-overwrite rule.
            if (existingEntry is not null)
            {
                foreach (var file in existingEntry.Files.Where(f => File.Exists(f.Path)))
                {
                    if (!IsMutableRuntimeFile(file.Path) &&
                        !HashMatchesCurrent(file.Path, file.Sha256))
                        return new InstallResult(false,
                            $"Cannot update {mod.Name}: a managed file was modified: {file.Path}", null);
                }
            }

            var existing = new List<string>();
            foreach (var file in plan.Files)
            {
                var destination = PhysicalPath(_gameFolderPath, file.DestinationRelativePath);
                if (!File.Exists(destination))
                    continue;
                if (IsMutableConfiguration(destination))
                {
                    preservedPaths.Add(destination);
                    skipped.Add($"{file.DestinationRelativePath} (kept existing configuration)");
                    continue;
                }
                if (ownedPaths.ContainsKey(destination))
                    continue;
                if (HashesMatch(file.SourceAbsolutePath, destination))
                {
                    preservedPaths.Add(destination);
                    skipped.Add($"{file.DestinationRelativePath} (reused identical pre-existing file)");
                }
                else
                    existing.Add(file.DestinationRelativePath);
            }
            if (existing.Count > 0)
                return new InstallResult(false,
                    $"{mod.Archive}: destination already exists, refusing to overwrite: {existing[0]}", null);

            foreach (var file in plan.Files)
            {
                var destination = PhysicalPath(_gameFolderPath, file.DestinationRelativePath);
                if (ownedPaths.TryGetValue(destination, out var ownedFile) &&
                    ownedFile.PreserveOnUninstall &&
                    !IsMutableConfiguration(destination) &&
                    File.Exists(destination) &&
                    !HashesMatch(file.SourceAbsolutePath, destination))
                {
                    return new InstallResult(false,
                        $"Cannot update {mod.Name}: an adopted file would need to be replaced: {destination}", null);
                }
            }

            var duplicate = plan.Files
                .GroupBy(f => f.DestinationRelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                return new InstallResult(false,
                    $"{mod.Archive}: multiple files map to the same destination: {duplicate.Key}", null);

            if (existingEntry is not null)
            {
                var backupRoot = Path.Combine(workDir, "previous-install");
                Directory.CreateDirectory(backupRoot);
                var backupNumber = 0;
                foreach (var file in existingEntry.Files.Where(f =>
                             !f.PreserveOnUninstall && File.Exists(f.Path)))
                {
                    var backup = Path.Combine(backupRoot, (++backupNumber).ToString());
                    File.Copy(file.Path, backup);
                    backups.Add((file.Path, backup));
                }
            }

            foreach (var file in plan.Files)
            {
                var destinationPath = PhysicalPath(_gameFolderPath, file.DestinationRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

                if (preservedPaths.Contains(destinationPath) ||
                    (ownedPaths.TryGetValue(destinationPath, out var existingFile) &&
                     existingFile.PreserveOnUninstall && File.Exists(destinationPath)))
                {
                    preservedPaths.Add(destinationPath);
                    installedPaths.Add(destinationPath);
                    continue;
                }

                // Copy, then verify the copy landed intact before trusting it.
                installedPaths.Add(destinationPath);
                changedPaths.Add(destinationPath);
                File.Copy(file.SourceAbsolutePath, destinationPath, overwrite: ownedPaths.ContainsKey(destinationPath));
                if (!HashesMatch(file.SourceAbsolutePath, destinationPath))
                    throw new IOException($"Verification failed after copying {file.DestinationRelativePath}");
            }

            var installedSet = installedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var oldFile in ownedPaths
                         .Where(pair => !pair.Value.PreserveOnUninstall &&
                                        !installedSet.Contains(pair.Key) &&
                                        !IsMutableConfiguration(pair.Key))
                         .Select(pair => pair.Key))
                if (File.Exists(oldFile))
                    File.Delete(oldFile);

            // Hash each installed file so uninstall can later refuse to delete
            // anything that has been changed. Remember the pack id (if any) so
            // uninstall can clear a now-empty pack from the pack journal (BUG-005).
            _journal.Add(new InstallJournalEntry(
                plan.Mod.Name,
                DateTimeOffset.UtcNow,
                installedPaths.Select(p => new JournalFileEntry(
                    p, ComputeSha256(p), preservedPaths.Contains(p))).ToList(),
                PackId: mod.PackId,
                ModId: mod.Id,
                Version: mod.Version,
                ArchiveSha256: mod.Sha256));

            return new InstallResult(true, null, installedPaths, rejected, skipped);
        }
        catch (Exception ex)
        {
            // Roll back this install: delete exactly what this call created.
            foreach (var path in changedPaths)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                    // Best effort; the journal was never written so nothing claims
                    // these files were installed.
                }
            }

            foreach (var (original, backup) in backups)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Copy(backup, original, overwrite: true);
                }
                catch
                {
                    // Best effort. The original journal remains in place when
                    // an update fails, so later repair still has ownership data.
                }
            }

            return new InstallResult(false, $"Install failed: {ex.Message}", null);
        }
        finally
        {
            TemporaryDirectory.DeleteBestEffort(workDir);
        }
    }

    /// <summary>
    /// True when a mod is journaled as installed and every file it installed
    /// still exists. A stale journal entry (files manually deleted) counts as
    /// not installed, so a later install can restore it.
    /// </summary>
    public bool IsInstalled(string modName)
    {
        var entry = _journal.Load().FirstOrDefault(e => e.ModName == modName);
        return entry is not null && entry.Files.All(f => File.Exists(f.Path));
    }

    /// <summary>True when this exact archive is journaled and its files are present.</summary>
    public bool IsCurrent(ModEntry mod)
    {
        var entry = FindJournalEntry(mod);
        return entry is not null &&
               entry.ArchiveSha256?.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase) == true &&
               entry.Files.All(f => File.Exists(f.Path));
    }

    public bool HasJournalEntry(ModEntry mod) => FindJournalEntry(mod) is not null;

    public string? JournaledName(ModEntry mod) => FindJournalEntry(mod)?.ModName;

    public string? UpdateBlockReason(ModEntry mod)
    {
        var entry = FindJournalEntry(mod);
        if (entry is null)
            return null;

        var modified = entry.Files.FirstOrDefault(file =>
            File.Exists(file.Path) && !HashMatchesCurrent(file.Path, file.Sha256));
        return modified is null
            ? null
            : $"Cannot update {mod.Name}: a managed file was modified: {modified.Path}";
    }

    private InstallJournalEntry? FindJournalEntry(ModEntry mod)
    {
        var entries = _journal.Load();
        return entries.FirstOrDefault(e =>
                   !string.IsNullOrWhiteSpace(e.ModId) &&
                   e.ModId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
               ?? entries.FirstOrDefault(e =>
                   string.IsNullOrWhiteSpace(e.ModId) &&
                   e.ModName.Equals(mod.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
/// Disable a mod without deleting anything: move every journaled file that sits
/// under BepInEx/plugins or BepInEx/patchers into the manager's disabled
/// folder (beside the executable), preserving the tree. The move is reversible
/// via <see cref="Enable"/>. Files that were modified since install are left in
/// place with a warning rather than touched.
/// </summary>
public DisableResult Disable(string modName)
{
    try
    {
        using var operation = EnterOperation();
        return DisableCore(modName);
    }
catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        return new DisableResult(false, ex.Message, new List<string>());
    }
}

private DisableResult DisableCore(string modName)
{
    var warnings = new List<string>();
    var entry = _journal.Load().FirstOrDefault(e => e.ModName == modName);
    if (entry is null)
        return new DisableResult(false, $"No journal entry found for {modName}", warnings);

    var moved = 0;
    var alreadyDisabled = 0;
    var kept = 0;
    var nonManaged = 0;

    foreach (var file in entry.Files)
    {
        if (file.PreserveOnUninstall)
        {
            warnings.Add($"Pre-existing file remains enabled: {file.Path}");
            nonManaged++;
            continue;
        }

        var sections = ManagedSections(file.Path);
        if (sections is null)
        {
            // BUG-011: framework/core and game-root files are not something we
            // toggle here; counting them lets us report a proper non-success.
            warnings.Add($"Not a managed BepInEx file, skipping: {file.Path}");
            nonManaged++;
            continue;
        }

        if (!File.Exists(file.Path))
        {
            warnings.Add($"Already missing, skipping: {file.Path}");
            alreadyDisabled++;
            continue;
        }

        if (!HashMatchesCurrent(file.Path, file.Sha256))
        {
            warnings.Add($"Modified since install, keeping in place: {file.Path}");
            kept++;
            continue;
        }

        var disabledPath = DisabledPath(_disabledRoot, sections);

        Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);

        // BUG-016: a stale disabled copy (e.g. disabled -> reinstalled -> disable
        // again) would make File.Move throw "file already exists". Clear it first.
        if (File.Exists(disabledPath))
        {
            try
            {
                File.Delete(disabledPath);
            }
            catch
            {
                warnings.Add($"Could not clear stale disabled copy, leaving enabled: {disabledPath}");
                continue;
            }
        }

        File.Move(file.Path, disabledPath);
        moved++;
    }

    PruneEmptyActiveFolders();

    // BUG-013: at least one managed file was kept — the mod is still partially active.
    if (moved > 0 && kept > 0)
        return new DisableResult(false,
            $"{modName} is only partially disabled: {kept} file(s) modified since install were left active, so the mod is still partially loaded.",
            warnings);

    if (moved > 0 && nonManaged > 0)
        return new DisableResult(false,
            $"{modName} is only partially disabled: {nonManaged} framework or game-root file(s) remain active.",
            warnings);

    if (moved == 0)
    {
        if (nonManaged > 0)
            // BUG-011: framework/game-root mods aren't something we toggle here.
            return new DisableResult(false,
                $"{modName} is not a managed BepInEx/plugins or BepInEx/patchers mod; framework/game-root mods cannot be disabled here.",
                warnings);

        if (kept > 0)
            return new DisableResult(false,
                $"{modName}: nothing disabled — {kept} file(s) modified since install were left in place.",
                warnings);

        // BUG-018: idempotent no-op — it was already disabled.
        return new DisableResult(true, null, warnings, $"Already disabled: {modName}");
    }

    return new DisableResult(true, null, warnings);
}

/// <summary>
/// Reverse of <see cref="Disable"/>: move journaled files that sit in the
/// disabled folder back to their original paths. Refuses the restore if
/// something already occupies the destination.
/// </summary>
public EnableResult Enable(string modName)
{
    try
    {
        using var operation = EnterOperation();
        return EnableCore(modName);
    }
catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        return new EnableResult(false, ex.Message, new List<string>());
    }
}

private EnableResult EnableCore(string modName)
{
    var warnings = new List<string>();
    var entry = _journal.Load().FirstOrDefault(e => e.ModName == modName);
    if (entry is null)
        return new EnableResult(false, $"No journal entry found for {modName}", warnings);

    var moved = 0;
    var alreadyEnabled = 0;
    var nonManaged = 0;

    foreach (var file in entry.Files)
    {
        if (file.PreserveOnUninstall)
        {
            warnings.Add($"Pre-existing file was not managed: {file.Path}");
            nonManaged++;
            continue;
        }

        var sections = ManagedSections(file.Path);
        if (sections is null)
        {
            // BUG-011: framework/core and game-root files are not toggled here.
            warnings.Add($"Not a managed BepInEx file, skipping: {file.Path}");
            nonManaged++;
            continue;
        }

        var disabledPath = ExistingDisabledPath(sections) ?? DisabledPath(_disabledRoot, sections);

        if (!File.Exists(disabledPath))
        {
            warnings.Add($"Not in the disabled folder, skipping: {Path.GetFileName(file.Path)}");
            alreadyEnabled++;
            continue;
        }

        if (File.Exists(file.Path))
        {
            warnings.Add($"Destination already exists, leaving disabled: {file.Path}");
            continue;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
        File.Move(disabledPath, file.Path);
        moved++;
    }

    PruneEmptyDisabledFolders();

    var blocked = warnings.Count(w => w.StartsWith("Destination already exists", StringComparison.Ordinal));

    if (moved > 0 && (blocked > 0 || nonManaged > 0))
        return new EnableResult(false,
            $"{modName} is only partially enabled: {blocked + nonManaged} file(s) could not be restored.",
            warnings);

    if (moved == 0)
    {
        if (nonManaged > 0)
            // BUG-011: framework/game-root mods aren't something we toggle here.
            return new EnableResult(false,
                $"{modName} is not a managed BepInEx/plugins or BepInEx/patchers mod; framework/game-root mods cannot be enabled here.",
                warnings);

        // BUG-018: idempotent no-op — it was already enabled.
        return new EnableResult(true, null, warnings, $"Already enabled: {modName}");
    }

    return new EnableResult(true, null, warnings);
}

/// <summary>
/// The part of a journaled path that lives under a managed root (plugins or
/// patchers), e.g. ["ModName", "lib", "file.dll"], so it can be relocated to the
/// disabled folder and back. Null when the file isn't one we manage.
/// </summary>
private string[]? ManagedSections(string filePath)
{
    var relative = RelativeToGame(filePath);
    if (relative is null)
        return null;

    var sections = relative.Replace('\\', '/').Split('/');
    if (sections.Length < 3 ||
        !sections[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) ||
        !(sections[1].Equals("plugins", StringComparison.OrdinalIgnoreCase) ||
          sections[1].Equals("patchers", StringComparison.OrdinalIgnoreCase)))
        return null;

    return sections.Skip(2).ToArray();
}

private string? RelativeToGame(string filePath)
{
    var full = Path.GetFullPath(filePath);
    var game = Path.GetFullPath(_gameFolderPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    return full.StartsWith(game, StringComparison.OrdinalIgnoreCase) ? full[game.Length..] : null;
}

private bool IsMutableRuntimeFile(string filePath) =>
    IsMutableConfiguration(filePath) || IsRuntimeCache(filePath);

private bool IsMutableConfiguration(string filePath) =>
    IsBepInExSection(filePath, "config");

private bool IsRuntimeCache(string filePath) =>
    IsBepInExSection(filePath, "cache");

private bool IsBepInExSection(string filePath, string section)
{
    var relative = RelativeToGame(filePath);
    if (relative is null)
        return false;

    var parts = relative.Replace('\\', '/').Split('/');
    return parts.Length >= 3 &&
           parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
           parts[1].Equals(section, StringComparison.OrdinalIgnoreCase);
}

private bool HashMatchesCurrent(string path, string expectedSha256) =>
    ComputeSha256(path).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);

private void PruneEmptyDisabledFolders()
{
    var disabledRoot = _disabledRoot;
    if (!Directory.Exists(disabledRoot))
        return;

    try
    {
        foreach (var folder in Directory.EnumerateDirectories(disabledRoot))
        {
            if (!Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
    }
    catch
    {
        // Best effort cleanup of emptied folders.
    }
}

private void PruneEmptyActiveFolders()
{
    foreach (var root in new[] { "BepInEx/plugins", "BepInEx/patchers" })
    {
        var fullRoot = Path.Combine(_gameFolderPath, root.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(fullRoot))
            continue;

        foreach (var folder in Directory.EnumerateDirectories(fullRoot))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch
            {
                // Best effort cleanup of emptied folders.
            }
        }
    }
}

    public UninstallResult Uninstall(string modName)
    {
        try
        {
            using var operation = EnterOperation();
            return UninstallCore(modName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return new UninstallResult(false, ex.Message, new List<string>());
        }
    }

    private UninstallResult UninstallCore(string modName)
    {
    // BUG-040: a missing game folder is distinct from "no journal entry".
    if (!Directory.Exists(_gameFolderPath))
        return new UninstallResult(false, $"Game folder not found: {_gameFolderPath}", new List<string>());

    var entries = _journal.Load();
    var entry = entries.FirstOrDefault(e => e.ModName == modName);

    if (entry is null)
        return new UninstallResult(false, $"No journal entry found for {modName}", new List<string>());

    var warnings = new List<string>();
    var filesToDelete = new List<string>();

    foreach (var file in entry.Files)
    {
        if (file.PreserveOnUninstall)
        {
            warnings.Add($"Pre-existing file kept in place: {file.Path}");
            continue;
        }

        var pathToDelete = file.Path;
        if (!File.Exists(pathToDelete))
        {
            var sections = ManagedSections(file.Path);
            var disabledPath = sections is null ? null : ExistingDisabledPath(sections);
            if (disabledPath is not null && File.Exists(disabledPath))
                pathToDelete = disabledPath;
            else
            {
                warnings.Add($"File already missing, skipping: {file.Path}");
                continue;
            }
        }

        var currentHash = ComputeSha256(pathToDelete);
        if (!currentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (IsMutableConfiguration(pathToDelete))
            {
                warnings.Add($"Modified configuration kept in place: {pathToDelete}");
                continue;
            }

            if (IsRuntimeCache(pathToDelete))
            {
                filesToDelete.Add(pathToDelete);
                continue;
            }

            return new UninstallResult(false,
                $"Uninstall stopped because a managed file was modified: {pathToDelete}",
                warnings);
        }

        filesToDelete.Add(pathToDelete);
    }

    foreach (var path in filesToDelete)
        File.Delete(path);

    // Every existing file has passed the hash preflight, so deletion cannot leave
    // a deliberately modified file behind without its journal entry.
    _journal.Remove(modName);

    // BUG-005: if this was the last journaled mod belonging to a pack, clear
    // the stale pack entry so update detection stops reporting "Update available".
    if (!string.IsNullOrEmpty(entry.PackId) &&
        !entries.Any(e => !ReferenceEquals(e, entry) &&
                          string.Equals(e.PackId, entry.PackId, StringComparison.OrdinalIgnoreCase)))
    {
        try { _modpackJournal.Remove(entry.PackId!); }
        catch { /* best effort; pack journal is advisory */ }
    }
    PruneEmptyDisabledFolders();
    PruneEmptyActiveFolders();
    return new UninstallResult(true, null, warnings);
}

    /// <summary>
    /// Turn a forward-slash relative destination (as ZIP stores it) into a real
    /// absolute path on this OS, using the platform's directory separator.
    /// </summary>
    private static string PhysicalPath(string gameFolderPath, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Install destination must be relative: {relativePath}");

        var gameRoot = Path.GetFullPath(gameFolderPath);
        var destination = Path.GetFullPath(Path.Combine(
            gameRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var requiredPrefix = Path.EndsInDirectorySeparator(gameRoot)
            ? gameRoot
            : gameRoot + Path.DirectorySeparatorChar;

        if (!destination.StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Install destination escapes the game folder: {relativePath}");

        PathSafety.EnsureContainedWithoutReparsePoints(
            gameRoot, destination, "Install destination");

        return destination;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hashBytes = sha256.ComputeHash(stream);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool HashesMatch(string first, string second) =>
        ComputeSha256(first).Equals(ComputeSha256(second), StringComparison.OrdinalIgnoreCase);

    private static string? MatchingArchiveExclusion(
        string relativePath, List<string>? exclusions)
    {
        foreach (var exclusion in exclusions ?? new List<string>())
        {
            if (exclusion.EndsWith("/", StringComparison.Ordinal))
            {
                if (relativePath.StartsWith(exclusion, StringComparison.OrdinalIgnoreCase))
                    return exclusion;
            }
            else if (relativePath.Equals(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return exclusion;
            }
        }

        return null;
    }

    private string? ExistingDisabledPath(string[] sections)
    {
        var primary = DisabledPath(_disabledRoot, sections);
        if (File.Exists(primary))
            return primary;
        if (_legacyDisabledRoot is null)
            return null;

        var legacy = DisabledPath(_legacyDisabledRoot, sections);
        return File.Exists(legacy) ? legacy : null;
    }

    private static string DisabledPath(string root, IEnumerable<string> sections) =>
        Path.Combine(new[] { root }.Concat(sections).ToArray());

    private IDisposable? EnterOperation() =>
        _operationLockHeld ? null : GameOperationLock.Acquire(_gameFolderPath);
}
