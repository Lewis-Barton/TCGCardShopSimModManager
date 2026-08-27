using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

/// <summary>Outcome of validating one submitted modpack.</summary>
public sealed record SubmissionResult(bool IsValid, List<string> Errors, List<string> Warnings)
{
    public static SubmissionResult Ok(List<string> warnings) => new(true, new List<string>(), warnings);
    public static SubmissionResult Failure(List<string> errors, List<string> warnings) =>
        new(false, errors, warnings);
}

/// <summary>
/// Checks a modpack submission before it is merged — the things a reviewer would
/// otherwise catch by eye. Reads <c>modpacks/index.json</c> and the referenced
/// manifest/logo from disk (this is a local authoring tool, not the live GitHub
/// gallery), and reports structural problems plus softer warnings.
///
/// Enforces the project's pack rules: every pack must carry a
/// <see cref="ModListConventions.BepInExModId"/> entry, and every mod must have a
/// resolvable source (DownloadUrl, NexusModId, or a pack-level fallback).
/// </summary>
public sealed class ModpackSubmissionValidator
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly string _modpacksRoot;

    public ModpackSubmissionValidator(string modpacksRoot)
    {
        _modpacksRoot = modpacksRoot;
    }

    public SubmissionResult ValidatePack(string packId)
    {
        var warnings = new List<string>();
        if (!TryReadIndex(out var index, out var indexFailure))
            return indexFailure!;

        var entry = index!.Packs!.FirstOrDefault(p =>
            p.Id.Equals(packId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            return SubmissionResult.Failure(new List<string> { $"No index entry for pack id '{packId}'." }, warnings);

        return ValidateEntry(entry);
    }

    private SubmissionResult ValidateEntry(ModpackSummary entry)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Logo: present and actually a PNG.
        // BUG-034: reject traversal/absolute logo refs before resolving.
        if (!string.IsNullOrWhiteSpace(entry.Logo) && IsUnsafeRelativePath(entry.Logo))
            return SubmissionResult.Failure(new List<string> { $"Logo reference is unsafe: '{entry.Logo}'" }, warnings);

        var logoPath = Path.Combine(_modpacksRoot, entry.Logo);
        if (!File.Exists(logoPath))
            errors.Add($"Logo file missing: {entry.Logo}");
        else if (!IsPng(logoPath))
            errors.Add($"Logo {entry.Logo} is not a PNG.");
        else if (new FileInfo(logoPath).Length < 1024)
            warnings.Add($"Logo {entry.Logo} is small (<1 KB) — check it isn't a placeholder.");

        // Manifest: present, parseable, and structurally valid.
        // BUG-034: reject traversal/absolute manifest refs before resolving.
        if (!string.IsNullOrWhiteSpace(entry.Manifest) && IsUnsafeRelativePath(entry.Manifest))
            return SubmissionResult.Failure(new List<string> { $"Manifest reference is unsafe: '{entry.Manifest}'" }, warnings);

        var manifestPath = Path.Combine(_modpacksRoot, entry.Manifest);
        if (!File.Exists(manifestPath))
            return SubmissionResult.Failure(new List<string> { $"Manifest file missing: {entry.Manifest}" }, warnings);

        ModListManifest manifest;
        try
        {
            manifest = new ManifestReader().Read(manifestPath);
        }
        catch (Exception ex)
        {
            return SubmissionResult.Failure(new List<string> { $"Manifest is not valid: {ex.Message}" }, warnings);
        }

        var manifestValidation = new ManifestValidator().Validate(manifest);
        if (!manifestValidation.IsValid)
            errors.AddRange(manifestValidation.Errors);

        // BUG-033: a manifest for a different pack must not validate as VALID
        // for the referenced index entry — promote the mismatch to an error.
        if (!manifest.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Manifest name '{manifest.Name}' does not match index name '{entry.Name}'.");

        var indexBuildIds = entry.CompatibleGameBuildIds ?? new List<string>();
        var manifestBuildIds = manifest.CompatibleGameBuildIds ?? new List<string>();
        if (!indexBuildIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(manifestBuildIds))
            errors.Add("Compatible game build ids differ between index.json and the manifest.");
        if (manifestBuildIds.Count == 0)
            warnings.Add("Pack does not declare compatible Steam build ids; users will see a compatibility warning.");

        // Every mod needs a way to actually be fetched.
        foreach (var mod in manifest.Mods)
        {
            var resolvable = !string.IsNullOrEmpty(mod.DownloadUrl)
                             || mod.NexusModId is not null
                             || !string.IsNullOrEmpty(entry.Source);
            if (!resolvable)
                errors.Add($"{mod.Name}: no source — needs a DownloadUrl, NexusModId, or a pack-level 'source'.");
        }

        // The BepInEx framework must ship in every pack, using exactly the
        // reserved framework install type.
        var bepinex = manifest.Mods.FirstOrDefault(m =>
            m.Id.Equals(ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase));
        if (bepinex is null)
            errors.Add($"Pack is missing the required BepInEx entry (id '{ModListConventions.BepInExModId}', " +
                       $"installType '{ModListConventions.BepInExInstallType}').");
        else if (bepinex.InstallType != ModListConventions.BepInExInstallType)
            errors.Add($"The BepInEx framework entry must use install type '{ModListConventions.BepInExInstallType}', found '{bepinex.InstallType}'.");

        return errors.Count == 0
            ? SubmissionResult.Ok(warnings)
            : SubmissionResult.Failure(errors, warnings);
    }

    public List<(string PackId, SubmissionResult Result)> ValidateAll()
    {
        if (!TryReadIndex(out var index, out var indexFailure))
            return new List<(string, SubmissionResult)>
            {
                ("(index.json)", indexFailure!)
            };

        return index!.Packs!
            .Select(entry => (entry.Id, ValidateEntry(entry)))
            .ToList();
    }

    private bool TryReadIndex(out ModpackIndex? index, out SubmissionResult? failure)
    {
        var indexPath = Path.Combine(_modpacksRoot, "index.json");
        if (!File.Exists(indexPath))
        {
            index = null;
            failure = SubmissionResult.Failure(
                new List<string> { $"Missing index.json at {indexPath}." }, new List<string>());
            return false;
        }

        try
        {
            index = JsonSerializer.Deserialize<ModpackIndex>(File.ReadAllText(indexPath), Options)
                    ?? throw new InvalidOperationException("index.json parsed to null");
        }
        catch (Exception ex)
        {
            index = null;
            failure = SubmissionResult.Failure(
                new List<string> { $"index.json is not valid JSON: {ex.Message}" }, new List<string>());
            return false;
        }

        // A malformed index may omit 'packs'; report it instead of letting validation throw.
        if (index.Packs is null)
        {
            failure = SubmissionResult.Failure(
                new List<string> { "index.json is missing the required 'packs' array." }, new List<string>());
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsPng(string path)
    {
        try
        {
            var header = new byte[8];
            using var stream = File.OpenRead(path);
            return stream.Read(header, 0, 8) == 8 &&
                   header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
                   header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnsafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return false;
        if (Path.IsPathRooted(relativePath))
            return true;

        foreach (var segment in relativePath.Split('/', '\\'))
            if (segment is ".." or "")
                return true;

        return false;
    }
}
