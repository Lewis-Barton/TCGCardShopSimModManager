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
    private List<DiscoveredMod> _visibleDiscovered = new();

    private List<ModpackSummary> _packs = new();
    private List<InstalledModpack> _installedPacks = new();
    private readonly HttpClient _http = new();
    private readonly ModpackIndexReader _packReader;
    private readonly object _logoCacheLock = new();
    private readonly Dictionary<string, Task<Bitmap?>> _logoCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _logoLoadSlots = new(4, 4);
    private CancellationTokenSource? _modDiscoveryCancellation;
    private string? _discoveredGameFolder;
    private bool _modActionRunning;
    private Task? _packLoadTask;
    private bool _usingCachedPackIndex;
    private string? _installedGameBuildId;
    private string? _latestReleaseUrl;
    private bool _gameLaunchRunning;
    private bool _loadingAppearance;
    private bool _closed;

    public MainWindow()
    {
        _packReader = new ModpackIndexReader(_http);
        InitializeComponent();
        _installedModStateFilter.ItemsSource = new[]
        {
            "All states",
            "Enabled",
            "Disabled",
            "Modified",
            "Unmanaged"
        };
        _installedModStateFilter.SelectedIndex = 0;
        InitializeAppearanceSettings();
        Closed += (_, _) => DisposeWindowResources();

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
    private async void OnOpenLatestReleaseClick(object? sender, RoutedEventArgs e) => await RunHandler(OpenLatestReleaseAsync);
    private async void OnCheckPackFilesClick(object? sender, RoutedEventArgs e) => await RunHandler(OnCheckPackFilesAsync);
    private async void OnExportBundleClick(object? sender, RoutedEventArgs e) => await RunHandler(OnExportBundleAsync);
    private async void OnRefreshDownloadCacheClick(object? sender, RoutedEventArgs e) => await RunHandler(RefreshDownloadCacheAsync);
    private async void OnClearDownloadCacheClick(object? sender, RoutedEventArgs e) => await RunHandler(OnClearDownloadCacheAsync);
    private async void OnRefreshSaveProfileStorageClick(object? sender, RoutedEventArgs e) => await RunHandler(RefreshSaveProfileStorageAsync);
    private async void OnManageSaveProfilesClick(object? sender, RoutedEventArgs e) => await RunHandler(OnManageSaveProfilesAsync);
    private async void OnClearSaveProfileStorageClick(object? sender, RoutedEventArgs e) => await RunHandler(OnClearSaveProfileStorageAsync);
    private async void OnPickGameFolder(object? sender, RoutedEventArgs e) => await RunHandler(() => PickFolderAsync(_gameBox));
    private async void OnRefreshPacksClick(object? sender, RoutedEventArgs e) => await RunHandler(LoadPacksAsync);
    private async void OnLaunchGameClick(object? sender, RoutedEventArgs e)
    {
        if (_gameLaunchRunning)
            return;

        _gameLaunchRunning = true;
        _launchGame.IsEnabled = false;
        _launchGame.Content = "Launching...";
        _launchGameStatus.Text = "Sending the launch request to Steam...";
        try
        {
            await RunHandler(OnLaunchGameAsync);
        }
        finally
        {
            _gameLaunchRunning = false;
            if (!_closed)
            {
                _launchGame.Content = "Launch game";
                _launchGame.IsEnabled = true;
            }
        }
    }
    private void OnBrowseNavClick(object? sender, RoutedEventArgs e) => ShowPage(_browsePage, _browseNav);
    private async void OnManageNavClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(_managePage, _manageNav);
        if (!string.IsNullOrWhiteSpace(_gameBox.Text) &&
            !string.Equals(_discoveredGameFolder, _gameBox.Text, StringComparison.OrdinalIgnoreCase))
            await RunHandler(OnListModsAsync);
    }
    private async void OnSettingsNavClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(_settingsPage, _settingsNav);
        RefreshNexusStatus();
        await RunHandler(async () =>
        {
            await Task.WhenAll(RefreshDownloadCacheAsync(), RefreshSaveProfileStorageAsync());
        });
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
    private void OnInstalledModFilterChanged(object? sender, TextChangedEventArgs e) => ApplyInstalledModFilters();
    private void OnInstalledModStateFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyInstalledModFilters();
    private void OnModSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateModActions();
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

        // Catalog loading is independent of installed-mod discovery. Start it
        // immediately so hashing a large game folder cannot hold Browse empty.
        var catalogTask = LoadPacksAsync();

        var path = await Task.Run(() =>
            new SteamLocator().FindGameInstallPath(SteamLocator.GameAppId));

        if (path is null)
            Log("Not found. Pick the game folder manually with Browse, then List mods.");
        else
        {
            _gameBox.Text = path;
            Log($"Detected: {path}");
        }

        await catalogTask;
        if (path is not null)
            await RefreshPackInstallationStateAsync();
    }

    private async Task OnListModsAsync()
    {
        var gameFolder = _gameBox.Text;
        if (string.IsNullOrWhiteSpace(gameFolder))
        {
            Log($"Enter a game folder first.");
            return;
        }

        _modDiscoveryCancellation?.Cancel();
        _modDiscoveryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _modDiscoveryCancellation = cancellation;
        _refreshMods.IsEnabled = false;
        SetModActionsEnabled(false, false, false);
        _modSelectionStatus.Text = "Scanning installed mods...";
        _refreshMods.Content = "Scanning...";
        _progress.IsVisible = true;

        try
        {
            var discovered = await Task.Run(
                () => ModDiscovery.Discover(gameFolder, cancellationToken: cancellation.Token),
                cancellation.Token);
            if (!ReferenceEquals(_modDiscoveryCancellation, cancellation))
                return;
            _discovered = discovered;
            _discoveredGameFolder = gameFolder;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_modDiscoveryCancellation, cancellation))
            {
                _modDiscoveryCancellation = null;
                cancellation.Dispose();
                _refreshMods.IsEnabled = !_modActionRunning;
                _refreshMods.Content = "Refresh list";
                _progress.IsVisible = false;
                UpdateModActions();
            }
        }

        ApplyInstalledModFilters();

        Log($"Mods found on disk ({_discovered.Count}):");
        foreach (var mod in _discovered)
            Log($"  {mod.ModName,-35} {mod.State} ({mod.FileCount} file(s))");
    }

    // --- modpack gallery ----------------------------------------------------

    private Task LoadPacksAsync()
    {
        if (_packLoadTask is { IsCompleted: false } activeLoad)
            return activeLoad;

        _packLoadTask = LoadPacksCoreAsync();
        return _packLoadTask;
    }

    private void ApplyInstalledModFilters()
    {
        var selected = SelectedMod();
        var search = _installedModSearch.Text?.Trim();
        var state = _installedModStateFilter.SelectedIndex switch
        {
            1 => ModInventoryState.Installed,
            2 => ModInventoryState.Disabled,
            3 => ModInventoryState.Modified,
            4 => ModInventoryState.Unknown,
            _ => (ModInventoryState?)null
        };

        _visibleDiscovered = _discovered
            .Where(mod => string.IsNullOrWhiteSpace(search) ||
                mod.ModName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (mod.ActiveRoot?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(mod => state is null || mod.State == state)
            .ToList();

        _modsList.ItemsSource = _visibleDiscovered
            .Select(mod => $"  {mod.ModName}   [{mod.State}]  ({mod.FileCount})")
            .ToList();
        _modsList.SelectedIndex = selected is null ? -1 : _visibleDiscovered.IndexOf(selected);
        _installedModCount.Text = _visibleDiscovered.Count == _discovered.Count
            ? $"{_discovered.Count} mod{(_discovered.Count == 1 ? string.Empty : "s")} found"
            : $"Showing {_visibleDiscovered.Count} of {_discovered.Count} mods";
        UpdateModActions();
    }

    private async Task LoadPacksCoreAsync()
    {
        _refreshPacks.IsEnabled = false;
        _refreshPacks.Content = "Refreshing...";
        _packStatus.Text = "Loading modpacks from GitHub...";
        _packProgress.IsVisible = true;
        try
        {
            if (_packs.Count == 0)
            {
                var immediate = await Task.Run(() =>
                    _packReader.ReadCachedIndex() ?? _packReader.ReadBundledIndex());
                if (immediate is { Packs.Count: > 0 })
                {
                    _packs = immediate.Packs;
                    _usingCachedPackIndex = true;
                    try
                    {
                        _installedPacks = ReadInstalledPacks();
                    }
                    catch
                    {
                        _installedPacks = new List<InstalledModpack>();
                    }
                    ApplyPackFilters();
                    _packStatus.Text =
                        $"{_packs.Count} modpack(s) available. Checking GitHub for updates...";
                }
            }

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
            _packStatus.Text = _packs.Count > 0
                ? $"{_packs.Count} modpack(s) available from the local catalog. " +
                  $"Could not refresh GitHub: {ex.Message}"
                : $"Could not load modpacks: {ex.Message}";
        }
        finally
        {
            _packProgress.IsVisible = false;
            _refreshPacks.IsEnabled = true;
            _refreshPacks.Content = "Refresh";
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
        var cardWidth = largeCard ? 340 : largeText ? 300 : 250;
        var cardHeight = largeText
            ? largeCard ? 380 : 340
            : largeCard ? 330 : 280;
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

        // Reuse one fetch/decode task per logo across filter and card rebuilds.
        _ = SetLogoAsync(img, _packReader.LogoUrl(pack));

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
            MaxLines = largeCard && !largeText ? 3 : 4
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
        await _logoLoadSlots.WaitAsync();
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            return new Bitmap(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
        finally
        {
            _logoLoadSlots.Release();
        }
    }

    private Task<Bitmap?> CachedLogoAsync(string url)
    {
        lock (_logoCacheLock)
        {
            if (_logoCache.TryGetValue(url, out var cached))
                return cached;
            var loading = LoadLogoAsync(url);
            _logoCache[url] = loading;
            return loading;
        }
    }

    private async Task SetLogoAsync(Image image, string url)
    {
        var bitmap = await CachedLogoAsync(url);
        if (bitmap is not null && !_closed)
            await Dispatcher.UIThread.InvokeAsync(() => image.Source = bitmap);
    }

    private void DisposeWindowResources()
    {
        _closed = true;
        _modDiscoveryCancellation?.Cancel();
        _modDiscoveryCancellation?.Dispose();
        _modDiscoveryCancellation = null;
        _http.Dispose();
        lock (_logoCacheLock)
        {
            foreach (var task in _logoCache.Values)
            {
                if (task.Status == TaskStatus.RanToCompletion)
                    task.Result?.Dispose();
                else
                    _ = task.ContinueWith(
                        completed => completed.Result?.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default);
            }
            _logoCache.Clear();
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

    private async Task OnLaunchGameAsync()
    {
        using var existingGame = FindRunningGameProcess();
        if (existingGame is not null)
        {
            _launchGame.Content = "Game running";
            _launchGameStatus.Text = "TCG Card Shop Simulator is already running.";
            Log("TCG Card Shop Simulator is already running.");
            await WaitForGameExitAsync(existingGame);
            return;
        }

        try
        {
            using var steamLaunch = Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://run/{SteamLocator.GameAppId}",
                UseShellExecute = true
            });
        }
        catch
        {
            _launchGameStatus.Text = "Steam could not start the game.";
            throw;
        }

        Log("Launching TCG Card Shop Simulator through Steam...");
        _launchGameStatus.Text = "Waiting for TCG Card Shop Simulator to start...";

        for (var attempt = 0; attempt < 60 && !_closed; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            var game = FindRunningGameProcess();
            if (game is null)
                continue;

            using (game)
            {
                _launchGame.Content = "Game running";
                _launchGameStatus.Text = "TCG Card Shop Simulator is running.";
                await WaitForGameExitAsync(game);
            }
            return;
        }

        if (!_closed)
            _launchGameStatus.Text = "Steam accepted the launch request. If the game did not open, try again.";
    }

    private async Task WaitForGameExitAsync(Process game)
    {
        try
        {
            await game.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The game can close between discovery and attaching the exit wait.
        }

        if (!_closed)
            _launchGameStatus.Text = "TCG Card Shop Simulator has closed.";
    }

    private static Process? FindRunningGameProcess()
    {
        var processName = Path.GetFileNameWithoutExtension(SteamLocator.GameExecutableName);
        var processes = Process.GetProcessesByName(processName);
        if (processes.Length == 0)
            return null;

        for (var index = 1; index < processes.Length; index++)
            processes[index].Dispose();
        return processes[0];
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

    private async Task RefreshPackInstallationStateAsync()
    {
        var gameFolder = _gameBox.Text;
        _installedGameBuildId = string.IsNullOrWhiteSpace(gameFolder)
            ? null
            : await Task.Run(() => new SteamLocator().FindGameBuildId(
                gameFolder, SteamLocator.GameAppId));
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

        BeginModAction($"Enabling {mod.ModName}...");
        try
        {
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
        finally
        {
            EndModAction();
        }
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

        BeginModAction($"Disabling {mod.ModName}...");
        try
        {
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
        finally
        {
            EndModAction();
        }
    }

    private DiscoveredMod? SelectedMod()
    {
        if (_modsList.SelectedIndex < 0 || _modsList.SelectedIndex >= _visibleDiscovered.Count)
            return null;
        return _visibleDiscovered[_modsList.SelectedIndex];
    }

    private void UpdateModActions()
    {
        if (_modActionRunning)
        {
            SetModActionsEnabled(false, false, false);
            return;
        }

        var mod = SelectedMod();
        if (mod is null)
        {
            SetModActionsEnabled(false, false, false);
            _modSelectionStatus.Text = _discovered.Count == 0
                ? "No installed mods were found."
                : _visibleDiscovered.Count == 0
                    ? "No mods match the current search and state filter."
                    : "Select a managed mod to enable, disable or uninstall it.";
            return;
        }

        switch (mod.State)
        {
            case ModInventoryState.Installed:
                SetModActionsEnabled(false, true, true);
                _modSelectionStatus.Text = $"{mod.ModName} is enabled and available to the game.";
                break;
            case ModInventoryState.Disabled:
                SetModActionsEnabled(true, false, true);
                _modSelectionStatus.Text = $"{mod.ModName} is disabled and stored outside the game.";
                break;
            case ModInventoryState.Modified:
                SetModActionsEnabled(false, false, true);
                _modSelectionStatus.Text =
                    $"{mod.ModName} has changed files. Enable and disable are unavailable; uninstall will preserve files that no longer match the journal.";
                break;
            default:
                SetModActionsEnabled(false, false, false);
                _modSelectionStatus.Text =
                    $"{mod.ModName} is unmanaged. The manager will not change files it does not own.";
                break;
        }
    }

    private void SetModActionsEnabled(bool enable, bool disable, bool uninstall)
    {
        _enableMod.IsEnabled = enable;
        _disableMod.IsEnabled = disable;
        _uninstallMod.IsEnabled = uninstall;
    }

    private void BeginModAction(string status)
    {
        _modActionRunning = true;
        _refreshMods.IsEnabled = false;
        SetModActionsEnabled(false, false, false);
        _modSelectionStatus.Text = status;
        _progress.IsVisible = true;
    }

    private void EndModAction()
    {
        _modActionRunning = false;
        _refreshMods.IsEnabled = true;
        _progress.IsVisible = false;
        UpdateModActions();
    }

    private async Task OnUpdateCheckAsync()
    {
        Log("--- Update check");
        _checkForUpdates.IsEnabled = false;
        _updateCheckStatus.IsVisible = true;
        _updateCheckStatus.Text = "Checking for updates...";
        _openLatestRelease.IsVisible = false;
        _latestReleaseUrl = null;
        var local = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        try
        {
            var result = await Task.Run(async () =>
            {
                using var checker = new UpdateChecker(
                    "Lewis-Barton/TCGCardShopSimModManager", local, _http);
                return await checker.CheckAsync(CancellationToken.None);
            });

            if (result.Error is not null)
            {
                _updateCheckStatus.Text = result.Error;
                Log(result.Error);
                return;
            }

            if (!result.HasRelease)
                _updateCheckStatus.Text = $"Version {local} is installed. No published release was found.";
            else if (result.IsUpToDate)
                _updateCheckStatus.Text = $"Version {local} is up to date. Latest release: {result.LatestVersion}.";
            else
            {
                _updateCheckStatus.Text = $"Version {result.LatestVersion} is available. You have {local}.";
                if (IsWebUrl(result.ReleaseUrl))
                {
                    _latestReleaseUrl = result.ReleaseUrl;
                    _openLatestRelease.IsVisible = true;
                }
            }
            Log(_updateCheckStatus.Text);
        }
        catch (Exception ex)
        {
            _updateCheckStatus.Text = $"Could not check for updates: {ex.Message}";
            Log(_updateCheckStatus.Text);
            Diagnostic.Write(ex.ToString(), "update-check");
        }
        finally
        {
            _checkForUpdates.IsEnabled = true;
        }
    }

    private Task OpenLatestReleaseAsync()
    {
        if (_latestReleaseUrl is not { } releaseUrl || !IsWebUrl(releaseUrl))
        {
            _openLatestRelease.IsVisible = false;
            _updateCheckStatus.Text = "The release page address is unavailable. Check again and retry.";
            return Task.CompletedTask;
        }

        try
        {
            Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _updateCheckStatus.Text = $"Could not open the release page: {ex.Message}";
            Diagnostic.Write(ex.ToString(), "update-check");
        }
        return Task.CompletedTask;
    }

    private static bool IsWebUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private async Task OnExportBundleAsync()
    {
        Log("--- Export support bundle");
        _exportSupportBundle.IsEnabled = false;
        _supportBundleStatus.IsVisible = true;
        _supportBundleStatus.Text = "Exporting support bundle...";
        try
        {
            var gameFolder = string.IsNullOrWhiteSpace(_gameBox.Text) ? null : _gameBox.Text;
            var bundlePath = await Task.Run(() => SupportBundle.Create(gameFolder, outputDirectory: null));
            _supportBundleStatus.Text = $"Support bundle saved to {bundlePath}";
            Log($"Support bundle written to: {bundlePath}");
        }
        catch (Exception ex)
        {
            _supportBundleStatus.Text = $"Could not export support bundle: {ex.Message}";
            Log(_supportBundleStatus.Text);
            Diagnostic.Write(ex.ToString(), "support-bundle");
        }
        finally
        {
            _exportSupportBundle.IsEnabled = true;
        }
    }

    private async Task RefreshDownloadCacheAsync()
    {
        var info = await Task.Run(() => new DownloadCacheManager().Inspect());
        _downloadCacheStatus.Text = info.FileCount == 0
            ? "No downloaded mod archives are cached."
            : info.PartialFileCount == 0
                ? $"{FormatBytes(info.SizeBytes)} in {info.VerifiedFileCount:N0} cached archive{(info.VerifiedFileCount == 1 ? string.Empty : "s")}."
                : $"{FormatBytes(info.SizeBytes)} in {info.VerifiedFileCount:N0} ready archive{(info.VerifiedFileCount == 1 ? string.Empty : "s")} and {info.PartialFileCount:N0} resumable partial download{(info.PartialFileCount == 1 ? string.Empty : "s")}.";
        _clearDownloadCache.IsEnabled = info.FileCount > 0;
    }

    private async Task OnClearDownloadCacheAsync()
    {
        var manager = new DownloadCacheManager();
        var current = await Task.Run(manager.Inspect);
        if (current.FileCount == 0)
        {
            await RefreshDownloadCacheAsync();
            return;
        }

        var confirmation = new DownloadCacheClearConfirmationWindow(FormatBytes(current.SizeBytes));
        if (!await confirmation.ShowDialog<bool>(this))
            return;

        _clearDownloadCache.IsEnabled = false;
        _downloadCacheStatus.Text = "Clearing cached downloads...";
        var result = await Task.Run(manager.Clear);
        _downloadCacheStatus.Text = result.Errors.Count == 0
            ? $"Cleared {FormatBytes(result.FreedBytes)} from downloaded mod storage."
            : $"Cleared {FormatBytes(result.FreedBytes)}, but {result.Errors.Count:N0} file{(result.Errors.Count == 1 ? string.Empty : "s")} could not be removed.";
        foreach (var error in result.Errors)
            Log($"Download cache: {error}");
        _clearDownloadCache.IsEnabled = result.Errors.Count > 0;
    }

    private async Task RefreshSaveProfileStorageAsync()
    {
        var info = await Task.Run(() => new ModpackSaveProfileManager().InspectStorage());
        _saveProfileStorageStatus.Text = info.ProfileCount == 0
            ? "No separate modpack saves are stored."
            : $"{FormatBytes(info.SizeBytes)} in {info.FileCount:N0} save file{(info.FileCount == 1 ? string.Empty : "s")} for {info.ProfileCount:N0} modpack{(info.ProfileCount == 1 ? string.Empty : "s")}.";
        _clearSaveProfileStorage.IsEnabled = info.FileCount > 0;
        _manageSaveProfiles.IsEnabled = info.ProfileCount > 0;
    }

    private async Task OnManageSaveProfilesAsync()
    {
        var manager = new ModpackSaveProfileManager();
        var profiles = await Task.Run(manager.ListStoredProfiles);
        if (profiles.Count == 0)
        {
            await RefreshSaveProfileStorageAsync();
            return;
        }

        var selectedPackId = await new SaveProfilesManageWindow(profiles)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(selectedPackId))
            return;

        _manageSaveProfiles.IsEnabled = false;
        _clearSaveProfileStorage.IsEnabled = false;
        _saveProfileStorageStatus.Text = $"Deleting stored saves for {selectedPackId}...";
        var result = await Task.Run(() => manager.DeleteStoredProfile(selectedPackId));
        var remaining = await Task.Run(manager.InspectStorage);
        _clearSaveProfileStorage.IsEnabled = remaining.FileCount > 0;
        _manageSaveProfiles.IsEnabled = remaining.ProfileCount > 0;
        if (result.Errors.Count == 0)
            _saveProfileStorageStatus.Text =
                $"Deleted {FormatBytes(result.FreedBytes)} of stored saves for {selectedPackId}. " +
                $"{remaining.ProfileCount:N0} stored profile{(remaining.ProfileCount == 1 ? string.Empty : "s")} remain.";
        else
        {
            _saveProfileStorageStatus.Text =
                $"Stored saves for {selectedPackId} could not be fully removed.";
            foreach (var error in result.Errors)
                Log($"Save profiles: {error}");
        }
    }

    private async Task OnClearSaveProfileStorageAsync()
    {
        var manager = new ModpackSaveProfileManager();
        var current = await Task.Run(manager.InspectStorage);
        if (current.FileCount == 0)
        {
            await RefreshSaveProfileStorageAsync();
            return;
        }

        var confirmation = new SaveProfilesClearConfirmationWindow(
            FormatBytes(current.SizeBytes), current.ProfileCount);
        if (!await confirmation.ShowDialog<bool>(this))
            return;

        _clearSaveProfileStorage.IsEnabled = false;
        _saveProfileStorageStatus.Text = "Clearing stored modpack saves...";
        var result = await Task.Run(manager.ClearStorage);
        _saveProfileStorageStatus.Text = result.Errors.Count == 0
            ? $"Cleared {FormatBytes(result.FreedBytes)} from modpack save storage."
            : $"Cleared {FormatBytes(result.FreedBytes)}, but {result.Errors.Count:N0} profile{(result.Errors.Count == 1 ? string.Empty : "s")} could not be fully removed.";
        foreach (var error in result.Errors)
            Log($"Save profiles: {error}");
        _clearSaveProfileStorage.IsEnabled = result.Errors.Count > 0;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
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

        var confirmation = new ModUninstallConfirmationWindow(mod.ModName, mod.State);
        if (!await confirmation.ShowDialog<bool>(this))
            return;

        BeginModAction($"Uninstalling {mod.ModName}...");
        try
        {
            Log($"--- Uninstall {mod.ModName}");
            var result = await Task.Run(() => new ModInstaller(gameFolder).Uninstall(mod.ModName));

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
        finally
        {
            EndModAction();
        }
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
            _discoveredGameFolder = null;
            // The game folder is now known — reload the gallery so update badges
            // can be shown for any already-installed packs, and populate Manage
            // without requiring a second click.
            await Task.WhenAll(LoadPacksAsync(), OnListModsAsync());
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

    private async Task OnCheckPackFilesAsync()
    {
        if (NexusTokenStore.TryLoad() is null && !ApiKeyStore.Exists)
        {
            _nexusStatus.Text = "Modpack file checks need Nexus access. Sign in or enter a personal API key first.";
            return;
        }

        if (_packs.Count == 0)
            await LoadPacksAsync();
        if (_packs.Count == 0)
            return;

        var window = new ModpackAuthorUpdateWindow(_packs, _packReader, _http);
        await window.ShowDialog(this);
    }
}
