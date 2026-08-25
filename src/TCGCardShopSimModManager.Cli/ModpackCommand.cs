using System.Net.Http;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Cli;

/// <summary>
/// Install a modpack hosted on GitHub: fetch the index, download the pack's
/// archives, then run the standard install pipeline.
///
///   modpack list                       show the available packs
///   modpack install &lt;packId&gt; [game] [optionalIds|all]
/// </summary>
public static class ModpackCommand
{
    public static async Task Run(string? sub, string? arg1, string? arg2, string? arg3 = null)
    {
        if (sub is "import")
        {
            await NexusModpackImportCommand.Run(arg1, arg2, arg3);
            return;
        }

        if (sub is "files")
        {
            await NexusFileListCommand.Run(arg1);
            return;
        }

        if (sub is "check-updates")
        {
            await ModpackUpdateCheckCommand.Run(arg1);
            return;
        }

        // `validate` is a local authoring check against modpacks/ on disk — it
        // never touches GitHub, so handle it before the live-index path.
        if (sub is "validate")
        {
            var root = arg2 ?? "modpacks";
            var validator = new ModpackSubmissionValidator(root);

            if (arg1 is null)
            {
                var all = validator.ValidateAll();
                var ok = true;
                foreach (var (id, result) in all)
                {
                    PrintSubmission(id, result);
                    ok &= result.IsValid;
                }
                Console.WriteLine(ok ? "All packs valid." : "Some packs failed validation.");
                // BUG-031: a failed validation must not exit 0.
                Environment.ExitCode = ok ? 0 : 1;
                return;
            }

            var submission = validator.ValidatePack(arg1);
            PrintSubmission(arg1, submission);
            Environment.ExitCode = submission.IsValid ? 0 : 1;
            return;
        }

        // BUG-035: validate the install id up front, before any network fetch, so
        // a missing id prints a usage hint instead of "Unexpected error" after a
        // wasted round-trip to GitHub.
        if (sub is "install" && arg1 is null)
        {
            Console.WriteLine("Usage: modpack install <id> [game] [optionalIds|all]");
            Environment.ExitCode = 2;
            return;
        }

        if (sub is not (null or "list" or "install"))
        {
            Console.WriteLine("Usage: modpack <list | install <id> [game] [optionalIds|all] | validate [id] [root] | files <Nexus URL|modId> | check-updates <packId|manifest.json> | import <links.txt> <packFolder> [packName]>");
            Environment.ExitCode = 2;
            return;
        }

        using var reader = new ModpackIndexReader();
        var index = await reader.FetchIndexAsync();

        if (sub is not "install")
        {
            if (index.Packs.Count == 0)
            {
                Console.WriteLine("No modpacks are published yet.");
                return;
            }

            Console.WriteLine("Available modpacks:");
            foreach (var p in index.Packs)
                Console.WriteLine($"  {p.Id,-22} {p.Name} — {p.ShortDescription}");
            return;
        }

        var packId = arg1!;
        var summary = index.Packs.FirstOrDefault(p =>
            p.Id.Equals(packId, StringComparison.OrdinalIgnoreCase));

        if (summary is null)
        {
            Console.WriteLine($"No pack named '{packId}'. Run 'modpack list' to see available packs.");
            Environment.ExitCode = 1;
            return;
        }

        var gameFolder = arg2 ?? new SteamLocator().FindGameInstallPath(SteamLocator.GameAppId);
        if (gameFolder is null)
        {
            Console.WriteLine("Could not auto-detect the game folder. Pass it as the last argument.");
            Environment.ExitCode = 1;
            return;
        }

        var manifest = await reader.FetchManifestAsync(summary);
        var installedBuildId = new SteamLocator().FindGameBuildId(
            gameFolder, SteamLocator.GameAppId);
        PrintCompatibility(GameCompatibility.Evaluate(
            manifest.CompatibleGameBuildIds, installedBuildId));
        var allOptionalIds = manifest.Mods.Where(mod => !mod.Required).Select(mod => mod.Id).ToArray();
        var installedPack = new ModpackJournalStore(gameFolder).Load()
            .FirstOrDefault(entry => summary.IsId(entry.PackId));
        string[] selectedOptionalIds;
        if (arg3?.Equals("all", StringComparison.OrdinalIgnoreCase) == true)
            selectedOptionalIds = allOptionalIds;
        else if (arg3 is not null)
            selectedOptionalIds = arg3.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        else if (installedPack?.SelectedOptionalModIds is { } previousSelection)
            selectedOptionalIds = previousSelection.ToArray();
        else if (installedPack is not null)
            selectedOptionalIds = allOptionalIds; // legacy installs contained every entry
        else
            selectedOptionalIds = Array.Empty<string>();

        IModSource? fallback = summary.Source is null
            ? null
            : summary.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new HttpModSource(m => $"{summary.Source.TrimEnd('/')}/{Uri.EscapeDataString(m.FileName)}")
                : new LocalFileSource(summary.Source);

        Console.WriteLine($"Installing {summary.Name} into {gameFolder}...");
        var report = await new ModpackInstaller(gameFolder).InstallAsync(
            manifest, fallback, pack: summary, selectedOptionalIds: selectedOptionalIds);

        foreach (var line in report.Lines)
            Console.WriteLine(line);
        if (!report.Success)
            Environment.ExitCode = 1;
    }

    private static void PrintSubmission(string packId, SubmissionResult result)
    {
        var tag = result.IsValid ? "VALID" : "INVALID";
        Console.WriteLine($"[{tag}] {packId}");
        foreach (var error in result.Errors)
            Console.WriteLine($"  error: {error}");
        foreach (var warning in result.Warnings)
            Console.WriteLine($"  warning: {warning}");
    }

    private static void PrintCompatibility(GameCompatibilityResult compatibility)
    {
        switch (compatibility.Status)
        {
            case GameCompatibilityStatus.Compatible:
                Console.WriteLine($"Game compatibility: Steam build {compatibility.InstalledBuildId} is supported.");
                break;
            case GameCompatibilityStatus.Incompatible:
                Console.WriteLine(
                    $"WARNING: Steam build {compatibility.InstalledBuildId} may not be supported by this modpack. " +
                    $"Declared builds: {string.Join(", ", compatibility.CompatibleBuildIds)}.");
                break;
            case GameCompatibilityStatus.InstalledBuildUnknown:
                Console.WriteLine(
                    "WARNING: The installed Steam build could not be determined, so this modpack may not be supported. " +
                    $"Declared builds: {string.Join(", ", compatibility.CompatibleBuildIds)}.");
                break;
            default:
                Console.WriteLine(
                    "WARNING: This modpack does not declare compatible game builds and may not be supported.");
                break;
        }
    }
}
