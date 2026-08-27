using System.IO;
using Xunit;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ModpackSubmissionTests : IDisposable
{
    private readonly string _root;

    public ModpackSubmissionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "modpack-submission-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ValidatePack_Passes_ForAWellFormedSubmission()
    {
        WriteValidPack("essential-qol");
        var result = new ModpackSubmissionValidator(_root).ValidatePack("essential-qol");
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("compatible Steam build ids"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenBepInExEntryMissing()
    {
        WritePackWithoutBepInEx();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("no-bepinex");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("BepInEx"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenModHasNoSource()
    {
        WritePackWithUnsourcedMod();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("unsourced");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no source"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenLogoMissing()
    {
        WriteValidPack("essential-qol", withLogo: false);
        var result = new ModpackSubmissionValidator(_root).ValidatePack("essential-qol");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Logo"));
    }

    [Fact]
    public void ValidateAll_ReportsEveryPack()
    {
        WriteValidPack("essential-qol");
        var results = new ModpackSubmissionValidator(_root).ValidateAll();
        var entry = Assert.Single(results);
        Assert.Equal("essential-qol", entry.PackId);
        Assert.True(entry.Result.IsValid);
    }

    [Fact]
    public void ValidateAll_ValidatesMultipleIndexEntries()
    {
        WriteValidPacks("first-pack", "second-pack");

        var results = new ModpackSubmissionValidator(_root).ValidateAll();

        Assert.Equal(["first-pack", "second-pack"], results.Select(result => result.PackId));
        Assert.All(results, result => Assert.True(
            result.Result.IsValid, string.Join("\n", result.Result.Errors)));
    }

    [Fact]
    public void ValidatePack_Fails_WhenIndexMissingPacksArray() // BUG-002
    {
        WriteIndexNoPacks();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("anything");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("packs"));
    }

    [Fact]
    public void ValidateAll_Fails_WhenIndexMissingPacksArray() // BUG-002
    {
        WriteIndexNoPacks();
        var results = new ModpackSubmissionValidator(_root).ValidateAll();
        var entry = Assert.Single(results);
        Assert.False(entry.Result.IsValid);
        Assert.Contains(entry.Result.Errors, e => e.Contains("packs"));
    }

    [Fact]
    public void ValidateAll_Fails_WhenIndexMissing() // BUG-031
    {
        // No index.json written at all — must surface as a failure, not "all valid".
        var results = new ModpackSubmissionValidator(_root).ValidateAll();
        var entry = Assert.Single(results);
        Assert.Equal("(index.json)", entry.PackId);
        Assert.False(entry.Result.IsValid);
    }

    [Fact]
    public void ValidatePack_Fails_WhenFrameworkUsesWrongInstallType() // BUG-032
    {
        WritePackWrongFrameworkType();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("broken-fw");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("framework entry must use install type"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenManifestNameMismatchesIndex() // BUG-033
    {
        WritePackWrongName();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("testpack");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not match"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenLogoReferenceUnsafe() // BUG-034
    {
        WritePackUnsafeLogo();
        var result = new ModpackSubmissionValidator(_root).ValidatePack("unsafe");
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unsafe"));
    }

    [Fact]
    public void ValidatePack_Fails_WhenIndexAndManifestBuildsDiffer()
    {
        WriteValidPack("essential-qol");
        var indexPath = Path.Combine(_root, "index.json");
        var index = File.ReadAllText(indexPath).Replace(
            "\"version\":\"1.0.0\"",
            "\"version\":\"1.0.0\",\"compatibleGameBuildIds\":[\"123\"]");
        File.WriteAllText(indexPath, index);

        var result = new ModpackSubmissionValidator(_root).ValidatePack("essential-qol");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("differ"));
    }

    // --- helpers ---------------------------------------------------------

    private void WriteValidPack(string id, bool withLogo = true)
    {
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJson(id));
        var packDir = Path.Combine(_root, id);
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJson());
        if (withLogo)
            File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private void WriteValidPacks(params string[] ids)
    {
        var entries = ids.Select(id =>
            "{\"id\":\"" + id + "\",\"name\":\"Pack One\"," +
            "\"shortDescription\":\"desc\",\"logo\":\"" + id + "/logo.png\"," +
            "\"manifest\":\"" + id + "/manifest.json\",\"version\":\"1.0.0\"}");
        File.WriteAllText(
            Path.Combine(_root, "index.json"),
            "{\"version\":1,\"packs\":[" + string.Join(',', entries) + "]}");
        foreach (var id in ids)
        {
            var packDir = Path.Combine(_root, id);
            Directory.CreateDirectory(packDir);
            File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJson());
            File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
        }
    }

    private void WritePackWithoutBepInEx()
    {
        var id = "no-bepinex";
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJson(id));
        var packDir = Path.Combine(_root, id);
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJsonNoBepInEx());
        File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private void WritePackWithUnsourcedMod()
    {
        var id = "unsourced";
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJson(id));
        var packDir = Path.Combine(_root, id);
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJsonUnsourced());
        File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private void WriteIndexNoPacks()
    {
        File.WriteAllText(Path.Combine(_root, "index.json"), "{\"version\":1}");
    }

    private void WritePackWrongFrameworkType()
    {
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJson("broken-fw"));
        var packDir = Path.Combine(_root, "broken-fw");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJsonWrongFrameworkType());
        File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private void WritePackWrongName()
    {
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJson("testpack"));
        var packDir = Path.Combine(_root, "testpack");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJsonWrongName());
        File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private void WritePackUnsafeLogo()
    {
        File.WriteAllText(Path.Combine(_root, "index.json"), IndexJsonWithLogo("unsafe", "../escape.png"));
        var packDir = Path.Combine(_root, "unsafe");
        Directory.CreateDirectory(packDir);
        File.WriteAllText(Path.Combine(packDir, "manifest.json"), ManifestJson());
        File.WriteAllBytes(Path.Combine(packDir, "logo.png"), MakePng());
    }

    private static string IndexJson(string id) =>
        "{\"version\":1,\"packs\":[{\"id\":\"" + id + "\",\"name\":\"Pack One\"," +
        "\"shortDescription\":\"desc\",\"logo\":\"" + id + "/logo.png\"," +
        "\"manifest\":\"" + id + "/manifest.json\",\"version\":\"1.0.0\"}]}";

    private static string ManifestJson() =>
        "{\"manifestVersion\":1,\"name\":\"Pack One\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[" +
        "{\"id\":\"bepinex\",\"name\":\"BepInEx\",\"version\":\"5.4.23\",\"archive\":\"bepinex.zip\"," +
        "\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"installType\":\"BepInEx\",\"dependencies\":[],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/bepinex.zip\"}," +
        "{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":\"1.0.0\",\"archive\":\"mod.zip\"," +
        "\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"installType\":\"BepInExPlugin\",\"dependencies\":[\"bepinex\"],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/mod.zip\"}]}";

    private static string ManifestJsonNoBepInEx() =>
        "{\"manifestVersion\":1,\"name\":\"Pack One\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":\"1.0.0\",\"archive\":\"mod.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInExPlugin\",\"dependencies\":[],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/mod.zip\"}]}";

    private static string ManifestJsonUnsourced() =>
        "{\"manifestVersion\":1,\"name\":\"Pack One\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":\"1.0.0\",\"archive\":\"mod.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInExPlugin\",\"dependencies\":[],\"conflicts\":[]}]}";

    private static string ManifestJsonWrongFrameworkType() =>
        "{\"manifestVersion\":1,\"name\":\"Pack One\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[{\"id\":\"bepinex\",\"name\":\"BepInEx\",\"version\":\"5.4.23\",\"archive\":\"bepinex.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInExPlugin\",\"dependencies\":[],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/bepinex.zip\"}," +
        "{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":\"1.0.0\",\"archive\":\"mod.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInExPlugin\",\"dependencies\":[\"bepinex\"],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/mod.zip\"}]}";

    private static string ManifestJsonWrongName() =>
        "{\"manifestVersion\":1,\"name\":\"Totally Different Pack\",\"game\":\"tcgcardshopsimulator\"," +
        "\"mods\":[{\"id\":\"bepinex\",\"name\":\"BepInEx\",\"version\":\"5.4.23\",\"archive\":\"bepinex.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInEx\",\"dependencies\":[],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/bepinex.zip\"}," +
        "{\"id\":\"example-mod\",\"name\":\"Example Mod\",\"version\":\"1.0.0\",\"archive\":\"mod.zip\"," +
        "\"sha256\":\"abc\",\"installType\":\"BepInExPlugin\",\"dependencies\":[\"bepinex\"],\"conflicts\":[]," +
        "\"downloadUrl\":\"https://example.com/mod.zip\"}]}";

    private static string IndexJsonWithLogo(string id, string logo) =>
        "{\"version\":1,\"packs\":[{\"id\":\"" + id + "\",\"name\":\"Pack One\"," +
        "\"shortDescription\":\"desc\",\"logo\":\"" + logo + "\"," +
        "\"manifest\":\"" + id + "/manifest.json\",\"version\":\"1.0.0\"}]}";

    private static byte[] MakePng()
    {
        // Valid PNG signature + padding so it clears the <1 KB placeholder warning.
        var bytes = new byte[2048];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        return bytes;
    }
}
