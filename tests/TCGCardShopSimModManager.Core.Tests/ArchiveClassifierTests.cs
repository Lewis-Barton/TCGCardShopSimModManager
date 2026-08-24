using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ArchiveClassifierTests
{
    private static readonly ModEntry Mod = new(
        "example-mod", "Example Mod", null, "pack.zip", new string('0', 64),
        "BepInExPlugin", new List<string>(), new List<string>());

    private static ExtractedSource Source(string relativePath) =>
        new(relativePath, Path.Combine(@"C:\fake\extract", relativePath));

    [Fact]
    public void LooseDllAtRoot_BecomesPluginFolderLayout()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("ExampleMod.dll"),
            Source("README.md")
        });

        Assert.Contains("loose plugin folder", plan.LayoutName);
        var file = Assert.Single(plan.Files);
        Assert.Equal("BepInEx/plugins/Example Mod/ExampleMod.dll", file.DestinationRelativePath);
        var skip = Assert.Single(plan.SkippedEntries);
        Assert.Contains("README.md", skip);
    }

    [Fact]
    public void BepInExFolder_MirrorsIntoGameBepInEx()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("BepInEx/plugins/RealMod.dll"),
            Source("BepInEx/config/DumbSettings.cfg"),
            Source("BepInEx/patchers/CorePatch.dll")
        });

        Assert.Contains("BepInEx layout", plan.LayoutName);
        Assert.Equal(3, plan.Files.Count);
        Assert.Equal(
            "BepInEx/plugins/RealMod.dll",
            plan.Files.Single(f => f.SourceRelativePath == "BepInEx/plugins/RealMod.dll").DestinationRelativePath);
        Assert.Equal(
            "BepInEx/config/DumbSettings.cfg",
            plan.Files.Single(f => f.SourceRelativePath == "BepInEx/config/DumbSettings.cfg").DestinationRelativePath);
        Assert.Equal(
            "BepInEx/patchers/CorePatch.dll",
            plan.Files.Single(f => f.SourceRelativePath == "BepInEx/patchers/CorePatch.dll").DestinationRelativePath);
    }

    [Fact]
    public void SingleWrapperContainingBepInEx_StripsWrapperFolder()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("StarWars Mod/BepInEx/plugins/StarWars/cards"),
            Source("StarWars Mod/BepInEx/plugins/Phone - Overhaul/Icon.png")
        });

        Assert.Equal("wrapped BepInEx layout (strips one wrapper folder)", plan.LayoutName);
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/StarWars/cards");
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/Phone - Overhaul/Icon.png");
    }

    [Fact]
    public void WrappedBepInExRootHijackDll_IsRejected()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("Wrapper/BepInEx/plugins/RealMod.dll"),
            Source("Wrapper/BepInEx/winhttp.dll")
        });

        Assert.Single(plan.Files);
        Assert.Contains(plan.SkippedEntries, entry => entry.Contains("winhttp.dll"));
    }

    [Fact]
    public void PatcherFolder_UsesPatcherLayout()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("patchers/MyPatch.dll")
        });

        Assert.Contains("patcher layout", plan.LayoutName);
        var file = Assert.Single(plan.Files);
        Assert.Equal("BepInEx/patchers/MyPatch.dll", file.DestinationRelativePath);
    }

    [Fact]
    public void PluginsFolder_MirrorsIntoBepInExPlugins()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("plugins/EnhancedPrefabLoader.API/EnhancedPrefabLoader.API.dll"),
            Source("patchers/EnhancedPrefabLoader/Prepatch.dll"),
            Source("config/EnhancedPrefabLoader.cfg")
        });

        Assert.Equal("BepInEx content tree (mirrors plugins, patchers, and config)", plan.LayoutName);
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/EnhancedPrefabLoader.API/EnhancedPrefabLoader.API.dll");
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/patchers/EnhancedPrefabLoader/Prepatch.dll");
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/config/EnhancedPrefabLoader.cfg");
    }

    [Fact]
    public void SingleFolderContainingPluginDll_IsPreservedUnderBepInExPlugins()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("TextureReplacer/TextureReplacer.dll"),
            Source("TextureReplacer/objects_data/cards/example.txt"),
            Source("TextureReplacer/objects_textures/example.png")
        });

        Assert.Equal("wrapped plugin folder (goes under BepInEx/plugins)", plan.LayoutName);
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/TextureReplacer/TextureReplacer.dll");
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/TextureReplacer/objects_data/cards/example.txt");
        Assert.Contains(plan.Files, file => file.DestinationRelativePath ==
            "BepInEx/plugins/TextureReplacer/objects_textures/example.png");
    }

    [Fact]
    public void RootFilesWithoutStructure_MirrorIntoGameRoot()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("Data/Textures/card_back.png"),
            Source("mod.txt")
        });

        Assert.Contains("game root", plan.LayoutName);
        Assert.Equal(2, plan.Files.Count);
        Assert.Equal("Data/Textures/card_back.png", plan.Files[0].DestinationRelativePath);
        Assert.Equal("mod.txt", plan.Files[1].DestinationRelativePath);
    }

    [Fact]
    public void OnlyDocumentation_ProducesEmptyFileList()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("README.md"),
            Source("__MACOSX/something"),
            Source(".DS_Store")
        });

        Assert.Empty(plan.Files);
        Assert.Equal(3, plan.SkippedEntries.Count);
        Assert.Contains("nothing installable", plan.LayoutName);
    }

    [Fact]
    public void EmptySources_ProducesEmptyLayout()
    {
        var plan = new ArchiveClassifier().BuildPlan(Mod, Array.Empty<ExtractedSource>());
        Assert.Equal("empty archive", plan.LayoutName);
        Assert.Empty(plan.Files);
        Assert.Empty(plan.SkippedEntries);
    }

    [Fact]
    public void BepInExRootFile_IsRejected()
    {
        // A DLL placed directly at the BepInEx/ root (e.g. winhttp.dll) is a
        // loader-hijack target and must never be installed.
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("BepInEx/plugins/RealMod.dll"),
            Source("BepInEx/winhttp.dll")
        });

        Assert.Contains("BepInEx layout", plan.LayoutName);
        var real = Assert.Single(plan.Files, f => f.SourceRelativePath == "BepInEx/plugins/RealMod.dll");
        Assert.Equal("BepInEx/plugins/RealMod.dll", real.DestinationRelativePath);

        Assert.DoesNotContain(plan.Files, f => f.SourceRelativePath == "BepInEx/winhttp.dll");
        Assert.Contains(plan.SkippedEntries, s => s.Contains("winhttp.dll"));
    }

    [Fact]
    public void LooseDllInBepInExLayout_GoesToPlugins()
    {
        // A loose .dll at the archive root alongside BepInEx/ is a plugin and
        // must land under BepInEx/plugins/<mod>, not the game root.
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("BepInEx/plugins/Mod/mod.dll"),
            Source("loose.dll")
        });

        Assert.Contains("BepInEx layout", plan.LayoutName);
        Assert.Equal(2, plan.Files.Count);
        Assert.Contains(plan.Files,
            f => f.DestinationRelativePath == "BepInEx/plugins/Example Mod/loose.dll");
        Assert.Contains(plan.Files,
            f => f.DestinationRelativePath == "BepInEx/plugins/Mod/mod.dll");
    }

    [Fact]
    public void FrameworkDllUnderBepInExCore_IsAllowed()
    {
        // The genuine BepInEx framework ships files such as BepInEx/core/doorstop.dll.
        // These are not hijack targets (they live one level below the BepInEx/ root),
        // so they must mirror into the game's BepInEx/ tree, not be rejected.
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("BepInEx/core/doorstop.dll"),
            Source("BepInEx/core/BepInEx.dll"),
            Source("BepInEx/plugins/RealMod.dll")
        });

        Assert.Contains("BepInEx layout", plan.LayoutName);
        Assert.Equal(3, plan.Files.Count);
        Assert.Contains(plan.Files,
            f => f.SourceRelativePath == "BepInEx/core/doorstop.dll"
                 && f.DestinationRelativePath == "BepInEx/core/doorstop.dll");
        Assert.Contains(plan.Files,
            f => f.SourceRelativePath == "BepInEx/core/BepInEx.dll"
                 && f.DestinationRelativePath == "BepInEx/core/BepInEx.dll");
        Assert.DoesNotContain(plan.SkippedEntries, s => s.Contains("doorstop.dll"));
    }

    [Fact]
    public void RootHijackDllInBepInExLayout_IsRejected()
    {
        // BUG-001: a hijack-target DLL (winhttp.dll) at the archive root alongside a
        // BepInEx/ folder must be refused, not mirrored into the game root.
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("BepInEx/plugins/RealMod.dll"),
            Source("winhttp.dll")
        });

        Assert.Contains("BepInEx layout", plan.LayoutName);
        Assert.DoesNotContain(plan.Files, f => f.SourceRelativePath == "winhttp.dll");
        Assert.Contains(plan.SkippedEntries, s => s.Contains("winhttp.dll") && s.Contains("refused"));
    }

    [Fact]
    public void FrameworkBootstrapDllAtGameRoot_IsAllowed()
    {
        var framework = Mod with
        {
            Id = ModListConventions.BepInExModId,
            InstallType = ModListConventions.BepInExInstallType
        };
        var plan = new ArchiveClassifier().BuildPlan(framework, new[]
        {
            Source("BepInEx/core/BepInEx.dll"),
            Source("winhttp.dll")
        });

        Assert.Contains(plan.Files,
            file => file.SourceRelativePath == "winhttp.dll" &&
                    file.DestinationRelativePath == "winhttp.dll");
        Assert.DoesNotContain(plan.SkippedEntries, entry => entry.Contains("winhttp.dll"));
    }

    [Fact]
    public void GameRootHijackDll_IsRejected()
    {
        // BUG-001: a hijack-target DLL dropped at the game root (no BepInEx structure)
        // must be refused even when it is the only archive entry.
        var plan = new ArchiveClassifier().BuildPlan(Mod, new[]
        {
            Source("version.dll")
        });

        Assert.Empty(plan.Files);
        Assert.Contains(plan.SkippedEntries, s => s.Contains("version.dll") && s.Contains("refused"));
    }
}
