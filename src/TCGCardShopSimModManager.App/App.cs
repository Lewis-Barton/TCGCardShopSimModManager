using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

// `partial` lets the XAML compiler see this type; the theme + styles live in
// App.axaml. Initialize() loads that XAML at runtime (AvaloniaXamlLoader).
public partial class App : Application
{
    private static readonly AppearancePreferencesStore PreferencesStore = new();

    public static AppearancePreferences Preferences { get; private set; } = new();

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ActualThemeVariantChanged += (_, _) =>
        {
            if (Preferences.Theme == AppColorTheme.System)
                ApplyAppearance(Preferences, save: false);
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Preferences = PreferencesStore.Load();
        ApplyAppearance(Preferences, save: false);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }

    public static void ApplyAppearance(AppearancePreferences preferences, bool save = true)
    {
        Preferences = preferences;
        if (Current is not App app)
            return;

        app.RequestedThemeVariant = preferences.Theme switch
        {
            AppColorTheme.Light => ThemeVariant.Light,
            AppColorTheme.Dark or AppColorTheme.HighContrast => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        var dark = preferences.Theme == AppColorTheme.Dark ||
                   preferences.Theme == AppColorTheme.System && app.ActualThemeVariant == ThemeVariant.Dark;
        ApplyPalette(app, preferences.Theme == AppColorTheme.HighContrast, dark);

        var large = preferences.TextSize == AppTextSize.Large;
        app.Resources["BodyFontSize"] = large ? 18d : 14d;
        app.Resources["SubtitleFontSize"] = large ? 16d : 12d;
        app.Resources["HeaderFontSize"] = large ? 24d : 20d;
        app.Resources["PageTitleFontSize"] = large ? 30d : 24d;

        if (save)
            PreferencesStore.Save(preferences);
    }

    private static void ApplyPalette(App app, bool highContrast, bool dark)
    {
        var values = highContrast
            ? new[] { "#000000", "#101010", "#FFFFFF", "#FFD800", "#FFF176", "#000000", "#FFFFFF", "#D6D6D6", "#332B00", "#FF5252", "#332B00", "#000000", "#FFFFFF", "#000000", "#FFFFFF" }
            : dark
                ? new[] { "#151A20", "#20262E", "#47515E", "#2DD4BF", "#5EEAD4", "#10201E", "#F3F4F6", "#AEB7C2", "#123B39", "#F87171", "#303844", "#39424E", "#F3F4F6", "#2B333D", "#AEB7C2" }
                : new[] { "#EEF1F5", "#FFFFFF", "#D2D8E0", "#0F766E", "#115E59", "#FFFFFF", "#1F2933", "#6B7280", "#DDF3F0", "#B42318", "#E8ECEF", "#E5E9EF", "#1F2933", "#F1F3F5", "#4B5563" };
        var keys = new[]
        {
            "AppBackgroundBrush", "PanelBrush", "BorderBrushKey", "AccentBrush",
            "AccentHoverBrush", "AccentTextBrush", "TextBrush", "MutedBrush", "SoftAccentBrush", "DangerBrush",
            "NavHoverBrush", "SecondaryBrush", "SecondaryTextBrush", "CheckBoxBrush",
            "CheckBoxBorderBrush"
        };
        for (var index = 0; index < keys.Length; index++)
            app.Resources[keys[index]] = new SolidColorBrush(Color.Parse(values[index]));
    }
}
