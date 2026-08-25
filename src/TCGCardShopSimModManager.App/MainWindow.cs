using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

/// <summary>
/// The desktop shell over <see cref="DeploymentService"/> and the rest of the
/// engine. The layout lives in MainWindow.axaml; this file is the behaviour.
/// Compute happens on a background task; controls are only touched on the UI
/// thread (after the await resumes there).
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int MaxVisibleLogLines = 500;
    private readonly Queue<string> _visibleLogLines = new();
    private List<DiscoveredMod> _discovered = new();

    private List<ModpackSummary> _packs = new();
    private List<InstalledModpack> _installedPacks = new();
    private readonly HttpClient _http = new();
    private readonly ModpackIndexReader _packReader;
    private bool _usingCachedPackIndex;
    private string? _installedGameBuildId;
    private bool _loadingAppearance;

    public MainWindow()
    {
        _packReader = new ModpackIndexReader(_http);
        InitializeComponent();
        InitializeAppearanceSettings();
        Closed += (_, _) => _http.Dispose();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is not null)
        {
            Title = $"TCG Card Shop Sim Mod Manager {version}";
            _versionText.Text = $"Version {version.ToString(3)}";
        }

        // BUG-038: an exception during startup detection must be caught and
        // logged, not left as an unobserved async-void exception that can crash
        // the app at launch. Route it through RunHandler like every button.
        Opened += async (_, _) => await RunHandler(WelcomeDetectAsync);
    }

    // --- click handlers -----------------------------------------------------
    // XAML wires each Button.Click to one of these. They forward to the real
    // async work and swallow exceptions into the log (mirrors the old helper).

    private async void OnUninstallClick(object? sender, RoutedEventArgs e) => await RunHandler(OnUninstallAsync);
    private async void OnListModsClick(object? sender, RoutedEventArgs e) => await RunHandler(OnListModsAsync);
    private async void OnEnableClick(object? sender, RoutedEventArgs e) => await RunHandler(OnEnableAsync);
    private async void OnDisableClick(object? sender, RoutedEventArgs e) => await RunHandler(OnDisableAsync);
    private async void OnUpdateCheckClick(object? sender, RoutedEventArgs e) => await RunHandler(OnUpdateCheckAsync);
    private async void OnExportBundleClick(object? sender, RoutedEventArgs e) => await RunHandler(OnExportBundleAsync);
    private async void OnPickGameFolder(object? sender, RoutedEventArgs e) => await RunHandler(() => PickFolderAsync(_gameBox));
    private async void OnRefreshPacksClick(object? sender, RoutedEventArgs e) => await RunHandler(LoadPacksAsync);
    private async void OnLaunchGameClick(object? sender, RoutedEventArgs e) => await RunHandler(OnLaunchGameAsync);
    private void OnBrowseNavClick(object? sender, RoutedEventArgs e) => ShowPage(_browsePage, _browseNav);
    private void OnManageNavClick(object? sender, RoutedEventArgs e) => ShowPage(_managePage, _manageNav);
    private void OnSettingsNavClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(_settingsPage, _settingsNav);
        RefreshNexusStatus();
    }
    private async void OnNexusLoginClick(object? sender, RoutedEventArgs e) => await OnNexusLoginAsync();
    private async void OnNexusApiKeyClick(object? sender, RoutedEventArgs e) => await OnNexusApiKeyAsync();
    private void OnNexusLogoutClick(object? sender, RoutedEventArgs e)
    {
        NexusTokenStore.Delete();
        RefreshNexusStatus();
    }
    private void OnPackTextFilterChanged(object? sender, TextChangedEventArgs e) => ApplyPackFilters();
    private void OnPackCheckFilterChanged(object? sender, RoutedEventArgs e) => ApplyPackFilters();
    private void OnPackSizeFilterChanged(object? sender, RangeBaseValueChangedEventArgs e) => ApplyPackFilters();
    private void OnAppearanceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppearance || _themeSelector.SelectedIndex < 0 ||
            _textSizeSelector.SelectedIndex < 0 || _cardSizeSelector.SelectedIndex < 0)
            return;

        App.ApplyAppearance(new AppearancePreferences(
            (AppColorTheme)_themeSelector.SelectedIndex,
            (AppTextSize)_textSizeSelector.SelectedIndex,
            (AppCardSize)_cardSizeSelector.SelectedIndex));
        ApplyResponsiveLayout();
        ApplyPackFilters();
    }
    private void OnResetFiltersClick(object? sender, RoutedEventArgs e)
    {
        _packSearch.Text = string.Empty;
        _modFilter.Text = string.Empty;
        _tagFilter.Text = string.Empty;
        _includeNonFeatured.IsChecked = true;
        _includeAdult.IsChecked = false;
        _installedOnly.IsChecked = false;
        _excludeMod.IsChecked = false;
        _sizeFilter.Value = _sizeFilter.Maximum;
        ApplyPackFilters();
    }

    private async Task RunHandler(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            // BUG-037: surface the full exception (type + message) on screen and
            // record the detail to the diagnostic log, so a thrown failure is
            // diagnosable instead of being silently swallowed as one line.
            Log($"Error: {ex.GetType().Name}: {ex.Message}");
            Diagnostic.Write(ex.ToString(), "error");
        }
    }

    // --- actions -----------------------------------------------------------

    /// <summary>Runs once, when the window opens: fill the game folder from Steam.</summary>
    private async Task WelcomeDetectAsync()
    {
        Log("Looking for TCG Card Shop Simulator through Steam...");

        var path = await Task.Run(() =>
            new SteamLocator().FindGameInstallPath(SteamLocator.GameAppId));

        if (path is null)
            Log("Not found. Pick the game folder manually with Browse, then List mods.");
        else
        {
            _gameBox.Text = path;
            Log($"Detected: {path}");
            await OnListModsAsync();
        }

        // Best-effort: populate the Modpacks gallery too. If we're offline this
        // logs and carries on; the tab just shows "could not load".
        await LoadPacksAsync();
    }

    private async Task OnListModsAsync()
    {
        var gameFolder = _gameBox.Text;
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            Log($"Enter a game folder first.");
            return;
        }

        _discovered = await Task.Run(() => ModDiscovery.Discover(gameFolder));

        _modsList.ItemsSource = _discovered
            .Select(m => $"  {m.ModName}   [{m.State}]  ({m.FileCount})")
            .ToList();

        Log($"Mods found on disk ({_discovered.Count}):");
        foreach (var mod in _discovered)
            Log($"  {mod.ModName,-35} {mod.State} ({mod.FileCount} file(s))");
    }

    // --- modpack gallery ----------------------------------------------------

    private async Task LoadPacksAsync()
    {
        _packStatus.Text = "Loading modpacks from GitHub...";
        _packProgress.IsVisible = true;
        try
        {
            var gameFolder = _gameBox.Text;
            _installedGameBuildId = string.IsNullOrWhiteSpace(gameFolder)
                ? null
                : await Task.Run(() => new SteamLocator().FindGameBuildId(
                    gameFolder, SteamLocator.GameAppId));
            var index = await _packReader.FetchIndexAsync();
            _packs = index.Packs;
            _usingCachedPackIndex = _packReader.LastFetchUsedCache;

            // BUG-008: loading the installed-packs journal must not abort gallery
            // rendering. Isolate it so a corrupt/unreadable journal only suppresses
            // update badges (with a warning), never the whole gallery.
            try
            {
                _installedPacks = ReadInstalledPacks();
            }
            catch (Exception ex)
            {
                _installedPacks = new List<InstalledModpack>();
                _packStatus.Text = $"Could not read installed modpacks: {ex.Message}";
            }

            ApplyPackFilters();
        }
        catch (Exception ex)
        {
            _packStatus.Text = $"Could not load modpacks: {ex.Message}";
        }
        finally
        {
            _packProgress.IsVisible = false;
        }
    }

    private List<InstalledModpack> ReadInstalledPacks()
    {
        var gameFolder = _gameBox.Text;
        if (string.IsNullOrWhiteSpace(gameFolder) || !Directory.Exists(gameFolder))
            return new List<InstalledModpack>();

        var installed = new ModpackJournalStore(gameFolder).Load();

        // BUG-009: a pack-id rename must not orphan the stored entry. Map a legacy
        // PackId (matching a pack's FormerIds) to its canonical id, and persist the
        // normalization so the legacy id doesn't linger and the next Record can
        // cleanly replace it.
        if (_packs is not null)
        {
            var byFormer = _packs
                .Where(p => p.FormerIds is { Count: > 0 })
                .SelectMany(p => p.FormerIds!.Select(f => (former: f, canonical: p.Id)))
                .ToDictionary(x => x.former, x => x.canonical, StringComparer.OrdinalIgnoreCase);

            if (byFormer.Count > 0)
            {
                var changed = false;
                var rewritten = installed.Select(e =>
                {
                    if (byFormer.TryGetValue(e.PackId, out var canonical))
                    {
                        changed = true;
                        return e with { PackId = canonical };
                    }
                    return e;
                }).ToList();

                if (changed)
                {
                    new ModpackJournalStore(gameFolder).Save(rewritten);
                    installed = rewritten;
                }
            }
        }

        return installed;
    }

    private bool IsUpdateAvailable(ModpackSummary pack)
    {
        // BUG-009: match by canonical id or any legacy FormerId, so a pack-id
        // rename doesn't break update detection for an already-installed pack.
        var installed = _installedPacks.FirstOrDefault(p => pack.IsId(p.PackId));
        return installed is not null && ModpackVersion.IsNewer(installed.PackVersion, pack.Version);
    }

    private void ApplyPackFilters()
    {
        if (_packsPanel is null)
            return;

        var search = _packSearch.Text?.Trim();
        var mod = _modFilter.Text?.Trim();
        var tag = _tagFilter.Text?.Trim();
        var maxBytes = _sizeFilter.Value >= _sizeFilter.Maximum
            ? (long?)null
            : (long)(_sizeFilter.Value * 1024 * 1024 * 1024);

        _sizeFilterLabel.Text = maxBytes is null ? "Any size" : $"Up to {_sizeFilter.Value:0} GB";
        var visible = _packs.Where(pack =>
            (string.IsNullOrEmpty(search) || pack.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             pack.ShortDescription.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (_includeNonFeatured.IsChecked == true || pack.Featured) &&
            (_includeAdult.IsChecked == true || !pack.Nsfw) &&
            (maxBytes is null || pack.DownloadSize is null || pack.DownloadSize <= maxBytes) &&
            (string.IsNullOrEmpty(mod) ||
             (pack.ModIds?.Any(value => value.Contains(mod, StringComparison.OrdinalIgnoreCase)) == true) !=
             (_excludeMod.IsChecked == true)) &&
            (string.IsNullOrEmpty(tag) || pack.Tags?.Any(value => value.Contains(tag, StringComparison.OrdinalIgnoreCase)) == true) &&
            (_installedOnly.IsChecked != true || _installedPacks.Any(installed => pack.IsId(installed.PackId))))
            .ToList();

        _packsPanel.Children.Clear();
        foreach (var pack in visible)
            _packsPanel.Children.Add(BuildPackCard(pack, IsUpdateAvailable(pack)));
        var countText = visible.Count == _packs.Count
            ? $"{visible.Count} modpack(s) available."
            : $"Showing {visible.Count} of {_packs.Count} modpack(s).";
        _packStatus.Text = _usingCachedPackIndex
            ? $"{countText} Showing the last saved catalog because GitHub could not be reached."
            : countText;
        _installedPackSummary.Text = _installedPacks.Count switch
        {
            0 => "No modpack is installed in the selected game folder.",
            1 => $"Installed: {_installedPacks[0].Name} · version {_installedPacks[0].PackVersion}",
            _ => $"Installed: {string.Join(", ", _installedPacks.Select(pack => $"{pack.Name} {pack.PackVersion}"))}"
        };
    }

    private Border BuildPackCard(ModpackSummary pack, bool updateAvailable)
    {
        var largeText = App.Preferences.TextSize == AppTextSize.Large;
        var largeCard = App.Preferences.CardSize == AppCardSize.Large;
        var expanded = largeText || largeCard;
        var cardWidth = largeCard ? 340 : largeText ? 300 : 250;
        var cardHeight = largeText
            ? largeCard ? 380 : 340
            : largeCard ? 330 : 234;
        var previewHeight = largeCard ? 180 : largeText ? 145 : 125;
        var bannerHeight = largeText ? 32 : 24;
        var card = new Border
        {
            Classes = { "card", "packCard" },
            Width = cardWidth,
            Height = cardHeight,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"{bannerHeight},{previewHeight},*")
        };
        var img = new Image
        {
            Height = previewHeight,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        Grid.SetRow(img, 1);

        // Fetch the logo off the UI thread, then drop it in once it arrives.
        _ = LoadLogoAsync(_packReader.LogoUrl(pack)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is Bitmap bmp)
                Dispatcher.UIThread.Post(() => img.Source = bmp);
        });

        grid.Children.Add(img);
        var installed = _installedPacks.FirstOrDefault(entry => pack.IsId(entry.PackId));
        if (installed is not null)
        {
            var banner = new Border
            {
                Background = BannerBackground(updateAvailable),
                Padding = new Thickness(8, 4),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = updateAvailable
                        ? $"↑ UPDATE AVAILABLE · {PackVersionTransition(installed.PackVersion, pack.Version)}"
                        : $"✓ INSTALLED · {installed.PackVersion}",
                    Foreground = App.Preferences.Theme == AppColorTheme.HighContrast
                        ? Brushes.Black
                        : Brushes.White,
                    FontSize = largeText ? 15 : 11,
                    FontWeight = FontWeight.Bold
                }
            };
            Grid.SetRow(banner, 0);
            grid.Children.Add(banner);
        }
        var details = new StackPanel { Spacing = 2, Margin = new Thickness(10, 6) };
        Grid.SetRow(details, 2);
        details.Children.Add(new TextBlock
        {
            Text = pack.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = largeText ? 19 : 14
        });
        details.Children.Add(new TextBlock
        {
            Text = pack.ShortDescription,
            TextWrapping = TextWrapping.Wrap,
            FontSize = largeText ? 16 : 11,
            MaxLines = largeText ? 4 : expanded ? 3 : 2
        });

        var compatibility = GameCompatibility.Evaluate(
            pack.CompatibleGameBuildIds, _installedGameBuildId);
        if (compatibility.MayBeUnsupported)
            details.Children.Add(new TextBlock
            {
                Text = "May not be supported",
                Foreground = new SolidColorBrush(Colors.Orange),
                FontSize = largeText ? 16 : 11,
                FontWeight = FontWeight.Bold
            });

        grid.Children.Add(details);
        card.Child = grid;
        card.PointerPressed += async (_, _) => await RunHandler(() => OpenPack(pack));
        return card;
    }

    private async Task OpenPack(ModpackSummary pack)
    {
        var gameFolder = _gameBox.Text;
        _installedGameBuildId = string.IsNullOrWhiteSpace(gameFolder)
            ? null
            : await Task.Run(() => new SteamLocator().FindGameBuildId(
                gameFolder, SteamLocator.GameAppId));
        var installed = _installedPacks.FirstOrDefault(entry => pack.IsId(entry.PackId));
        var active = _installedPacks.FirstOrDefault();
        var detail = new PackDetailWindow(
            pack, gameFolder, _http, _packReader, installed, _installedGameBuildId, active);
        await detail.ShowDialog(this);
        await LoadPacksAsync();
    }

    private async Task<Bitmap?> LoadLogoAsync(string url)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private void ShowPage(Control page, Button nav)
    {
        _browsePage.IsVisible = ReferenceEquals(page, _browsePage);
        _managePage.IsVisible = ReferenceEquals(page, _managePage);
        _settingsPage.IsVisible = ReferenceEquals(page, _settingsPage);
        foreach (var button in new[] { _browseNav, _manageNav, _settingsNav })
            button.Classes.Set("active", ReferenceEquals(button, nav));
    }

    private static string PackVersionTransition(string installedVersion, string availableVersion) =>
        $"{installedVersion} → {availableVersion}";

    private static IBrush BannerBackground(bool updateAvailable)
    {
        if (App.Preferences.Theme == AppColorTheme.HighContrast)
            return new SolidColorBrush(Color.Parse("#FFD800"));
        return new SolidColorBrush(Color.Parse(updateAvailable ? "#15803D" : "#0F766E"));
    }

    private void InitializeAppearanceSettings()
    {
        _loadingAppearance = true;
        try
        {
            _themeSelector.ItemsSource = new[] { "Use system setting", "Light", "Dark", "High contrast" };
            _textSizeSelector.ItemsSource = new[] { "Normal", "Large" };
            _cardSizeSelector.ItemsSource = new[] { "Standard", "Large" };
            _themeSelector.SelectedIndex = (int)App.Preferences.Theme;
            _textSizeSelector.SelectedIndex = (int)App.Preferences.TextSize;
            _cardSizeSelector.SelectedIndex = (int)App.Preferences.CardSize;
        }
        finally
        {
            _loadingAppearance = false;
        }
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        var largeText = App.Preferences.TextSize == AppTextSize.Large;
        _shellGrid.ColumnDefinitions[0].Width = new GridLength(largeText ? 250 : 190);
        _browseLayout.ColumnDefinitions[0].Width = new GridLength(largeText ? 300 : 240);
    }

    private Task OnLaunchGameAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = $"steam://run/{SteamLocator.GameAppId}",
            UseShellExecute = true
        });
        Log("Launching TCG Card Shop Simulator through Steam...");
        return Task.CompletedTask;
    }

    private void RefreshNexusStatus()
    {
        var token = NexusTokenStore.TryLoad();
        var user = token is null ? null : NexusJwt.DecodeAccessToken(token.AccessToken);
        if (user is null)
        {
            _nexusStatus.Text = ApiKeyStore.Exists
                ? "A personal Nexus API key is saved for downloads."
                : NexusOAuth.ClientId == "public_test"
                    ? "Not connected. A production Nexus OAuth client ID has not been configured."
                    : "Not connected to Nexus Mods.";
            _nexusLogin.IsEnabled = true;
            _nexusLogout.IsEnabled = false;
            _nexusApiKey.Content = ApiKeyStore.Exists ? "Change API key" : "Enter API key";
            return;
        }

        _nexusStatus.Text = ApiKeyStore.Exists
            ? $"Signed in as {user.Name}. A personal API key is also saved as a fallback."
            : $"Signed in as {user.Name}.";
        _nexusLogin.IsEnabled = false;
        _nexusLogout.IsEnabled = true;
        _nexusApiKey.Content = ApiKeyStore.Exists ? "Change API key" : "Enter API key";
    }

    private async Task OnNexusApiKeyAsync()
    {
        _nexusApiKey.IsEnabled = false;
        try
        {
            var dialog = new NexusCredentialWindow(_http, ApiKeyStore.Exists);
            await dialog.ShowDialog(this);
            RefreshNexusStatus();
        }
        finally
        {
            _nexusApiKey.IsEnabled = true;
        }
    }

    private async Task OnNexusLoginAsync()
    {
        _nexusLogin.IsEnabled = false;
        _nexusStatus.Text = "Opening Nexus Mods in your browser...";
        try
        {
            var user = await NexusOAuth.LoginAsync(_http);
            _nexusStatus.Text = $"Signed in as {user.Name}.";
            _nexusLogout.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _nexusStatus.Text = $"Nexus sign-in failed: {ex.Message}";
            _nexusLogin.IsEnabled = true;
            Diagnostic.Write(ex.ToString(), "nexus-login");
        }
    }

    private async Task OnEnableAsync()
    {
        var gameFolder = _gameBox.Text;
        var mod = SelectedMod();
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            Log($"Enter a game folder first.");
            return;
        }
        if (mod is null)
        {
            Log($"Select a mod in the list first.");
            return;
        }

        Log($"--- Enable {mod.ModName}");
        var result = await Task.Run(() => new ModInstaller(gameFolder).Enable(mod.ModName));

        if (!result.Success)
        {
            Log(result.Error ?? "Enable failed.");
            return;
        }

        Log($"Enabled {mod.ModName}.");
        foreach (var warning in result.Warnings)
            Log($"  Warning: {warning}");

        await OnListModsAsync();
    }

    private async Task OnDisableAsync()
    {
        var gameFolder = _gameBox.Text;
        var mod = SelectedMod();
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            Log($"Enter a game folder first.");
            return;
        }
        if (mod is null)
        {
            Log($"Select a mod in the list first.");
            return;
        }

        Log($"--- Disable {mod.ModName}");
        var result = await Task.Run(() => new ModInstaller(gameFolder).Disable(mod.ModName));

        if (!result.Success)
        {
            Log(result.Error ?? "Disable failed.");
            return;
        }

        Log($"Disabled {mod.ModName} (files moved out of the game so BepInEx won't load them).");
        foreach (var warning in result.Warnings)
            Log($"  Warning: {warning}");

        await OnListModsAsync();
    }

    private DiscoveredMod? SelectedMod()
    {
        if (_modsList.SelectedIndex < 0 || _modsList.SelectedIndex >= _discovered.Count)
            return null;
        return _discovered[_modsList.SelectedIndex];
    }

    private async Task OnUpdateCheckAsync()
    {
        Log("--- Update check");
        var local = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

        var result = await Task.Run(async () =>
        {
            using var checker = new UpdateChecker(
                "Lewis-Barton/TCGCardShopSimModManager", local, _http);
            return await checker.CheckAsync(CancellationToken.None);
        });

        if (result.Error is not null)
        {
            Log(result.Error);
            return;
        }

        if (!result.HasRelease)
            Log($"Local version: {local}. No GitHub releases published yet.");
        else
            Log(result.IsUpToDate
                ? $"Local {local} — up to date (latest {result.LatestVersion})."
                : $"Update available: {result.LatestVersion} ({result.ReleaseUrl})");
    }

    private async Task OnExportBundleAsync()
    {
        Log("--- Export support bundle");
        var bundlePath = await Task.Run(() => SupportBundle.Create(gameFolder: null, outputDirectory: null));
        Log($"Support bundle written to: {bundlePath}");
    }

    private async Task OnUninstallAsync()
    {
        var gameFolder = _gameBox.Text;
        var mod = SelectedMod();
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            Log($"Enter a game folder first.");
            return;
        }
        if (mod is null)
        {
            Log($"Select a mod in the list first.");
            return;
        }

        Log($"--- Uninstall {mod.ModName}");
        var result = await RunUnderProgress(() => Task.Run(() => new ModInstaller(gameFolder).Uninstall(mod.ModName)));

        if (!result.Success)
        {
            Log(result.Error ?? "Uninstall failed.");
            return;
        }

        Log($"Uninstalled {mod.ModName}.");
        foreach (var warning in result.Warnings)
            Log($"  Warning: {warning}");

        await OnListModsAsync();
    }

    private async Task<T> RunUnderProgress<T>(Func<Task<T>> work)
    {
        _progress.IsVisible = true;
        try
        {
            return await work();
        }
        finally
        {
            _progress.IsVisible = false;
        }
    }

    // --- dialogs & helpers -------------------------------------------------

    private async Task PickFolderAsync(TextBox target)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
            return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose folder", AllowMultiple = false });
        if (folders.Count > 0)
        {
            target.Text = folders[0].Path.LocalPath;
            // The game folder is now known — reload the gallery so update badges
            // can be shown for any already-installed packs.
            await LoadPacksAsync();
        }
    }

    private void Log(string line)
    {
        if (_visibleLogLines.Count == MaxVisibleLogLines)
            _visibleLogLines.Dequeue();
        _visibleLogLines.Enqueue(line);

        _log.Text = string.Join('\n', _visibleLogLines) + "\n";
        _log.CaretIndex = _log.Text.Length;
    }
}
