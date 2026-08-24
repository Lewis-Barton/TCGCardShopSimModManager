using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCGCardShopSimModManager.Core;

public enum AppColorTheme
{
    System,
    Light,
    Dark,
    HighContrast
}

public enum AppTextSize
{
    Normal,
    Large
}

public enum AppCardSize
{
    Standard,
    Large
}

public sealed record AppearancePreferences(
    AppColorTheme Theme = AppColorTheme.System,
    AppTextSize TextSize = AppTextSize.Normal,
    AppCardSize CardSize = AppCardSize.Standard);

public sealed class AppearancePreferencesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AtomicJsonFile<AppearancePreferences> _file;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TCGCardShopSimModManager",
        "appearance.json");

    public AppearancePreferencesStore(string? path = null)
    {
        _file = new AtomicJsonFile<AppearancePreferences>(
            path ?? DefaultPath,
            Options,
            () => new AppearancePreferences(),
            recoverCorrupt: true);
    }

    public AppearancePreferences Load() => _file.Read();

    public void Save(AppearancePreferences preferences) => _file.Write(preferences);
}
