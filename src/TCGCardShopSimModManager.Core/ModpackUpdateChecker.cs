namespace TCGCardShopSimModManager.Core;

public enum ModpackUpdateStatus
{
    Current,
    UpdateAvailable,
    MissingOrArchived,
    NotChecked
}

public sealed record ModpackUpdateResult(
    ModEntry Mod,
    ModpackUpdateStatus Status,
    NexusFileInfo? PinnedFile = null,
    NexusFileInfo? SuggestedFile = null,
    string? Message = null);

/// <summary>Checks pinned Nexus files without changing a manifest or downloading archives.</summary>
public sealed class ModpackUpdateChecker
{
    private readonly Func<long, CancellationToken, Task<IReadOnlyList<NexusFileInfo>>> _listFiles;

    public ModpackUpdateChecker(
        Func<long, CancellationToken, Task<IReadOnlyList<NexusFileInfo>>> listFiles)
    {
        _listFiles = listFiles ?? throw new ArgumentNullException(nameof(listFiles));
    }

    public async Task<IReadOnlyList<ModpackUpdateResult>> CheckAsync(
        ModListManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var filesByMod = new Dictionary<long, IReadOnlyList<NexusFileInfo>>();
        var results = new List<ModpackUpdateResult>(manifest.Mods.Count);

        foreach (var mod in manifest.Mods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mod.NexusModId is not { } nexusModId || mod.NexusFileId is not { } nexusFileId)
            {
                results.Add(new ModpackUpdateResult(
                    mod,
                    ModpackUpdateStatus.NotChecked,
                    Message: "This entry is not pinned to a Nexus file."));
                continue;
            }

            if (!filesByMod.TryGetValue(nexusModId, out var files))
            {
                files = await _listFiles(nexusModId, cancellationToken);
                filesByMod.Add(nexusModId, files);
            }

            var pinned = files.FirstOrDefault(file => file.FileId == nexusFileId);
            if (pinned is null || IsArchived(pinned))
            {
                results.Add(new ModpackUpdateResult(
                    mod,
                    ModpackUpdateStatus.MissingOrArchived,
                    pinned,
                    Message: pinned is null
                        ? "The pinned file is no longer in the Nexus file list."
                        : "Nexus marks the pinned file as archived."));
                continue;
            }

            var label = DisplayLabel(pinned);
            var suggested = files
                .Where(file => file.FileId > pinned.FileId)
                .Where(file => DisplayLabel(file).Equals(label, StringComparison.OrdinalIgnoreCase))
                .Where(file => !IsRetired(file))
                .OrderByDescending(file => file.FileId)
                .FirstOrDefault();

            results.Add(suggested is null
                ? new ModpackUpdateResult(mod, ModpackUpdateStatus.Current, pinned)
                : new ModpackUpdateResult(mod, ModpackUpdateStatus.UpdateAvailable, pinned, suggested));
        }

        return results;
    }

    private static string DisplayLabel(NexusFileInfo file) =>
        string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName.Trim();

    private static bool IsArchived(NexusFileInfo file) =>
        file.Category?.Equals("ARCHIVED", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsRetired(NexusFileInfo file) =>
        IsArchived(file)
        || file.Category?.Equals("OLD_VERSION", StringComparison.OrdinalIgnoreCase) == true;
}
