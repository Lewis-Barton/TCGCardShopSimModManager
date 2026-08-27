using System.Collections.Generic;
using Xunit;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class ManifestValidatorTests
{
    [Fact]
    public void FailureResultDoesNotExposeOrRetainMutableErrorList()
    {
        var errors = new List<string> { "first" };

        var result = ValidationResult.Failure(errors);
        errors.Add("second");

        Assert.Equal(typeof(IReadOnlyList<string>),
            typeof(ValidationResult).GetProperty(nameof(ValidationResult.Errors))!.PropertyType);
        Assert.Equal(["first"], result.Errors);
    }

    private static ModListManifest Manifest(params ModEntry[] mods) =>
        new(1, "Test Pack", "tcgcardshopsimulator", new List<ModEntry>(mods));

    private static ModEntry Mod(string id, string installType, string archive) =>
        new(id, id, "1.0.0", archive, new string('a', 64), installType,
            new List<string>(), new List<string>());

    [Fact]
    public void Validate_AcceptsDotDotInsideFilename() // BUG-024
    {
        // ".." inside a filename is not a traversal — this must validate.
        var manifest = Manifest(Mod("example-mod", "BepInExPlugin", "MyMod..v1.zip"));
        var result = new ManifestValidator().Validate(manifest);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void Validate_RejectsPathTraversalInArchive() // BUG-024
    {
        var manifest = Manifest(Mod("example-mod", "BepInExPlugin", "../escape.zip"));
        var result = new ManifestValidator().Validate(manifest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unsafe"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("folder/name")]
    [InlineData("folder\\name")]
    [InlineData(".")]
    [InlineData("..")]
    public void Validate_RejectsModNameThatIsUnsafeAsFolder(string name)
    {
        var mod = Mod("example-mod", "BepInExPlugin", "mod.zip") with { Name = name };

        var result = new ManifestValidator().Validate(Manifest(mod));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("safe folder name"));
    }

    [Fact]
    public void Validate_AcceptsDotsInsideModName()
    {
        var mod = Mod("example-mod", "BepInExPlugin", "mod.zip") with { Name = "Example..Mod" };

        var result = new ManifestValidator().Validate(Manifest(mod));

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void Validate_RejectsReservedBepInExTypeForNonFramework() // BUG-025
    {
        // "BepInEx" is reserved for the framework entry (id bepinex).
        var manifest = Manifest(Mod("evil", "BepInEx", "mod.zip"));
        var result = new ManifestValidator().Validate(manifest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("reserved"));
    }

    [Fact]
    public void Validate_AcceptsFrameworkWithBepInExType() // BUG-032
    {
        var manifest = Manifest(Mod("bepinex", "BepInEx", "bepinex.zip"));
        var result = new ManifestValidator().Validate(manifest);
        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }

    [Fact]
    public void Validate_RejectsOptionalFramework()
    {
        var framework = Mod("bepinex", "BepInEx", "bepinex.zip") with { Required = false };

        var result = new ManifestValidator().Validate(Manifest(framework));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("framework entry must be required"));
    }

    [Fact]
    public void Validate_RejectsRequiredModDependingOnOptionalMod()
    {
        var optional = Mod("library", "BepInExPlugin", "library.zip") with { Required = false };
        var required = Mod("required", "BepInExPlugin", "required.zip") with
        {
            Dependencies = new List<string> { optional.Id }
        };

        var result = new ManifestValidator().Validate(Manifest(required, optional));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Mark the dependency as required"));
    }

    [Fact]
    public void Validate_RejectsEmptyModsList() // BUG-028
    {
        var manifest = Manifest();
        var result = new ManifestValidator().Validate(manifest);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no mods"));
    }

    [Fact]
    public void Validate_RejectsNullModsListWithoutThrowing()
    {
        var manifest = new ModListManifest(
            1, "Malformed", "tcgcardshopsimulator", null!);

        var result = new ManifestValidator().Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("no mods"));
    }

    [Fact]
    public void EnforceBepInExFirst_NormalizesMissingDependencyLists()
    {
        var mod = Mod("example", "BepInExPlugin", "example.zip") with
        {
            Dependencies = null!,
            Conflicts = null!
        };

        var normalized = ModpackInstaller.EnforceBepInExFirst(Manifest(mod));

        Assert.Empty(normalized.Mods[0].Dependencies);
        Assert.Empty(normalized.Mods[0].Conflicts);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Validate_RejectsMalformedSha256(string sha256)
    {
        var mod = Mod("example", "BepInExPlugin", "example.zip") with { Sha256 = sha256 };

        var result = new ManifestValidator().Validate(Manifest(mod));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("64 hexadecimal"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3")]
    [InlineData("current")]
    public void Validate_RejectsInvalidCompatibleBuildId(string buildId)
    {
        var manifest = Manifest(Mod("example", "BepInExPlugin", "example.zip")) with
        {
            CompatibleGameBuildIds = new List<string> { buildId }
        };

        var result = new ManifestValidator().Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("digits only"));
    }

    [Fact]
    public void Validate_RejectsDuplicateCompatibleBuildIds()
    {
        var manifest = Manifest(Mod("example", "BepInExPlugin", "example.zip")) with
        {
            CompatibleGameBuildIds = new List<string> { "123", "123" }
        };

        var result = new ManifestValidator().Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("duplicates"));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("BepInEx\\config\\settings.cfg")]
    [InlineData("")]
    [InlineData("/")]
    public void Validate_RejectsUnsafeExcludedArchivePath(string path)
    {
        var mod = Mod("example", "BepInExPlugin", "example.zip") with
        {
            ExcludedArchivePaths = [path]
        };

        var result = new ManifestValidator().Validate(Manifest(mod));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("excluded archive path is unsafe"));
    }

    [Fact]
    public void Validate_AcceptsExactAndDirectoryArchiveExclusions()
    {
        var mod = Mod("example", "BepInExPlugin", "example.zip") with
        {
            ExcludedArchivePaths =
            [
                "BepInEx/config/settings.cfg",
                "BepInEx/plugins/Shared/"
            ]
        };

        var result = new ManifestValidator().Validate(Manifest(mod));

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
    }
}
