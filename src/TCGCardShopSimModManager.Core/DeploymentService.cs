using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

public sealed record DeploymentReport(bool Success, List<string> Lines)
{
    public static DeploymentReport Ok(List<string> lines) => new(true, lines);

    public static DeploymentReport Failure(List<string> lines, string? error)
    {
        if (error is not null)
            lines.Add(error);
        return new(false, lines);
    }
}

/// <summary>A per-archive preview, ready for display.</summary>
public sealed record PlanPreview(
    string ModName,
    string LayoutName,
    List<string> Files,
    List<string> Skipped,
    List<string> Rejected);

/// <summary>
/// The single orchestration path both front-ends use — the CLI commands and the
/// desktop app are thin shells around this, so behaviour stays the same no
/// matter which one you use.
/// </summary>
public sealed class DeploymentService
{
    public DeploymentReport Validate(string manifestPath, string? gameFolderPath)
    {
        Diagnostic.Write($"DeploymentService.Validate({manifestPath})");
        var lines = new List<string>();

        if (!File.Exists(manifestPath))
            return DeploymentReport.Failure(lines, $"Manifest file not found: {manifestPath}");

        // BUG-026: a malformed manifest must surface a friendly message, not the
        // raw serializer exception via the top-level handler.
        ModListManifest manifest;
        try
        {
            manifest = ModpackInstaller.EnforceBepInExFirst(new ManifestReader().Read(manifestPath));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return DeploymentReport.Failure(lines, $"Manifest is not valid JSON: {ex.Message}");
        }
        var validation = new ManifestValidator().Validate(manifest);
        if (!validation.IsValid)
        {
            lines.Add("Manifest is invalid:");
            lines.AddRange(validation.Errors.Select(e => $"  - {e}"));
            return DeploymentReport.Failure(lines, null);
        }

        lines.Add($"Manifest '{manifest.Name}' is valid.");

        if (gameFolderPath is null)
            lines.Add("  (No game folder given — checking with every mod enabled.)");

        var resolution = new ModListResolver().Resolve(manifest, ResolveEnabledIds(manifest, gameFolderPath));
        if (!resolution.IsValid)
        {
            lines.Add("The enabled mod list cannot be installed:");
            lines.AddRange(resolution.Errors.Select(e => $"  - {e}"));
            return DeploymentReport.Failure(lines, null);
        }

        lines.Add($"  {resolution.OrderedMods.Count} mod(s). Valid install order:");
        lines.AddRange(resolution.OrderedMods.Select(m => $"    {Label(m)} (id: {m.Id})"));
        return DeploymentReport.Ok(lines);
    }

