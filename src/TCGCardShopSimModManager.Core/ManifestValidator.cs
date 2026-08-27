namespace TCGCardShopSimModManager.Core;

public sealed record ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<string> Errors { get; }

    private ValidationResult(bool isValid, IEnumerable<string> errors)
    {
        IsValid = isValid;
        Errors = errors.ToArray();
    }

    public static ValidationResult Success() => new(true, Array.Empty<string>());
    public static ValidationResult Failure(IEnumerable<string> errors) =>
        new(false, errors);
}

public sealed class ManifestValidator
{
    private static readonly HashSet<string> KnownInstallTypes = new()
    {
        "BepInExPlugin",
        "BepInEx"
    };

    public ValidationResult Validate(ModListManifest manifest)
    {
        var errors = new List<string>();

        if (manifest.ManifestVersion != 1)
            errors.Add($"Unsupported manifest version: {manifest.ManifestVersion}");

        if (string.IsNullOrWhiteSpace(manifest.Name))
            errors.Add("Manifest name is required.");

        if (!string.Equals(manifest.Game, NexusApi.GameDomain, StringComparison.OrdinalIgnoreCase))
            errors.Add($"Unsupported game '{manifest.Game}'; expected '{NexusApi.GameDomain}'.");

        if (manifest.TotalSize < 0)
            errors.Add("Total download size cannot be negative.");

        foreach (var buildId in manifest.CompatibleGameBuildIds ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(buildId) || buildId.Any(character => !char.IsDigit(character)))
                errors.Add($"Compatible game build id '{buildId}' must contain digits only.");
        }

        if (manifest.CompatibleGameBuildIds?.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            manifest.CompatibleGameBuildIds?.Count)
            errors.Add("Compatible game build ids must not contain duplicates.");

        // BUG-028: an empty mod list installs nothing useful — surface it.
        var mods = manifest.Mods;
        if (mods is null || mods.Count == 0)
            errors.Add("Manifest declares no mods; nothing will be installed.");

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods ?? System.Linq.Enumerable.Empty<ModEntry>())
        {
            if (string.IsNullOrWhiteSpace(mod.Name))
                errors.Add("A mod entry is missing a name.");
            else if (!IsSafeDirectoryName(mod.Name))
                errors.Add($"{mod.Name}: mod name cannot be used as a safe folder name.");
            else if (!seenNames.Add(mod.Name))
                errors.Add($"Duplicate mod name: {mod.Name}");

            if (string.IsNullOrWhiteSpace(mod.Id))
                errors.Add($"{mod.Name}: missing 'id' (dependencies and profiles reference mods by id).");
            else if (!seenIds.Add(mod.Id))
                errors.Add($"Duplicate mod id: {mod.Id}");

            if (string.IsNullOrWhiteSpace(mod.Sha256))
                errors.Add($"{mod.Name}: missing SHA-256 hash.");
            else if (mod.Sha256.Length != 64 || mod.Sha256.Any(character => !Uri.IsHexDigit(character)))
                errors.Add($"{mod.Name}: SHA-256 hash must contain exactly 64 hexadecimal characters.");

            // BUG-025 / BUG-032: the "BepInEx" install type is reserved for the
            // framework entry (id == bepinex). It must not be used by ordinary
            // mods, and the framework must use exactly this type.
            if (!KnownInstallTypes.Contains(mod.InstallType))
                errors.Add($"{mod.Name}: unknown install type '{mod.InstallType}'.");
            else if (mod.InstallType == ModListConventions.BepInExInstallType &&
                     !string.Equals(mod.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{mod.Name}: install type '{mod.InstallType}' is reserved for the framework entry (id '{ModListConventions.BepInExModId}').");
            else if (string.Equals(mod.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase) &&
                     mod.InstallType != ModListConventions.BepInExInstallType)
                errors.Add($"{mod.Name}: the framework entry (id '{ModListConventions.BepInExModId}') must use install type '{ModListConventions.BepInExInstallType}'.");

            if (string.Equals(mod.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase) &&
                !mod.Required)
                errors.Add($"{mod.Name}: the framework entry must be required.");

            // BUG-024: reject real traversal (a ".." path segment or a rooted
            // path), but allow ".." to appear inside a filename (e.g. MyMod..v1.zip).
            if (string.IsNullOrWhiteSpace(mod.Archive))
                errors.Add($"{mod.Name}: archive path is required.");
            else if (IsUnsafeRelativePath(mod.Archive))
                errors.Add($"{mod.Name}: archive path is unsafe ('{mod.Archive}')");

            if (mod.DownloadUrl is { Length: > 0 } downloadUrl &&
                (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
                 uri.Scheme is not ("http" or "https")))
                errors.Add($"{mod.Name}: download URL must be an absolute HTTP or HTTPS URL.");

            if (mod.NexusModId <= 0)
                errors.Add($"{mod.Name}: Nexus mod id must be positive.");
            if (mod.NexusFileId <= 0)
                errors.Add($"{mod.Name}: Nexus file id must be positive.");

            var excludedPaths = mod.ExcludedArchivePaths ?? new List<string>();
            foreach (var excludedPath in excludedPaths)
            {
                if (string.IsNullOrWhiteSpace(excludedPath) ||
                    excludedPath.Contains('\\') ||
                    IsUnsafeArchiveExclusion(excludedPath))
                {
                    errors.Add($"{mod.Name}: excluded archive path is unsafe ('{excludedPath}')");
                }
            }

            if (excludedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != excludedPaths.Count)
                errors.Add($"{mod.Name}: excluded archive paths must not contain duplicates.");
        }

        var modsById = (mods ?? new List<ModEntry>())
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Id))
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods?.Where(mod => mod.Required) ?? Enumerable.Empty<ModEntry>())
        {
            foreach (var dependencyId in mod.Dependencies ?? new List<string>())
            {
                if (modsById.TryGetValue(dependencyId, out var dependency) && !dependency.Required)
                    errors.Add($"{mod.Name}: required mod depends on optional mod '{dependency.Name}'. Mark the dependency as required.");
            }
        }

        return errors.Count == 0 ? ValidationResult.Success() : ValidationResult.Failure(errors);
    }

    private static bool IsUnsafeRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            return true;

        foreach (var segment in relativePath.Split('/', '\\'))
            if (segment is "." or ".." or "")
                return true;

        return false;
    }

    private static bool IsUnsafeArchiveExclusion(string path)
    {
        var candidate = path.EndsWith("/", StringComparison.Ordinal) ? path[..^1] : path;
        return candidate.Length == 0 || IsUnsafeRelativePath(candidate);
    }

    private static bool IsSafeDirectoryName(string name)
    {
        if (name is "." or ".." || name.IndexOfAny(new[] { '/', '\\' }) >= 0)
            return false;

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
