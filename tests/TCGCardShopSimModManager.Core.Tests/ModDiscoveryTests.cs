using System.IO.Compression;
using System.Security.Cryptography;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModDiscoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _gameFolder;
    private readonly string _sourceDir;
    private readonly string _disabledRoot;
    private readonly ModInstaller _installer;

    public ModDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "discovery-tests-" + Guid.NewGuid().ToString("N"));
        _gameFolder = Path.Combine(_root, "game");
        _sourceDir = Path.Combine(_root, "source");
        _disabledRoot = Path.Combine(_root, "disabled");
        Directory.CreateDirectory(_gameFolder);
        Directory.CreateDirectory(_sourceDir);
        _installer = new ModInstaller(_gameFolder, _disabledRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Discover_EmptyGameFolder_ReturnsNoMods()
    {
        Assert.Empty(ModDiscovery.Discover(_gameFolder, _disabledRoot));
    }

    [Fact]
    public void Discover_HandInstalledMod_IsUnknown()
    {
        // A mod placed by hand (no journal) must be reported, not hidden.
        var folder = Path.Combine(_gameFolder, "BepInEx", "plugins", "Hand Mod");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Hand.dll"), "bytes");

        var mod = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));
        Assert.Equal("Hand Mod (unmanaged, BepInEx/plugins)", mod.ModName);
        Assert.Equal(ModInventoryState.Unknown, mod.State);
    }

    [Fact]
    public void Discover_InstalledMod_IsInstalled()
    {
        Install("Example Mod", "ExampleMod.dll");

        var mod = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));
        Assert.Equal(ModInventoryState.Installed, mod.State);
    }

    [Fact]
    public void Discover_ModifiedFile_IsModified()
    {
        Install("Example Mod", "ExampleMod.dll");

        var installedFile = Path.Combine(_gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll");
        File.WriteAllText(installedFile, "tampered");

        var mod = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));
        Assert.Equal(ModInventoryState.Modified, mod.State);
    }

    [Fact]
    public void Discover_CancelledScanStopsBeforeReadingFiles()
    {
        Install("Example Mod", "ExampleMod.dll");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ModDiscovery.Discover(
                _gameFolder, _disabledRoot, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Disable_MovesFilesToDisabledAndReportsDisabled()
    {
        Install("Example Mod", "ExampleMod.dll");
        var active = Path.Combine(_gameFolder, "BepInEx", "plugins", "Example Mod");
        var disabledFile = Path.Combine(_disabledRoot, "Example Mod", "ExampleMod.dll");

        var result = _installer.Disable("Example Mod");

        Assert.True(result.Success);
        Assert.True(File.Exists(disabledFile));
        Assert.False(Directory.Exists(active)); // emptied folder pruned

        var mod = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));
        Assert.Equal(ModInventoryState.Disabled, mod.State);
    }

    [Fact]
    public void Enable_MovesFilesBackAndReportsInstalled()
    {
        Install("Example Mod", "ExampleMod.dll");
        _installer.Disable("Example Mod");

        var result = _installer.Enable("Example Mod");

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll")));

        var mod = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));
        Assert.Equal(ModInventoryState.Installed, mod.State);
    }

    [Fact]
    public void Disable_LeavesModifiedFileInPlaceAndReportsFailure()
    {
        // BUG-013: a disable that cannot move a modified file is a *partial*
        // disable and must be reported as a failure, not a silent success.
        Install("Example Mod", "ExampleMod.dll");
        var installedFile = Path.Combine(_gameFolder, "BepInEx", "plugins", "Example Mod", "ExampleMod.dll");
        File.WriteAllText(installedFile, "tampered");

        var result = _installer.Disable("Example Mod");

        Assert.False(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("Modified"));
        Assert.Contains("modified", result.Error ?? "");
        Assert.True(File.Exists(installedFile));
    }

    [Fact]
    public void Disable_UnknownMod_Fails()
    {
        var result = _installer.Disable("Never Installed");
        Assert.False(result.Success);
        Assert.Contains("No journal entry", result.Error);
    }

    [Fact]
    public void Discover_FrameworkModUnderBepInExCore_IsListed() // BUG-012
    {
        // Framework/core mods must be visible to `mods list`, not hidden because
        // they live outside plugins/patchers.
        var zipPath = CreateZip(("BepInEx/core/SomeFramework/framework.dll", "dll-bytes"));
        File.Copy(zipPath, Path.Combine(_sourceDir, "core.zip"), overwrite: true);
        var mod = new ModEntry("fw-mod", "SomeFramework", null, "core.zip",
            ComputeSha256(Path.Combine(_sourceDir, "core.zip")),
            "BepInExPlugin", new List<string>(), new List<string>());
        Assert.True(_installer.Install(mod, _sourceDir).Success);

        var discovered = ModDiscovery.Discover(_gameFolder, _disabledRoot);
        var fw = Assert.Single(discovered, m => m.ModName == "SomeFramework");

        Assert.Equal("BepInEx/core", fw.ActiveRoot);
        Assert.Equal(ModInventoryState.Installed, fw.State);
    }

    [Fact]
    public void Discover_JournaledFrameworkAcrossRootAndCore_IsOneMod()
    {
        var zipPath = CreateZip(
            ("BepInEx/core/Framework.dll", "framework"),
            ("doorstop_config.ini", "config"));
        File.Copy(zipPath, Path.Combine(_sourceDir, "framework.zip"), overwrite: true);
        var mod = new ModEntry("framework", "Framework", null, "framework.zip",
            ComputeSha256(Path.Combine(_sourceDir, "framework.zip")),
            "BepInEx", new List<string>(), new List<string>());
        Assert.True(_installer.Install(mod, _sourceDir).Success);

        var discovered = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));

        Assert.Equal("Framework", discovered.ModName);
        Assert.Equal(ModInventoryState.Installed, discovered.State);
        Assert.Equal(2, discovered.FileCount);
        Assert.Equal("Multiple locations", discovered.ActiveRoot);
    }

    [Fact]
    public void Discover_UnmanagedCoreSubdirectories_AreOneFrameworkEntry()
    {
        var first = Path.Combine(_gameFolder, "BepInEx", "core", "A");
        var second = Path.Combine(_gameFolder, "BepInEx", "core", "B");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "a.dll"), "a");
        File.WriteAllText(Path.Combine(second, "b.dll"), "b");

        var discovered = Assert.Single(ModDiscovery.Discover(_gameFolder, _disabledRoot));

        Assert.Equal("BepInEx framework files (unmanaged)", discovered.ModName);
        Assert.Equal(2, discovered.FileCount);
    }

    [Fact]
    public void Discover_SameNamedUnmanagedFoldersInDifferentRoots_AreNotMerged()
    {
        var plugin = Path.Combine(_gameFolder, "BepInEx", "plugins", "Shared");
        var patcher = Path.Combine(_gameFolder, "BepInEx", "patchers", "Shared");
        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(patcher);
        File.WriteAllText(Path.Combine(plugin, "plugin.dll"), "plugin");
        File.WriteAllText(Path.Combine(patcher, "patcher.dll"), "patcher");

        var discovered = ModDiscovery.Discover(_gameFolder, _disabledRoot);

        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, mod => mod.ModName == "Shared (unmanaged, BepInEx/plugins)");
        Assert.Contains(discovered, mod => mod.ModName == "Shared (unmanaged, BepInEx/patchers)");
    }

    // --- helpers -----------------------------------------------------------

    private void Install(string modName, string fileName)
    {
        var sourcePath = Path.Combine(_sourceDir, fileName);
        File.WriteAllText(sourcePath, "dll-bytes");

        var mod = new ModEntry("example-mod", modName, null, fileName,
            ComputeSha256(sourcePath), "BepInExPlugin", new List<string>(), new List<string>());

        var installed = _installer.Install(mod, _sourceDir);
        Assert.True(installed.Success, installed.Error);
    }

    private static string CreateZip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "discovery-tests-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }
}
