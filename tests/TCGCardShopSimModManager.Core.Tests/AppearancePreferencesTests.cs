using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.Core.Tests;

public sealed class AppearancePreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "appearance-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Store_DefaultsAndRoundTripsPreferences()
    {
        Directory.CreateDirectory(_root);
        var store = new AppearancePreferencesStore(Path.Combine(_root, "appearance.json"));

        Assert.Equal(new AppearancePreferences(), store.Load());

        var preferences = new AppearancePreferences(
            AppColorTheme.HighContrast, AppTextSize.Large, AppCardSize.Large);
        store.Save(preferences);

        Assert.Equal(preferences, store.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
