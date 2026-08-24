namespace TCGCardShopSimModManager.Core;

/// <summary>
/// Turns the files that came out of an archive into a concrete file-by-file
/// installation plan, guided by how the mod is structured. The rules are fixed
/// and documented so every archive produces a predictable plan.
///
/// Layout rules, checked in order:
///   1. Contains a BepInEx/ folder -> mirror the genuine framework tree into the
///      game's BepInEx/. The only prohibition is a known DLL search-order hijack
///      target (e.g. winhttp.dll, version.dll) sitting directly at the game root or
///      the BepInEx/ root; those are refused unless the reserved framework entry
///      needs its game-root bootstrap DLL.
///   2. Loose .dll at the archive root -> whole mod goes to BepInEx/plugins/{Name}/.
///   3. Contains one wrapper folder with a BepInEx/ folder inside -> strip the
///      wrapper and mirror the BepInEx tree.
///   4. Contains a plugins/ folder -> treat plugins/, patchers/, and config/ as
///      the contents of a BepInEx folder and mirror them there.
///   5. Contains one top-level folder with a plugin DLL directly inside it ->
///      preserve that folder under BepInEx/plugins/.
///   6. Contains a patchers/ folder -> files go to BepInEx/patchers/.
///   7. Anything else -> mirror the archive root straight into the game root, except
///      hijack-target DLLs at the game root which are refused.
/// Documentation and OS-junk files are skipped, not installed.
/// </summary>
public sealed class ArchiveClassifier
{
    public enum LayoutKind
    {
        BepInExLayout,
        WrappedBepInExLayout,
        PluginFolder,
        PluginTree,
        WrappedPluginFolder,
        Patcher,
        GameRoot,
        Empty
    }

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "readme", "readme.md", "readme.txt", "readme.rst",
        "license", "license.md", "license.txt", "license.rst",
        "notice", "notice.md", "notice.txt",
        "changelog", "changelog.md", "changelog.txt",
        "unknown", "unknown.md"
    };

    private static readonly HashSet<string> IgnoredFileNamesAnywhere = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", ".ds_store", "desktop.ini"
    };

    private const string MacOsJunkDirectory = "__macosx";

    private static readonly HashSet<string> BepInExContentDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "plugins", "patchers", "config"
    };

    // Known DLL search-order hijack targets. A mod that drops one of these at the
    // game root or the BepInEx/ root would have attacker code loaded by the game (or
    // BepInEx) before any legitimate DLL — pre-launch RCE. We refuse exactly these
    // names at exactly those roots, and let everything else mirror, so the genuine
    // BepInEx framework (e.g. BepInEx/core/doorstop.dll) still installs normally.
    private static readonly HashSet<string> KnownHijackTargetNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "winhttp.dll", "version.dll", "winmm.dll", "dbghelp.dll",
        "d3d9.dll", "d3d11.dll", "dxgi.dll", "dsound.dll",
        "mscoree.dll", "propsys.dll", "userenv.dll", "dinput8.dll",
        "dwrite.dll", "apphelp.dll", "comctl32.dll", "secur32.dll",
        "cryptbase.dll", "msimg32.dll", "uxtheme.dll", "ws2_32.dll"
    };

    private static bool IsHijackTarget(string path) =>
        KnownHijackTargetNames.Contains(Path.GetFileName(path));

    public InstallPlan BuildPlan(
        ModEntry mod,
        IReadOnlyCollection<ExtractedSource> sources,
        IReadOnlyCollection<string>? rejected = null)
    {
        var kind = DetectLayout(sources);
        var files = new List<ArchiveContentEntry>();
        var skipped = new List<string>();

        foreach (var source in sources.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = source.RelativePath;

            if (IsIgnored(relativePath))
            {
                skipped.Add($"{relativePath} (ignored: documentation or OS junk)");
                continue;
            }

            var destinationRelativePath = MapToDestination(relativePath, mod, kind);
            if (destinationRelativePath is null)
            {
                var reason = IsHijackTarget(relativePath)
                    ? $"{relativePath} (refused: known DLL-hijack target at a sensitive root)"
                    : $"{relativePath} (not covered by layout {kind})";
                skipped.Add(reason);
                continue;
            }

            files.Add(new ArchiveContentEntry(source.AbsolutePath, relativePath, destinationRelativePath));
        }

        var layoutName = kind == LayoutKind.Empty
            ? "empty archive"
            : files.Count == 0
                ? "nothing installable (documentation/OS junk only)"
                : LayoutDisplayName(kind);

        return new InstallPlan(mod, layoutName, files, skipped, rejected?.ToList() ?? new List<string>());
    }

    private static LayoutKind DetectLayout(IReadOnlyCollection<ExtractedSource> sources)
    {
        if (sources.Count == 0)
            return LayoutKind.Empty;

        // Top-level folder names tell us which layout this mod uses.
        var topLevelNames = sources
            .Select(s => s.RelativePath.Split('/')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (topLevelNames.Contains("BepInEx"))
            return LayoutKind.BepInExLayout;

        if (topLevelNames.Count == 1 && sources.Any(source =>
                source.RelativePath.Split('/').ElementAtOrDefault(1)?
                    .Equals("BepInEx", StringComparison.OrdinalIgnoreCase) == true))
            return LayoutKind.WrappedBepInExLayout;

        if (sources.Any(s =>
                !s.RelativePath.Contains('/') &&
                Path.GetExtension(s.RelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            return LayoutKind.PluginFolder;

        if (topLevelNames.Contains("plugins"))
            return LayoutKind.PluginTree;

        if (topLevelNames.Contains("patchers"))
            return LayoutKind.Patcher;

        if (topLevelNames.Count == 1 && sources.Any(source =>
                source.RelativePath.Count(character => character == '/') == 1 &&
                Path.GetExtension(source.RelativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            return LayoutKind.WrappedPluginFolder;

        return LayoutKind.GameRoot;
    }

    private static string? MapToDestination(string relativePath, ModEntry mod, LayoutKind kind)
    {
        var segments = relativePath.Split('/');

        switch (kind)
        {
            case LayoutKind.BepInExLayout when segments[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase):
                // Mirror the genuine BepInEx framework tree (plugins, patchers, config,
                // core/doorstop.dll, etc.) into the game's BepInEx/. The only file we
                // must never allow is a known DLL search-order hijack target placed
                // directly at the BepInEx/ root (e.g. BepInEx/winhttp.dll). Framework
                // files live one level deeper, so they still mirror.
                if (segments.Length == 2 && IsHijackTarget(segments[1]))
                    return null;
                return $"BepInEx/{string.Join('/', segments[1..])}";

            case LayoutKind.BepInExLayout:
                // Archive-root files alongside BepInEx/ (docs, loose plugin DLLs, etc.).
                // A loose .dll is a plugin and belongs under BepInEx/plugins/<mod>/; a
                // hijack-target name is never a legitimate plugin, so refuse it. Any
                // other root file mirrors into the game root — but never a hijack target
                // sitting directly at the game root.
                if (Path.GetExtension(relativePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (segments.Length == 1 && IsFramework(mod) && IsHijackTarget(relativePath))
                        return relativePath;
                    if (IsHijackTarget(relativePath))
                        return null;
                    return $"BepInEx/plugins/{mod.Name}/{relativePath}";
                }
                if (segments.Length == 1 && IsHijackTarget(relativePath))
                    return null;
                return relativePath;

            case LayoutKind.PluginFolder:
                if (IsHijackTarget(segments[^1]))
                    return null;
                return $"BepInEx/plugins/{mod.Name}/{relativePath}";

            case LayoutKind.WrappedBepInExLayout when
                segments.Length >= 3 &&
                segments[1].Equals("BepInEx", StringComparison.OrdinalIgnoreCase):
                if (segments.Length == 3 && IsHijackTarget(segments[2]))
                    return null;
                return $"BepInEx/{string.Join('/', segments[2..])}";

            case LayoutKind.WrappedBepInExLayout:
                return null;

            case LayoutKind.PluginTree when BepInExContentDirectories.Contains(segments[0]):
                if (IsHijackTarget(segments[^1]))
                    return null;
                return $"BepInEx/{relativePath}";

            case LayoutKind.PluginTree:
                if (IsHijackTarget(segments[^1]))
                    return null;
                return $"BepInEx/plugins/{mod.Name}/{relativePath}";

            case LayoutKind.WrappedPluginFolder:
                if (IsHijackTarget(segments[^1]))
                    return null;
                return $"BepInEx/plugins/{relativePath}";

            case LayoutKind.Patcher when segments[0].Equals("patchers", StringComparison.OrdinalIgnoreCase):
                if (segments.Length == 2 && IsHijackTarget(segments[1]))
                    return null;
                return $"BepInEx/patchers/{string.Join('/', segments[1..])}";

            case LayoutKind.Patcher:
                if (IsHijackTarget(segments[^1]))
                    return null;
                return $"BepInEx/plugins/{mod.Name}/{relativePath}";

            case LayoutKind.GameRoot:
                if (segments.Length == 1 && IsHijackTarget(segments[0]))
                    return null;
                return relativePath;

            default:
                return null;
        }
    }

    private static bool IsIgnored(string relativePath)
    {
        if (relativePath.StartsWith(MacOsJunkDirectory + "/", StringComparison.OrdinalIgnoreCase))
            return true;

        var fileName = relativePath.Split('/').Last();
        return IgnoredFileNames.Contains(fileName) || IgnoredFileNamesAnywhere.Contains(fileName);
    }

    private static bool IsFramework(ModEntry mod) =>
        string.Equals(mod.Id, ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(mod.InstallType, ModListConventions.BepInExInstallType, StringComparison.Ordinal);

    private static string LayoutDisplayName(LayoutKind kind) => kind switch
    {
        LayoutKind.BepInExLayout => "BepInEx layout (mirrors the game's BepInEx folder)",
        LayoutKind.WrappedBepInExLayout => "wrapped BepInEx layout (strips one wrapper folder)",
        LayoutKind.PluginFolder => "loose plugin folder (goes to BepInEx/plugins/<mod name>)",
        LayoutKind.PluginTree => "BepInEx content tree (mirrors plugins, patchers, and config)",
        LayoutKind.WrappedPluginFolder => "wrapped plugin folder (goes under BepInEx/plugins)",
        LayoutKind.Patcher => "patcher layout (goes to BepInEx/patchers)",
        LayoutKind.GameRoot => "game root files (mirrors into the game folder root)",
        _ => "empty archive"
    };
}