    public DeploymentReport Install(string manifestPath, string sourceDirectory, string gameFolderPath)
    {
        Diagnostic.Write($"DeploymentService.Install({manifestPath})");
        var lines = new List<string>();

        if (!File.Exists(manifestPath))
            return DeploymentReport.Failure(lines, $"Manifest file not found: {manifestPath}");

        // BUG-026: a malformed manifest must surface a friendly message, not the
        // raw serializer exception via the top-level handler.
        ModListManifest manifest;
        try
        {
            manifest = new ManifestReader().Read(manifestPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return DeploymentReport.Failure(lines, $"Manifest is not valid JSON: {ex.Message}");
        }
        return Install(manifest, sourceDirectory, gameFolderPath);
    }

    /// <summary>
    /// Install from an already-loaded manifest (e.g. one fetched from a hosted
    /// modpack). Same validate → plan → install pipeline as the file overload.
    /// </summary>
    public DeploymentReport Install(ModListManifest manifest, string sourceDirectory, string gameFolderPath)
    {
        Diagnostic.Write("DeploymentService.Install(manifest)");
        var lines = new List<string>();

        GameOperationLock operation;
        try
        {
            operation = GameOperationLock.Acquire(gameFolderPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return DeploymentReport.Failure(lines, ex.Message);
        }
        using (operation)
            return InstallLocked(manifest, sourceDirectory, gameFolderPath, lines);
    }

    internal DeploymentReport InstallWithLockHeld(
        ModListManifest manifest,
        string sourceDirectory,
        string gameFolderPath,
        IProgress<ModpackInstallProgress>? progress = null) =>
        InstallLocked(manifest, sourceDirectory, gameFolderPath, new List<string>(), progress);

    private static DeploymentReport InstallLocked(
        ModListManifest manifest,
        string sourceDirectory,
        string gameFolderPath,
        List<string> lines,
        IProgress<ModpackInstallProgress>? progress = null)
    {
        // BUG-020: the local path must guarantee BepInEx sorts first just like the
        // hosted-modpack path does. Enforce it here so both `install` (this
        // overload) and `validate` resolve the same BepInEx-first order.
        manifest = ModpackInstaller.EnforceBepInExFirst(manifest);

        var validation = new ManifestValidator().Validate(manifest);
        if (!validation.IsValid)
        {
            lines.Add("Manifest is invalid:");
            lines.AddRange(validation.Errors.Select(e => $"  - {e}"));
            return DeploymentReport.Failure(lines, null);
        }

        var resolution = new ModListResolver().Resolve(manifest, ResolveEnabledIds(manifest, gameFolderPath));
        if (!resolution.IsValid)
        {
            lines.Add("The enabled mod list cannot be installed:");
            lines.AddRange(resolution.Errors.Select(e => $"  - {e}"));
            return DeploymentReport.Failure(lines, null);
        }

        lines.Add("Install order:");
        lines.AddRange(resolution.OrderedMods.Select(m => $"  {Label(m)}"));

        var installer = new ModInstaller(gameFolderPath, disabledRoot: null, operationLockHeld: true);
        var toInstall = resolution.OrderedMods.Where(m => !installer.IsCurrent(m)).ToList();

        // Pre-flight: plan every archive so two mods claiming the same file are
        // caught before a single byte is copied.
        var planRoot = Path.Combine(Path.GetTempPath(), "cardshopmodmanager-preflight", Guid.NewGuid().ToString("N"));
        var plans = new List<InstallPlan>();
        try
        {
            Directory.CreateDirectory(planRoot);
            for (var i = 0; i < toInstall.Count; i++)
            {
                try
                {
                    progress?.Report(new ModpackInstallProgress(
                        ModpackInstallStage.Planning,
                        toInstall[i].Name,
                        i + 1,
                        toInstall.Count));
                    plans.Add(installer.CreatePlan(toInstall[i], sourceDirectory, Path.Combine(planRoot, $"mod-{i + 1}")));
                }
                catch (Exception ex)
                {
                    return DeploymentReport.Failure(lines, $"Could not plan {toInstall[i].Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            TemporaryDirectory.DeleteBestEffort(planRoot);
        }

        // BUG-019: refuse pre-flight when a pending mod collides with a file
        // already owned by an installed mod, not only when two pending mods clash.
        var replacingNames = toInstall
            .Select(installer.JournaledName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = DestinationConflictFinder.Find(
            plans, BuildInstalledPlans(gameFolderPath, replacingNames));
        if (conflicts.Count > 0)
        {
            lines.Add("File conflicts detected — refusing to install:");
            lines.AddRange(conflicts.Take(20).Select(c =>
                $"  {c.Destination} is claimed by '{c.ModA}' and '{c.ModB}'"));
            if (conflicts.Count > 20)
                lines.Add($"  ... and {conflicts.Count - 20} more conflict(s).");
            return DeploymentReport.Failure(lines, null);
        }

        foreach (var mod in toInstall)
        {
            if (installer.UpdateBlockReason(mod) is { } reason)
                return DeploymentReport.Failure(lines, reason);
        }

        DeploymentSnapshot snapshot;
        try
        {
            snapshot = DeploymentSnapshot.Capture(gameFolderPath, toInstall, plans);
        }
        catch (Exception ex)
        {
            return DeploymentReport.Failure(lines, $"Could not prepare deployment rollback: {ex.Message}");
        }

        using (snapshot)
        {
            for (var i = 0; i < toInstall.Count; i++)
            {
                var mod = toInstall[i];
                progress?.Report(new ModpackInstallProgress(
                    ModpackInstallStage.Installing,
                    mod.Name,
                    i + 1,
                    toInstall.Count));
                var updating = installer.HasJournalEntry(mod);
                var result = installer.Install(mod, sourceDirectory);
                lines.Add(result.Success
                    ? $"{(updating ? "Updated" : "Installed")} {Label(mod)}: {result.InstalledPaths!.Count} file(s)."
                    : $"Failed to install {Label(mod)}: {result.Error}");

                if (result.RejectedEntries is { Count: > 0 })
                {
                    lines.Add($"  warning: {result.RejectedEntries.Count} file(s) rejected during extraction:");
                    lines.AddRange(result.RejectedEntries.Select(r => $"    - {r}"));
                }

                if (result.SkippedEntries is { Count: > 0 })
                    lines.AddRange(result.SkippedEntries.Select(s => $"  note: skipped {s}"));

                if (result.Success)
                    continue;

                Diagnostic.Write($"install failed for {mod.Id}: {result.Error}", "install");
                lines.Add($"Rolling back {i} earlier mod change(s).");
                var rollbackErrors = snapshot.Rollback();
                if (rollbackErrors.Count == 0)
                    lines.Add("Deployment rollback completed.");
                else
                {
                    lines.Add("Deployment rollback was incomplete:");
                    lines.AddRange(rollbackErrors.Select(error => $"  - {error}"));
                }
                return DeploymentReport.Failure(lines, null);
            }

            snapshot.Commit();
        }

        return DeploymentReport.Ok(lines);
    }

    public IReadOnlyList<PlanPreview> Preview(string manifestPath, string sourceDirectory)
    {
        var manifest = new ManifestReader().Read(manifestPath);
        var installer = new ModInstaller(Path.GetTempPath()); // planning never touches the journal

        var previews = new List<PlanPreview>();
        var planRoot = Path.Combine(Path.GetTempPath(), "cardshopmodmanager-preview", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(planRoot);
            for (var i = 0; i < manifest.Mods.Count; i++)
            {
                var mod = manifest.Mods[i];
                var label = Label(mod);

                try
                {
                    var plan = installer.CreatePlan(mod, sourceDirectory, Path.Combine(planRoot, $"mod-{i + 1}"));
                    previews.Add(new PlanPreview(
                        label,
                        plan.LayoutName,
                        plan.Files.Select(f => $"  {f.SourceRelativePath}  ->  {f.DestinationRelativePath}").ToList(),
                        plan.SkippedEntries.ToList(),
                        plan.RejectedEntries.ToList()));
                }
                catch (Exception ex)
                {
                    previews.Add(new PlanPreview(label, "could not plan", new List<string> { ex.Message },
                        new List<string>(), new List<string>()));
                }
            }
        }
        finally
        {
            TemporaryDirectory.DeleteBestEffort(planRoot);
        }

        return previews;
    }

    private static ISet<string> ResolveEnabledIds(ModListManifest manifest, string? gameFolderPath)
    {
        var allIds = new HashSet<string>(manifest.Mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        if (gameFolderPath is null)
            return allIds;

        return new ProfilesStore(gameFolderPath).EnabledIdsOrAll() ?? allIds;
    }

    private static string Label(ModEntry mod) =>
        mod.Version is null ? mod.Name : $"{mod.Name} {mod.Version}";

    /// <summary>
    /// Rebuilds pseudo install plans for mods already on disk, so the pre-flight
    /// conflict check can also refuse a pending mod that would overwrite a file
    /// owned by an installed one (BUG-019). The journal records each installed
    /// file's full path; we turn that back into a relative destination so it
    /// compares like a pending plan's destinations.
    /// </summary>
    private static IReadOnlyList<InstallPlan> BuildInstalledPlans(
        string gameFolderPath,
        ISet<string>? excludedModNames = null)
    {
        var plans = new List<InstallPlan>();
        foreach (var entry in new JournalStore(gameFolderPath).Load())
        {
            if (excludedModNames?.Contains(entry.ModName) == true)
                continue;
            if (entry.Files.Count == 0)
                continue;

            var files = entry.Files.Select(f =>
            {
                var relative = Path.GetRelativePath(gameFolderPath, f.Path).Replace('\\', '/');
                return new ArchiveContentEntry(f.Path, relative, relative);
            }).ToList();

            plans.Add(new InstallPlan(
                new ModEntry(entry.ModName, entry.ModName, null, "installed",
                    new string('0', 64), "BepInExPlugin", new List<string>(), new List<string>()),
                "installed",
                files,
                new List<string>(),
                new List<string>()));
        }

        return plans;
    }

    private sealed class DeploymentSnapshot : IDisposable
    {
        private readonly DurableRecoveryTransaction _transaction;

        private DeploymentSnapshot(DurableRecoveryTransaction transaction) =>
            _transaction = transaction;

        public static DeploymentSnapshot Capture(
            string gameFolderPath,
            IReadOnlyList<ModEntry> mods,
            IReadOnlyList<InstallPlan> plans)
        {
            var journalEntries = new JournalStore(gameFolderPath).Load();
            var paths = new List<string>();
            for (var i = 0; i < mods.Count; i++)
            {
                var previous = FindJournalEntry(journalEntries, mods[i]);
                paths.AddRange(previous?.Files.Select(file => Path.GetFullPath(file.Path))
                               ?? Enumerable.Empty<string>());
                paths.AddRange(plans[i].Files.Select(file =>
                    DestinationPath(gameFolderPath, file.DestinationRelativePath)));
            }

            return new DeploymentSnapshot(
                DurableRecoveryTransaction.CaptureDeployment(gameFolderPath, paths));
        }

        public List<string> Rollback() => _transaction.Rollback();

        public void Commit() => _transaction.Commit();

        public void Dispose() => _transaction.Dispose();

        private static InstallJournalEntry? FindJournalEntry(
            IEnumerable<InstallJournalEntry> entries,
            ModEntry mod) =>
            entries.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(entry.ModId) &&
                entry.ModId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(entry =>
                string.IsNullOrWhiteSpace(entry.ModId) &&
                entry.ModName.Equals(mod.Name, StringComparison.OrdinalIgnoreCase));

        private static string DestinationPath(string gameFolderPath, string relativePath)
        {
            var destination = Path.GetFullPath(Path.Combine(
                gameFolderPath,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            PathSafety.EnsureContainedWithoutReparsePoints(
                gameFolderPath, destination, "Deployment destination");
            return destination;
        }
    }
}
