using System.Linq;
using System.Net.Http;

namespace TCGCardShopSimModManager.Core;

/// <summary>
/// Drives a hosted modpack end to end: download every archive (via the per-mod
/// source dispatcher) into a cache folder, then run the standard install
/// pipeline against that folder. The install half is exactly
/// <see cref="DeploymentService.Install"/> — validate, plan, refuse conflicts,
/// then copy.
/// </summary>
public sealed class ModpackInstaller
{
    private readonly string _gameFolderPath;
    private readonly HttpClient? _http;
    private readonly ModpackSaveProfileManager _saveProfiles;

    public static string DefaultDownloadCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TCGCardShopSimModManager",
        "download-cache");

    public ModpackInstaller(
        string gameFolderPath,
        HttpClient? http = null,
        ModpackSaveProfileManager? saveProfiles = null)
    {
        _gameFolderPath = gameFolderPath;
        _http = http;
        _saveProfiles = saveProfiles ?? new ModpackSaveProfileManager();
    }

    public async Task<DeploymentReport> InstallAsync(
        ModListManifest manifest,
        IModSource? fallbackSource = null,
        string? cacheDirectory = null,
        ModpackSummary? pack = null,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? selectedOptionalIds = null,
        IProgress<ModpackInstallProgress>? progress = null,
        string? verifiedCacheDirectory = null,
        bool switchInstalledPack = false,
        bool swapSaveProfile = false)
    {
        manifest = EnforceBepInExFirst(manifest);
        var validation = new ManifestValidator().Validate(manifest);
        if (!validation.IsValid)
        {
            var lines = new List<string> { "Manifest is invalid:" };
            lines.AddRange(validation.Errors.Select(error => $"  - {error}"));
            return DeploymentReport.Failure(lines, null);
        }

        List<string> installedOptionalIds;
        if (selectedOptionalIds is not null)
        {
            var selection = ModpackSelection.Resolve(manifest, selectedOptionalIds);
            if (!selection.IsValid)
                return DeploymentReport.Failure(new List<string>(), string.Join(Environment.NewLine, selection.Errors));
            manifest = selection.Manifest!;
            installedOptionalIds = manifest.Mods
                .Where(mod => !mod.Required)
                .Select(mod => mod.Id)
                .ToList();
        }
        else
            installedOptionalIds = manifest.Mods
                .Where(mod => !mod.Required)
                .Select(mod => mod.Id)
                .ToList();

        if (pack is not null)
        {
            var otherPacks = InstalledPacksExcept(pack.Id);
            if (otherPacks.Count > 0 && !switchInstalledPack)
                return DifferentPackInstalled(otherPacks);

            manifest = manifest with
            {
                Mods = manifest.Mods
                    .Select(mod => mod with { PackId = pack.Id })
                    .ToList()
            };
        }

        var ownsCacheDirectory = cacheDirectory is null;
        cacheDirectory ??= Path.Combine(
            Path.GetTempPath(),
            "cardshopmodmanager-modpack",
            Guid.NewGuid().ToString("N"));
        verifiedCacheDirectory ??= ownsCacheDirectory
            ? DefaultDownloadCacheDirectory
            : cacheDirectory;

        try
        {

            // Pre-flight: if the pack declares a total download size, refuse early
            // (before touching the network) when the download temp location or the
            // game folder lacks room. The per-file gate in ModDownloader is a
            // backstop for any mod whose real size exceeds the declared total.
            if (manifest.TotalSize is { } total && total > 0)
            {
                var margin = 25L * 1024 * 1024; // 25 MiB headroom for extraction overhead
                if (!HasFreeSpace(cacheDirectory, total + margin, out var downloadMsg))
                    return DeploymentReport.Failure(new List<string>(), downloadMsg);
                if (!HasFreeSpace(_gameFolderPath, total + margin, out var installMsg))
                    return DeploymentReport.Failure(new List<string>(), installMsg);
            }

            // The fallback only matters for mods with neither a DownloadUrl nor a Nexus id; point it at the cache so an already-downloaded file is reused.
            var fallback = fallbackSource ?? new LocalFileSource(cacheDirectory);

            using var source = new ModpackModSource(manifest.Game, fallback, http: _http);
            var downloader = new ModDownloader(
                source, new DownloadOptions { CacheDirectory = verifiedCacheDirectory });

            for (var index = 0; index < manifest.Mods.Count; index++)
            {
                var entry = manifest.Mods[index];
                var mod = new ModReference(
                    entry.Id, entry.Archive, entry.Sha256, entry.Version,
                    entry.NexusModId, entry.NexusFileId, entry.DownloadUrl);

                progress?.Report(new ModpackInstallProgress(
                    ModpackInstallStage.Downloading, entry.Name, index + 1, manifest.Mods.Count));
                var result = await downloader.DownloadAsync(
                    mod,
                    cacheDirectory,
                    download => progress?.Report(new ModpackInstallProgress(
                        ModpackInstallStage.Downloading,
                        entry.Name,
                        index + 1,
                        manifest.Mods.Count,
                        download.DownloadedBytes,
                        download.TotalBytes)),
                    cancellationToken);
                if (!result.Success)
                    return DeploymentReport.Failure(
                        new List<string>(), $"Failed to download {entry.Name}: {result.Error}");
                var completedBytes = result.DestinationPath is { } path && File.Exists(path)
                    ? new FileInfo(path).Length
                    : 0;
                progress?.Report(new ModpackInstallProgress(
                    ModpackInstallStage.Downloading,
                    entry.Name,
                    index + 1,
                    manifest.Mods.Count,
                    completedBytes,
                    completedBytes > 0 ? completedBytes : null,
                    result.FromCache));
            }

            progress?.Report(new ModpackInstallProgress(
                ModpackInstallStage.Preparing, null, 0, manifest.Mods.Count));

            // A verified archive can outlive its disposable workspace copy. If
            // that copy is missing before planning, restore it from the persistent
            // cache instead of failing the complete pack with "Source file not found".
            foreach (var entry in manifest.Mods)
            {
                var workspacePath = Path.Combine(cacheDirectory, entry.Archive);
                if (File.Exists(workspacePath))
                    continue;

                var mod = new ModReference(
                    entry.Id, entry.Archive, entry.Sha256, entry.Version,
                    entry.NexusModId, entry.NexusFileId, entry.DownloadUrl);
                var restored = await downloader.DownloadAsync(
                    mod, cacheDirectory, cancellationToken: cancellationToken);
                if (!restored.Success)
                    return DeploymentReport.Failure(
                        new List<string>(),
                        $"Could not restore {entry.Name} before installation: {restored.Error}");
            }

            GameOperationLock operation;
            try
            {
                operation = GameOperationLock.Acquire(_gameFolderPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return DeploymentReport.Failure(new List<string>(), ex.Message);
            }

            using (operation)
            {
                PackInstallSnapshot? snapshot = null;
                DurableRecoveryTransaction? switchTransaction = null;
                ModpackSaveProfileTransaction? saveTransaction = null;
                try
                {
                    var otherPacks = pack is null ? new List<InstalledModpack>() : InstalledPacksExcept(pack.Id);
                    if (otherPacks.Count > 0 && !switchInstalledPack)
                        return DifferentPackInstalled(otherPacks);

                    if (otherPacks.Count > 0)
                    {
                        if (swapSaveProfile && otherPacks.Count != 1)
                            return DeploymentReport.Failure(
                                new List<string>(),
                                "Save swapping requires exactly one installed modpack.");
                        switchTransaction = DurableRecoveryTransaction.CapturePackSwitch(_gameFolderPath);
                        if (swapSaveProfile)
                            saveTransaction = _saveProfiles.BeginSwap(otherPacks[0].PackId, pack!.Id);
                        var targetIds = manifest.Mods.Select(mod => mod.Id)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var oldEntries = new JournalStore(_gameFolderPath).Load()
                            .Where(entry => otherPacks.Any(old => old.PackId.Equals(
                                entry.PackId, StringComparison.OrdinalIgnoreCase)))
                            .Where(entry => string.IsNullOrWhiteSpace(entry.ModId) || !targetIds.Contains(entry.ModId))
                            .Reverse()
                            .ToList();
                        var oldInstaller = new ModInstaller(
                            _gameFolderPath, disabledRoot: null, operationLockHeld: true);
                        foreach (var entry in oldEntries)
                        {
                            var uninstall = oldInstaller.Uninstall(entry.ModName);
                            if (!uninstall.Success)
                            {
                                var lines = new List<string>
                                    { $"Could not remove {entry.ModName} while switching modpacks: {uninstall.Error}" };
                                AddRollbackResult(lines, switchTransaction.Rollback());
                                AddSaveRollbackResult(lines, saveTransaction?.Rollback());
                                return DeploymentReport.Failure(lines, null);
                            }
                        }
                    }
                    else if (pack is not null)
                        snapshot = PackInstallSnapshot.Capture(_gameFolderPath, pack.Id);

                    var report = new DeploymentService().InstallWithLockHeld(
                        manifest, cacheDirectory, _gameFolderPath, progress, cancellationToken);
                    if (!report.Success)
                    {
                        if (switchTransaction is not null)
                        {
                            AddRollbackResult(report.Lines, switchTransaction.Rollback());
                            AddSaveRollbackResult(report.Lines, saveTransaction?.Rollback());
                        }
                        else if (snapshot is not null)
                            AddRollbackResult(report.Lines, snapshot.Rollback());
                        return report;
                    }

                    if (pack is not null)
                    {
                        var selectedIds = manifest.Mods
                            .Select(mod => mod.Id)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var selectedNames = manifest.Mods
                            .Select(mod => mod.Name)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var removedEntries = new JournalStore(_gameFolderPath).Load()
                            .Where(entry => entry.PackId?.Equals(
                                pack.Id, StringComparison.OrdinalIgnoreCase) == true)
                            .Where(entry => entry.ModId is { Length: > 0 } modId
                                ? !selectedIds.Contains(modId)
                                : !selectedNames.Contains(entry.ModName))
                            .ToList();

                        var installer = new ModInstaller(
                            _gameFolderPath, disabledRoot: null, operationLockHeld: true);
                        foreach (var entry in removedEntries)
                        {
                            var uninstall = installer.Uninstall(entry.ModName);
                            if (uninstall.Success)
                            {
                                report.Lines.Add($"Removed deselected or retired mod {entry.ModName}.");
                                continue;
                            }

                            var rollbackErrors = snapshot!.Rollback();
                            report.Lines.Add($"Could not remove {entry.ModName}: {uninstall.Error}");
                            AddRollbackResult(report.Lines, rollbackErrors);
                            return DeploymentReport.Failure(report.Lines, null);
                        }

                        if (otherPacks.Count > 0)
                        {
                            var journal = new JournalStore(_gameFolderPath);
                            var entries = journal.Load();
                            var targetIds = manifest.Mods.Select(mod => mod.Id)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                            entries = entries.Select(entry =>
                                entry.ModId is { Length: > 0 } id && targetIds.Contains(id)
                                    ? entry with { PackId = pack.Id }
                                    : entry).ToList();
                            journal.Save(entries);

                            var packJournal = new ModpackJournalStore(_gameFolderPath);
                            foreach (var oldPack in otherPacks)
                                packJournal.Remove(oldPack.PackId);
                            packJournal.Record(pack.Id, pack.Version, pack.Name, installedOptionalIds);
                            report.Lines.Add($"Switched from {string.Join(", ", otherPacks.Select(old => old.Name))} to {pack.Name}.");
                        }
                        else
                            new ModpackJournalStore(_gameFolderPath).Record(
                                pack.Id, pack.Version, pack.Name, installedOptionalIds);
                    }

                    snapshot?.Commit();
                    switchTransaction?.Commit();
                    saveTransaction?.Commit();
                    return report;
                }
                catch (Exception ex)
                {
                    var lines = new List<string>();
                    if (switchTransaction is not null)
                    {
                        lines.Add($"The modpack switch could not be completed: {ex.Message}");
                        AddRollbackResult(lines, switchTransaction.Rollback());
                        AddSaveRollbackResult(lines, saveTransaction?.Rollback());
                    }
                    else if (snapshot is not null)
                    {
                        lines.Add($"The pack installation could not be completed: {ex.Message}");
                        AddRollbackResult(lines, snapshot.Rollback());
                    }
                    else
                        lines.Add($"The pack installation could not be prepared: {ex.Message}");
                    return DeploymentReport.Failure(lines, null);
                }
                finally
                {
                    snapshot?.Dispose();
                    switchTransaction?.Dispose();
                    saveTransaction?.Dispose();
                }
            }
        }
        finally
        {
            if (ownsCacheDirectory)
                TemporaryDirectory.DeleteBestEffort(cacheDirectory);
        }
    }

    private List<InstalledModpack> InstalledPacksExcept(string packId) =>
        Directory.Exists(_gameFolderPath)
            ? new ModpackJournalStore(_gameFolderPath).Load()
                .Where(installed => !installed.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase))
                .ToList()
            : new List<InstalledModpack>();

    private static DeploymentReport DifferentPackInstalled(IReadOnlyCollection<InstalledModpack> packs) =>
        DeploymentReport.Failure(new List<string>(),
            $"{string.Join(", ", packs.Select(pack => pack.Name))} is already installed. " +
            "Uninstall it first or use the modpack switch action.");

    public DeploymentReport Uninstall(string packId)
    {
        if (string.IsNullOrWhiteSpace(packId))
            return DeploymentReport.Failure(new List<string>(), "Modpack id is required.");
        if (!Directory.Exists(_gameFolderPath))
            return DeploymentReport.Failure(
                new List<string>(), $"Game folder not found: {_gameFolderPath}");

        GameOperationLock operation;
        try
        {
            operation = GameOperationLock.Acquire(_gameFolderPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return DeploymentReport.Failure(new List<string>(), ex.Message);
        }

        using (operation)
        {
            var entries = new JournalStore(_gameFolderPath).Load()
                .Where(entry => entry.PackId?.Equals(
                    packId, StringComparison.OrdinalIgnoreCase) == true)
                .Reverse()
                .ToList();
            if (entries.Count == 0)
                return DeploymentReport.Failure(
                    new List<string>(), $"No installed mods were found for modpack '{packId}'.");

            using var snapshot = PackInstallSnapshot.Capture(_gameFolderPath, packId);
            var lines = new List<string>();
            try
            {
                var installer = new ModInstaller(
                    _gameFolderPath, disabledRoot: null, operationLockHeld: true);
                foreach (var entry in entries)
                {
                    var result = installer.Uninstall(entry.ModName);
                    if (!result.Success)
                    {
                        var failureLines = new List<string>
                        {
                            $"Could not uninstall {entry.ModName}: {result.Error}"
                        };
                        AddRollbackResult(failureLines, snapshot.Rollback());
                        return DeploymentReport.Failure(failureLines, null);
                    }

                    lines.Add($"Uninstalled {entry.ModName}.");
                    lines.AddRange(result.Warnings.Select(warning => $"  warning: {warning}"));
                }

                new ModpackJournalStore(_gameFolderPath).Remove(packId);
                snapshot.Commit();
                return DeploymentReport.Ok(lines);
            }
            catch (Exception ex)
            {
                lines.Add($"Modpack uninstall failed: {ex.Message}");
                AddRollbackResult(lines, snapshot.Rollback());
                return DeploymentReport.Failure(lines, null);
            }
        }
    }

    /// <summary>
    /// Guarantees the BepInEx framework is installed before any other mod, so
    /// plugins always have a loader to drop into. When a BepInEx entry is present
    /// (id <see cref="ModListConventions.BepInExModId"/>), every other mod that
    /// doesn't already depend on it gets that dependency added. The resolver then
    /// orders BepInEx first via Kahn's algorithm — pack authors can't
    /// accidentally forget it. Packs without a BepInEx entry are returned
    /// unchanged.
    /// </summary>
    public static ModListManifest EnforceBepInExFirst(ModListManifest manifest)
    {
        if (manifest.Mods is null)
            return manifest;

        manifest = manifest with
        {
            Mods = manifest.Mods.Select(mod => mod with
            {
                Dependencies = mod.Dependencies ?? new List<string>(),
                Conflicts = mod.Conflicts ?? new List<string>()
            }).ToList()
        };

        var hasBepInEx = manifest.Mods.Any(m =>
            string.Equals(m.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase));
        if (!hasBepInEx)
            return manifest;

        var mods = manifest.Mods.Select(m =>
        {
            if (string.Equals(m.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase))
                return m;
            if ((m.Dependencies ?? new List<string>()).Any(d =>
                    d.Equals(ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase)))
                return m;
            return m with
            {
                Dependencies = new List<string>(m.Dependencies ?? new List<string>())
                    { ModListConventions.BepInExModId }
            };
        }).ToList();

        return manifest with { Mods = mods };
    }

    private static void AddRollbackResult(List<string> lines, IReadOnlyCollection<string> errors)
    {
        if (errors.Count == 0)
            lines.Add("Pack installation rollback completed.");
        else
        {
            lines.Add("Pack installation rollback was incomplete:");
            lines.AddRange(errors.Select(error => $"  - {error}"));
        }
    }

    private static void AddSaveRollbackResult(
        List<string> lines, IReadOnlyCollection<string>? errors)
    {
        if (errors is null)
            return;
        if (errors.Count == 0)
            lines.Add("Save-profile rollback completed.");
        else
        {
            lines.Add("Save-profile rollback was incomplete:");
            lines.AddRange(errors.Select(error => $"  - {error}"));
        }
    }

    private sealed class PackInstallSnapshot : IDisposable
    {
        private readonly DurableRecoveryTransaction _durable;

        private PackInstallSnapshot(DurableRecoveryTransaction durable) => _durable = durable;

        public static PackInstallSnapshot Capture(string gameFolderPath, string packId) =>
            new(DurableRecoveryTransaction.CapturePack(gameFolderPath, packId));

        public void Commit() => _durable.Commit();

        public List<string> Rollback() => _durable.Rollback();

        public void Dispose() => _durable.Dispose();
    }

    private static bool HasFreeSpace(string path, long neededBytes, out string message)
    {
        message = string.Empty;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < neededBytes)
            {
                message = $"Not enough free disk space on '{root}': need {neededBytes} bytes, only {free} free.";
                return false;
            }
            return true;
        }
        catch
        {
            // Can't read free space (network drive, unusual root) — don't block
            // on a false alarm; let a real write failure surface later.
            return true;
        }
    }

}
