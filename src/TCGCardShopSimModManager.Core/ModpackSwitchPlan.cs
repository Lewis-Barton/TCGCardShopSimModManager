namespace TCGCardShopSimModManager.Core;

public sealed record ModpackSwitchPlan(
    List<string> Retained,
    List<string> Updated,
    List<string> Removed,
    List<string> Added);

public static class ModpackSwitchPlanner
{
    public static ModpackSwitchPlan Create(
        IEnumerable<InstallJournalEntry> currentEntries,
        IEnumerable<ModEntry> targetMods)
    {
        var current = currentEntries.ToList();
        var target = targetMods.ToList();
        var currentById = current
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ModId))
            .GroupBy(entry => entry.ModId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var targetById = target
            .ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);

        var retained = new List<string>();
        var updated = new List<string>();
        foreach (var mod in target)
        {
            if (!currentById.TryGetValue(mod.Id, out var installed))
                continue;

            if (!string.IsNullOrWhiteSpace(installed.ArchiveSha256) &&
                installed.ArchiveSha256.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase))
                retained.Add(mod.Name);
            else
                updated.Add(mod.Name);
        }

        var removed = current
            .Where(entry => string.IsNullOrWhiteSpace(entry.ModId) || !targetById.ContainsKey(entry.ModId))
            .Select(entry => entry.ModName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var added = target
            .Where(mod => !currentById.ContainsKey(mod.Id))
            .Select(mod => mod.Name)
            .ToList();

        return new ModpackSwitchPlan(retained, updated, removed, added);
    }
}
