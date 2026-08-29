namespace TCGCardShopSimModManager.Core;

public sealed record ModpackSelectionResult(
    bool IsValid,
    List<string> Errors,
    ModListManifest? Manifest);

/// <summary>
/// Produces the installable subset of a hosted pack. Required entries are
/// always included; selected optional entries pull in their dependency chain.
/// The normal resolver remains the authority for missing dependencies,
/// conflicts, case mismatches and cycles.
/// </summary>
public static class ModpackSelection
{
    public static bool OptionalSelectionMatches(
        ModListManifest manifest,
        IEnumerable<string>? installedOptionalIds,
        IEnumerable<string> selectedOptionalIds)
    {
        var installed = (installedOptionalIds ?? manifest.Mods
                .Where(mod => !mod.Required)
                .Select(mod => mod.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return installed.SetEquals(selectedOptionalIds);
    }

    public static ModpackSelectionResult Resolve(
        ModListManifest manifest,
        IEnumerable<string> selectedOptionalIds)
    {
        var requested = new HashSet<string>(selectedOptionalIds, StringComparer.OrdinalIgnoreCase);
        var allIds = manifest.Mods.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = requested
            .Where(id => !allIds.Contains(id))
            .Select(id => $"Selected mod '{id}' is not in the modpack.")
            .ToList();

        var enabled = manifest.Mods
            .Where(mod => mod.Required || requested.Contains(mod.Id))
            .Select(mod => mod.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var byId = new Dictionary<string, ModEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in manifest.Mods)
            byId.TryAdd(mod.Id, mod);
        var pending = new Queue<string>(enabled);
        while (pending.TryDequeue(out var id))
        {
            if (!byId.TryGetValue(id, out var mod))
                continue;

            foreach (var dependencyId in mod.Dependencies)
            {
                if (byId.ContainsKey(dependencyId) && enabled.Add(dependencyId))
                    pending.Enqueue(dependencyId);
            }
        }

        var resolution = new ModListResolver().Resolve(manifest, enabled);
        errors.AddRange(resolution.Errors);
        if (errors.Count > 0)
            return new ModpackSelectionResult(false, errors, null);

        return new ModpackSelectionResult(
            true,
            new List<string>(),
            manifest with { Mods = resolution.OrderedMods });
    }
}
