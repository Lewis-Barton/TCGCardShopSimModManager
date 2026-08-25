using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Cli;

public static class ModpackUpdateCheckCommand
{
    public static async Task Run(string? packValue)
    {
        if (string.IsNullOrWhiteSpace(packValue))
        {
            Console.WriteLine("Usage: modpack check-updates <packId | manifest.json>");
            Environment.ExitCode = 2;
            return;
        }

        try
        {
            var manifestPath = ResolveManifestPath(packValue);
            var manifest = new ManifestReader().Read(manifestPath);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var auth = NexusAuth.Unified(http);
            using var api = new NexusApi(
                NexusApi.ApiBaseUrl(), NexusApi.GameDomain, NexusApi.UserAgent, http);
            var checker = new ModpackUpdateChecker(
                (modId, cancellationToken) => api.ListFilesAsync(modId, auth, cancellationToken));
            var results = await checker.CheckAsync(manifest);

            Console.WriteLine($"{manifest.Name} — pinned Nexus file check");
            PrintGroup("Required mods", results.Where(result => result.Mod.Required));
            PrintGroup("Optional mods", results.Where(result => !result.Mod.Required));

            var updates = results.Where(result => result.Status == ModpackUpdateStatus.UpdateAvailable).ToArray();
            var missing = results.Count(result => result.Status == ModpackUpdateStatus.MissingOrArchived);
            var current = results.Count(result => result.Status == ModpackUpdateStatus.Current);
            var skipped = results.Count(result => result.Status == ModpackUpdateStatus.NotChecked);
            Console.WriteLine();
            Console.WriteLine($"Summary: {updates.Length} update(s), {missing} missing or archived, {current} current, {skipped} not checked.");

            if (updates.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Suggested selectors:");
                foreach (var result in updates)
                {
                    var role = result.Mod.Id.Equals(ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase)
                        ? "bepinex"
                        : result.Mod.Required ? "required" : "optional";
                    Console.WriteLine($"{role} nexus:{result.Mod.NexusModId}:{result.SuggestedFile!.FileId} # {result.Mod.Name}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("No files were changed. Review and test replacements, then increment the pack version before publishing.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not check modpack updates: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void PrintGroup(string heading, IEnumerable<ModpackUpdateResult> values)
    {
        var results = values.ToArray();
        if (results.Length == 0)
            return;

        Console.WriteLine();
        Console.WriteLine(heading);
        foreach (var result in results)
        {
            var status = result.Status switch
            {
                ModpackUpdateStatus.Current => "CURRENT",
                ModpackUpdateStatus.UpdateAvailable => "UPDATE",
                ModpackUpdateStatus.MissingOrArchived => "MISSING",
                _ => "SKIPPED"
            };
            var version = VersionText(result.PinnedFile?.Version ?? result.Mod.Version);
            var line = $"[{status}] {result.Mod.Name} {version}".TrimEnd();
            if (result.SuggestedFile is { } suggested)
                line += $" -> {VersionText(suggested.Version)} (nexus:{result.Mod.NexusModId}:{suggested.FileId})";
            Console.WriteLine(line);
            if (result.Message is not null)
                Console.WriteLine($"  {result.Message}");
        }
    }

    private static string VersionText(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "(version unknown)" : $"v{version}";

    private static string ResolveManifestPath(string packValue)
    {
        if (File.Exists(packValue))
            return Path.GetFullPath(packValue);

        var modpacksRoot = FindModpacksRoot();
        var direct = Path.Combine(modpacksRoot, packValue, "manifest.json");
        if (File.Exists(direct))
            return direct;

        var match = Directory.EnumerateDirectories(modpacksRoot)
            .FirstOrDefault(path => Path.GetFileName(path).Equals(packValue, StringComparison.OrdinalIgnoreCase));
        if (match is not null && File.Exists(Path.Combine(match, "manifest.json")))
            return Path.Combine(match, "manifest.json");

        throw new FileNotFoundException($"No local manifest was found for '{packValue}'.");
    }

    private static string FindModpacksRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "modpacks", "index.json");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find modpacks/index.json from the current directory.");
    }
}
