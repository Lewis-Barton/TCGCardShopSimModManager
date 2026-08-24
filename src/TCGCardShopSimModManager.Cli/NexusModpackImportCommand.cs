using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Cli;

/// <summary>One-time authoring helper for turning exact Nexus file links into a manifest draft.</summary>
public static class NexusModpackImportCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static async Task Run(string? linksPath, string? packFolder, string? packName)
    {
        if (string.IsNullOrWhiteSpace(linksPath) || string.IsNullOrWhiteSpace(packFolder))
        {
            Console.WriteLine("Usage: modpack import <links.txt> <packFolder> [packName]");
            Environment.ExitCode = 2;
            return;
        }

        if (!File.Exists(linksPath))
        {
            Console.Error.WriteLine($"Link file not found: {linksPath}");
            Environment.ExitCode = 1;
            return;
        }

        IReadOnlyList<ImportRequest> requests;
        try
        {
            requests = ParseRequests(File.ReadAllLines(linksPath));
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        if (requests.Count == 0)
        {
            Console.Error.WriteLine("The link file contains no Nexus file links.");
            Environment.ExitCode = 1;
            return;
        }

        if (requests.Count(request => request.IsBepInEx) != 1)
        {
            Console.Error.WriteLine("Mark exactly one link as 'bepinex <url>'. Every hosted pack needs one framework entry.");
            Environment.ExitCode = 1;
            return;
        }

        var duplicate = requests.GroupBy(request => request.Link)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            Console.Error.WriteLine(
                $"The Nexus file {duplicate.Key.ModId}/{duplicate.Key.FileId} appears more than once.");
            Environment.ExitCode = 1;
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var auth = NexusAuth.Unified(http);
        using var api = new NexusApi(NexusApi.ApiBaseUrl(), NexusApi.GameDomain, NexusApi.UserAgent, http);
        NexusUser user;
        try
        {
            user = await api.GetUserAsync(auth, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not authenticate with Nexus: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        if (!user.IsPremium)
        {
            Console.Error.WriteLine(
                "Nexus only provides automatic download links to Premium accounts. " +
                "This importer needs a Premium account to hash each selected archive.");
            Environment.ExitCode = 1;
            return;
        }

        var resolvedPackName = string.IsNullOrWhiteSpace(packName)
            ? new DirectoryInfo(Path.GetFullPath(packFolder)).Name
            : packName.Trim();
        var cacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCGCardShopSimModManager", "modpack-import-cache", Slug(resolvedPackName));
        Directory.CreateDirectory(cacheFolder);

        var entries = new List<ModEntry>();
        long totalSize = 0;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            Console.WriteLine(
                $"[{index + 1}/{requests.Count}] Reading Nexus mod {request.Link.ModId}, file {request.Link.FileId}...");

            try
            {
                var mod = await api.GetModInfoAsync(request.Link.ModId, auth, CancellationToken.None);
                var file = await api.GetFileInfoAsync(
                    request.Link.ModId, request.Link.FileId, auth, CancellationToken.None);
                if (!ArchiveExtractor.IsSupportedArchive(file.FileName))
                    throw new InvalidOperationException(
                        $"'{file.FileName}' is not a supported archive. " +
                        "Supported formats are ZIP, RAR, 7Z, TAR, GZ, TGZ, BZ2 and XZ.");

                var archive = $"{request.Link.ModId}-{request.Link.FileId}-{SafeFileName(file.FileName)}";
                var cachePath = Path.Combine(cacheFolder, archive);
                var (sha256, actualSize, fromCache) = File.Exists(cachePath)
                    ? await HashFileAsync(cachePath)
                    : await DownloadAndHashAsync(
                        api, auth, http, request.Link.ModId, request.Link.FileId, cachePath);
                totalSize = checked(totalSize + actualSize);

                var name = SafeDisplayName(mod.Name, request.Link.ModId);
                if (entries.Any(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name = $"{name} - {request.Link.FileId}";
                var id = request.IsBepInEx
                    ? ModListConventions.BepInExModId
                    : $"{Slug(name)}-{request.Link.ModId}-{request.Link.FileId}";
                entries.Add(new ModEntry(
                    id,
                    name,
                    string.IsNullOrWhiteSpace(file.Version) ? null : file.Version,
                    archive,
                    sha256,
                    request.IsBepInEx ? ModListConventions.BepInExInstallType : "BepInExPlugin",
                    new List<string>(),
                    new List<string>(),
                    request.Link.ModId,
                    request.Link.FileId,
                    Required: request.Required));
                Console.WriteLine(
                    $"  {(fromCache ? "Cached" : "Downloaded")} {file.FileName} ({actualSize:N0} bytes). SHA-256 recorded.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Import stopped at line {request.LineNumber} ({request.Source}): {ex.Message}");
                Console.Error.WriteLine($"Completed downloads remain cached at {cacheFolder}");
                Environment.ExitCode = 1;
                return;
            }
        }

        Directory.CreateDirectory(packFolder);
        var normalManifestPath = Path.Combine(packFolder, "manifest.json");
        var outputPath = File.Exists(normalManifestPath)
            ? Path.Combine(packFolder, "manifest.imported.json")
            : normalManifestPath;
        var manifest = new ModListManifest(
            1, resolvedPackName, NexusApi.GameDomain, entries, totalSize);
        var validation = new ManifestValidator().Validate(manifest);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine("The generated manifest did not pass validation:");
            foreach (var error in validation.Errors)
                Console.Error.WriteLine($"  {error}");
            Environment.ExitCode = 1;
            return;
        }
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);

        Console.WriteLine($"Wrote {entries.Count} entries to {outputPath}");
        Console.WriteLine($"Authoring cache: {cacheFolder}");
        if (!outputPath.Equals(normalManifestPath, StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("An existing manifest.json was left unchanged. Review manifest.imported.json before replacing it.");
        Console.WriteLine("Review required/optional choices, dependencies, conflicts, and compatibleGameBuildIds before publishing.");
    }

    internal static IReadOnlyList<ImportRequest> ParseRequests(IEnumerable<string> lines)
    {
        var requests = new List<ImportRequest>();
        var lineNumber = 0;
        foreach (var original in lines)
        {
            lineNumber++;
            var value = StripInlineComment(original).Trim();
            if (value.Length == 0 || value.StartsWith('#'))
                continue;

            var role = "required";
            var separator = value.IndexOfAny([' ', '\t']);
            if (separator > 0)
            {
                var candidate = value[..separator].ToLowerInvariant();
                if (candidate is "required" or "optional" or "bepinex")
                {
                    role = candidate;
                    value = value[(separator + 1)..].Trim();
                }
            }

            if (!NexusFileLink.TryParse(value, out var link) || link is null)
            {
                if (NexusModLink.TryParse(value, out var mod) && mod is not null)
                    throw new FormatException(
                        $"Line {lineNumber} identifies Nexus mod {mod.ModId}, but not a file. " +
                        $"Run 'modpack files {mod.ModId}', then paste the chosen nexus:{mod.ModId}:<fileId> selector here.");
                throw new FormatException(
                    $"Line {lineNumber} is not an exact Nexus file link or nexus:<modId>:<fileId> selector: {original}");
            }

            requests.Add(new ImportRequest(
                lineNumber, original.Trim(), link, role != "optional", role == "bepinex"));
        }

        return requests;
    }

    private static string StripInlineComment(string value)
    {
        for (var index = 1; index < value.Length; index++)
        {
            if (value[index] == '#' && char.IsWhiteSpace(value[index - 1]))
                return value[..index];
        }

        return value;
    }

    private static async Task<(string Sha256, long Size, bool FromCache)> DownloadAndHashAsync(
        NexusApi api,
        NexusAuth auth,
        HttpClient http,
        long modId,
        long fileId,
        string destination)
    {
        var uri = await api.GetDownloadUriAsync(modId, fileId, auth, CancellationToken.None);
        var partial = destination + ".partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(NexusApi.UserAgent, "1.0"));
            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var sha = SHA256.Create();
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                sha.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            await output.FlushAsync();
            output.Close();
            File.Move(partial, destination, overwrite: true);
            return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), size, false);
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    private static async Task<(string Sha256, long Size, bool FromCache)> HashFileAsync(string path)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream);
        return (Convert.ToHexString(hash).ToLowerInvariant(), stream.Length, true);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(Path.GetFileName(value)
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray());
    }

    private static string SafeDisplayName(string value, long modId)
    {
        var safe = SafeFileName(value).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(safe) ? $"Nexus mod {modId}" : safe;
    }

    private static string Slug(string value)
    {
        var slug = new string(value.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray());
        var result = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return result.Length == 0 ? "pack" : result;
    }

    internal sealed record ImportRequest(
        int LineNumber,
        string Source,
        NexusFileLink Link,
        bool Required,
        bool IsBepInEx);
}
