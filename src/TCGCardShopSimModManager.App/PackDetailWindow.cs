using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

/// <summary>
/// Modal opened when a card in Browse Lists is clicked. Shows the pack's logo,
/// description and mod list, and runs the existing one-click install pipeline.
/// Kept as code-built UI (no extra .axaml) to stay in step with the rest of the
/// app's code-behind style.
/// </summary>
public sealed class PackDetailWindow : Window
{
    private readonly ModpackSummary _pack;
    private readonly string? _gameFolder;
    private readonly ModpackIndexReader _reader;
    private readonly HttpClient _http;
    private readonly InstalledModpack? _installedPack;
    private readonly InstalledModpack? _activePack;
    private readonly string? _installedGameBuildId;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, IsVisible = false };
    private readonly TextBlock _progressStatus = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _downloadStats = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _optionalSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _nexusAccess = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _nexusLogin = new() { Content = "Sign in to Nexus" };
    private readonly Button _nexusApiKey = new() { Content = "Enter API key", Classes = { "secondary" } };
    private readonly StackPanel _nexusActions = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        IsVisible = false
    };
    private readonly Button _install = new() { Content = "Install modpack", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _cancelInstall = new()
    {
        Content = "Cancel install",
        Classes = { "secondary" },
        HorizontalAlignment = HorizontalAlignment.Stretch,
        IsVisible = false
    };
    private readonly Button _uninstall = new()
    {
        Content = "Uninstall modpack",
        Classes = { "secondary" },
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _compatibility = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _acknowledgeCompatibility = new()
    {
        Content = "Install even though this game build may not be supported",
        IsVisible = false
    };
    private readonly Dictionary<string, CheckBox> _modChoices = new(StringComparer.OrdinalIgnoreCase);
    private bool _updatingChoices;
    private ModListManifest? _manifest;
    private readonly Stopwatch _speedTimer = new();
    private int _progressModIndex;
    private long _lastProgressBytes;
    private TimeSpan _lastProgressTime;
    private CancellationTokenSource? _installCancellation;

    public PackDetailWindow(
        ModpackSummary pack,
        string? gameFolder,
        HttpClient http,
        ModpackIndexReader reader,
        InstalledModpack? installedPack = null,
        string? installedGameBuildId = null,
        InstalledModpack? activePack = null)
    {
        _pack = pack;
        _gameFolder = gameFolder;
        _http = http;
        _reader = reader;
        _installedPack = installedPack;
        _activePack = activePack;
        _installedGameBuildId = installedGameBuildId;
        Title = pack.Name;
        Width = 560;
        Height = 500;
        MinWidth = 460;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var img = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
        _ = LoadLogoAsync(_reader.LogoUrl(pack)).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result is Bitmap bmp)
                Dispatcher.UIThread.Post(() => img.Source = bmp);
        });

        var requiredMods = new StackPanel { Spacing = 4 };
        var optionalMods = new StackPanel { Spacing = 4 };
        _install.Click += async (_, _) => await InstallAsync();
        _cancelInstall.Click += (_, _) => CancelInstall();
        _uninstall.Click += async (_, _) => await UninstallAsync();
        _uninstall.IsVisible = installedPack is not null;
        _acknowledgeCompatibility.IsCheckedChanged += (_, _) => RefreshInstallAvailability();
        _nexusLogin.Click += async (_, _) => await SignInToNexusAsync();
        _nexusApiKey.Click += async (_, _) => await EnterNexusApiKeyAsync();
        _nexusActions.Children.Add(_nexusLogin);
        _nexusActions.Children.Add(_nexusApiKey);

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 8,
                Children =
                {
                    img,
                    new TextBlock { Text = pack.Name, FontWeight = FontWeight.Bold, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = pack.ShortDescription, TextWrapping = TextWrapping.Wrap },
                    _compatibility,
                    _acknowledgeCompatibility,
                    new TextBlock { Text = "Required mods", FontWeight = FontWeight.Bold },
                    new TextBlock
                    {
                        Text = "These are always installed with the pack.",
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    new ScrollViewer { MaxHeight = 150, Content = requiredMods },
                    new TextBlock { Text = "Optional mods", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) },
                    new TextBlock
                    {
                        Text = "Choose any extras you want before installing.",
                        FontSize = 12,
                        Opacity = 0.7
                    },
                    new ScrollViewer { MaxHeight = 150, Content = optionalMods },
                    _optionalSummary,
                    _nexusAccess,
                    _nexusActions,
                    _progress,
                    _progressStatus,
                    _downloadStats,
                    _install,
                    _cancelInstall,
                    _uninstall,
                    _status
                }
            }
        };

        _ = LoadManifestAsync(requiredMods, optionalMods);
    }

    private async Task LoadManifestAsync(StackPanel requiredMods, StackPanel optionalMods)
    {
        try
        {
            _manifest = await _reader.FetchManifestAsync(_pack);
            var validation = new ManifestValidator().Validate(_manifest);
            if (!validation.IsValid)
            {
                _status.Text = "This modpack is invalid: " + string.Join(" ", validation.Errors);
                return;
            }
            ShowCompatibility(_manifest.CompatibleGameBuildIds);
            foreach (var mod in _manifest.Mods)
            {
                var version = string.IsNullOrWhiteSpace(mod.Version) ? "" : $" {mod.Version}";
                var choice = new CheckBox
                {
                    Content = $"{mod.Name}{version}",
                    IsChecked = mod.Required || IsPreviouslySelected(mod),
                    IsEnabled = !mod.Required
                };
                choice.IsCheckedChanged += (_, _) => OnModChoiceChanged(mod);
                _modChoices[mod.Id] = choice;
                (mod.Required ? requiredMods : optionalMods).Children.Add(choice);
            }
            if (optionalMods.Children.Count == 0)
                optionalMods.Children.Add(new TextBlock { Text = "This pack has no optional mods.", Opacity = 0.7 });
            RefreshOptionalSummary();
            RefreshNexusAccess();
            RefreshInstallAvailability();
            var switching = _activePack is not null && !_pack.IsId(_activePack.PackId);
            _install.Content = switching
                ? $"Switch from {_activePack!.Name}"
                : _installedPack is not null
                    ? ModpackVersion.IsNewer(_installedPack.PackVersion, _pack.Version)
                    ? "Install update"
                    : "Reinstall modpack"
                    : "Install modpack";
            if (string.IsNullOrWhiteSpace(_gameFolder))
                _status.Text = "Set the game folder on the Manage tab first.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not read manifest: {ex.Message}";
        }
    }

    private void ShowCompatibility(IEnumerable<string>? compatibleBuildIds)
    {
        var result = GameCompatibility.Evaluate(compatibleBuildIds, _installedGameBuildId);
        _compatibility.Foreground = new SolidColorBrush(
            result.Status == GameCompatibilityStatus.Compatible ? Colors.LightGreen : Colors.Orange);
        _compatibility.Text = result.Status switch
        {
            GameCompatibilityStatus.Compatible =>
                $"Compatible with installed Steam build {result.InstalledBuildId}.",
            GameCompatibilityStatus.Incompatible =>
                $"May not be supported: installed Steam build {result.InstalledBuildId} is not listed by this modpack. " +
                $"Declared builds: {string.Join(", ", result.CompatibleBuildIds)}.",
            GameCompatibilityStatus.InstalledBuildUnknown =>
                "May not be supported: the installed Steam build could not be determined. " +
                $"Declared builds: {string.Join(", ", result.CompatibleBuildIds)}.",
            _ => "May not be supported: this modpack does not declare compatible game builds."
        };
        _acknowledgeCompatibility.IsVisible = result.MayBeUnsupported;
    }

    private void RefreshInstallAvailability()
    {
        _install.IsEnabled = !string.IsNullOrWhiteSpace(_gameFolder) &&
            (!SelectedInstallNeedsNexus() || HasNexusCredentials()) &&
            (!_acknowledgeCompatibility.IsVisible || _acknowledgeCompatibility.IsChecked == true);
    }

    private bool IsPreviouslySelected(ModEntry mod)
    {
        if (mod.Required || _installedPack is null)
            return false;

        return _installedPack.SelectedOptionalModIds is null ||
               _installedPack.SelectedOptionalModIds.Contains(
                   mod.Id, StringComparer.OrdinalIgnoreCase);
    }

    private void OnModChoiceChanged(ModEntry changed)
    {
        if (_manifest is null || _updatingChoices || changed.Required)
            return;

        _updatingChoices = true;
        try
        {
            if (_modChoices[changed.Id].IsChecked == true)
                SelectDependencies(changed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            else
                ClearOptionalDependants(changed.Id, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            _updatingChoices = false;
        }
        RefreshOptionalSummary();
        RefreshNexusAccess();
        RefreshInstallAvailability();
    }

    private void SelectDependencies(ModEntry mod, HashSet<string> visited)
    {
        if (_manifest is null || !visited.Add(mod.Id))
            return;

        foreach (var dependencyId in mod.Dependencies)
        {
            var dependency = _manifest.Mods.FirstOrDefault(candidate =>
                candidate.Id.Equals(dependencyId, StringComparison.OrdinalIgnoreCase));
            if (dependency is null)
                continue;
            _modChoices[dependency.Id].IsChecked = true;
            SelectDependencies(dependency, visited);
        }
    }

    private void ClearOptionalDependants(string dependencyId, HashSet<string> visited)
    {
        if (_manifest is null || !visited.Add(dependencyId))
            return;

        foreach (var dependant in _manifest.Mods.Where(candidate =>
                     !candidate.Required &&
                     candidate.Dependencies.Contains(dependencyId, StringComparer.OrdinalIgnoreCase)))
        {
            _modChoices[dependant.Id].IsChecked = false;
            ClearOptionalDependants(dependant.Id, visited);
        }
    }

    private async Task InstallAsync()
    {
        if (_manifest is null)
            return;
        if (string.IsNullOrWhiteSpace(_gameFolder))
        {
            _status.Text = "Set the game folder on the Manage tab first.";
            return;
        }

        var selectedOptionalMods = _manifest.Mods
            .Where(mod => !mod.Required && _modChoices[mod.Id].IsChecked == true)
            .ToArray();
        var confirmation = new OptionalSelectionConfirmationWindow(_pack.Name, selectedOptionalMods);
        if (!await confirmation.ShowDialog<bool>(this))
            return;

        var switching = _activePack is not null && !_pack.IsId(_activePack.PackId);
        if (switching)
        {
            var selectedIds = selectedOptionalMods.Select(mod => mod.Id).ToArray();
            var selectedManifest = ModpackSelection.Resolve(_manifest, selectedIds).Manifest!;
            var currentEntries = new JournalStore(_gameFolder).Load()
                .Where(entry => entry.PackId?.Equals(
                    _activePack!.PackId, StringComparison.OrdinalIgnoreCase) == true);
            var switchPlan = ModpackSwitchPlanner.Create(currentEntries, selectedManifest.Mods);
            var switchConfirmation = new ModpackSwitchConfirmationWindow(
                _activePack!.Name, _pack.Name, switchPlan);
            if (!await switchConfirmation.ShowDialog<bool>(this))
                return;
        }

        _progress.IsVisible = true;
        _progressStatus.IsVisible = true;
        _downloadStats.IsVisible = true;
        _progress.IsIndeterminate = true;
        _progress.Value = 0;
        _progressStatus.Text = "Preparing downloads...";
        _downloadStats.Text = string.Empty;
        _install.IsEnabled = false;
        _cancelInstall.IsVisible = true;
        _cancelInstall.IsEnabled = true;
        SetOptionalChoicesEnabled(false);
        _speedTimer.Restart();
        _progressModIndex = 0;
        _lastProgressBytes = 0;
        _lastProgressTime = TimeSpan.Zero;
        _installCancellation = new CancellationTokenSource();
        try
        {
            var fallback = BuildFallback(_pack);
            var selectedOptionalIds = selectedOptionalMods.Select(mod => mod.Id).ToArray();
            var progress = new Progress<ModpackInstallProgress>(UpdateInstallProgress);
            var report = await Task.Run(() => new ModpackInstaller(_gameFolder, _http)
                .InstallAsync(_manifest, fallback, pack: _pack,
                    selectedOptionalIds: selectedOptionalIds,
                    progress: progress,
                    switchInstalledPack: switching,
                    cancellationToken: _installCancellation.Token));
            if (report.Success)
            {
                _status.Text = $"Installed {_pack.Name}.";
            }
            else
            {
                var details = report.Lines.Count == 0
                    ? "No further details were returned."
                    : string.Join(Environment.NewLine, report.Lines);
                _status.Text = "Install did not complete: " + details;
                Diagnostic.Write(details, "modpack-install");
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Install failed: {ex.Message}";
            Diagnostic.Write(ex.ToString(), "modpack-install");
        }
        finally
        {
            _installCancellation.Dispose();
            _installCancellation = null;
            _cancelInstall.IsVisible = false;
            _speedTimer.Stop();
            SetOptionalChoicesEnabled(true);
            if (_status.Text?.StartsWith("Installed ", StringComparison.Ordinal) == true)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = 100;
                _progressStatus.Text = "Installation complete.";
                _downloadStats.Text = string.Empty;
            }
            else
            {
                _progress.IsIndeterminate = false;
                _progressStatus.Text = "Installation stopped.";
            }
            RefreshInstallAvailability();
        }
    }

    private void CancelInstall()
    {
        if (_installCancellation is null)
            return;

        _cancelInstall.IsEnabled = false;
        _progressStatus.Text = "Cancelling after the current file operation...";
        _installCancellation.Cancel();
    }

    private async Task UninstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_gameFolder))
        {
            _status.Text = "Set the game folder on the Manage tab first.";
            return;
        }

        var confirmation = new ModpackUninstallConfirmationWindow(_pack.Name);
        if (!await confirmation.ShowDialog<bool>(this))
            return;

        _install.IsEnabled = false;
        _uninstall.IsEnabled = false;
        SetOptionalChoicesEnabled(false);
        _progress.IsVisible = true;
        _progress.IsIndeterminate = true;
        _progressStatus.IsVisible = true;
        _progressStatus.Text = "Uninstalling modpack...";
        _downloadStats.IsVisible = false;
        try
        {
            var report = await Task.Run(() =>
                new ModpackInstaller(_gameFolder).Uninstall(_pack.Id));
            if (report.Success)
            {
                var warnings = report.Lines
                    .Where(line => line.TrimStart().StartsWith(
                        "warning:", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _status.Text = warnings.Count == 0
                    ? $"Uninstalled {_pack.Name}."
                    : $"Uninstalled {_pack.Name}.{Environment.NewLine}" +
                      string.Join(Environment.NewLine, warnings);
                _progressStatus.Text = "Uninstall complete.";
                _uninstall.IsVisible = false;
            }
            else
            {
                _status.Text = "Uninstall did not complete: " +
                    (report.Lines.Count == 0
                        ? "No further details were returned."
                        : string.Join(Environment.NewLine, report.Lines));
                _progressStatus.Text = "Uninstall stopped.";
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Uninstall failed: {ex.Message}";
            _progressStatus.Text = "Uninstall stopped.";
        }
        finally
        {
            _progress.IsIndeterminate = false;
            _uninstall.IsEnabled = true;
            SetOptionalChoicesEnabled(true);
            RefreshInstallAvailability();
        }
    }

    private bool SelectedInstallNeedsNexus()
    {
        if (_manifest is null)
            return false;
        return _manifest.Mods.Any(mod =>
            mod.NexusModId is not null &&
            (mod.Required || _modChoices.TryGetValue(mod.Id, out var choice) && choice.IsChecked == true));
    }

    private static bool HasNexusCredentials() =>
        NexusTokenStore.TryLoad() is not null || ApiKeyStore.TryLoad() is not null;

    private void RefreshNexusAccess()
    {
        var needsNexus = SelectedInstallNeedsNexus();
        var hasCredentials = HasNexusCredentials();
        _nexusAccess.IsVisible = needsNexus;
        _nexusActions.IsVisible = needsNexus && !hasCredentials;
        _nexusAccess.Foreground = new SolidColorBrush(hasCredentials ? Colors.LightGreen : Colors.Orange);
        _nexusAccess.Text = hasCredentials
            ? "Nexus access is ready for the selected mods."
            : "This selection downloads from Nexus Mods. Sign in or enter a personal API key before installing.";
    }

    private async Task EnterNexusApiKeyAsync()
    {
        _nexusApiKey.IsEnabled = false;
        try
        {
            var dialog = new NexusCredentialWindow(_http, ApiKeyStore.Exists);
            await dialog.ShowDialog(this);
        }
        finally
        {
            _nexusApiKey.IsEnabled = true;
            RefreshNexusAccess();
            RefreshInstallAvailability();
        }
    }

    private async Task SignInToNexusAsync()
    {
        _nexusLogin.IsEnabled = false;
        _nexusAccess.Text = "Opening Nexus Mods in your browser...";
        string? failure = null;
        try
        {
            var user = await NexusOAuth.LoginAsync(_http);
            _nexusAccess.Text = $"Signed in as {user.Name}.";
        }
        catch (Exception ex)
        {
            failure = $"Nexus sign-in failed: {ex.Message}";
            Diagnostic.Write(ex.ToString(), "nexus-login");
        }
        finally
        {
            _nexusLogin.IsEnabled = true;
            RefreshNexusAccess();
            if (failure is not null)
                _nexusAccess.Text = failure;
            RefreshInstallAvailability();
        }
    }

    private void SetOptionalChoicesEnabled(bool enabled)
    {
        if (_manifest is null)
            return;
        foreach (var mod in _manifest.Mods.Where(mod => !mod.Required))
            _modChoices[mod.Id].IsEnabled = enabled;
    }

    private void RefreshOptionalSummary()
    {
        if (_manifest is null)
            return;
        var optionalCount = _manifest.Mods.Count(mod => !mod.Required);
        var selectedCount = _manifest.Mods.Count(mod =>
            !mod.Required && _modChoices.TryGetValue(mod.Id, out var choice) && choice.IsChecked == true);
        _optionalSummary.Text = optionalCount == 0
            ? "No optional mods are available."
            : $"Optional mods selected: {selectedCount} of {optionalCount}.";
    }

    private void UpdateInstallProgress(ModpackInstallProgress update)
    {
        if (update.Stage != ModpackInstallStage.Downloading)
        {
            _progress.IsIndeterminate = true;
            _progressStatus.Text = update.Stage switch
            {
                ModpackInstallStage.Preparing => "Downloads complete. Preparing the modpack...",
                ModpackInstallStage.Planning =>
                    $"Checking archive {update.ModIndex} of {update.ModCount}: {update.ModName}",
                ModpackInstallStage.Installing =>
                    $"Installing mod {update.ModIndex} of {update.ModCount}: {update.ModName}",
                _ => "Working..."
            };
            _downloadStats.Text = string.Empty;
            return;
        }

        if (_progressModIndex != update.ModIndex)
        {
            _progressModIndex = update.ModIndex;
            _lastProgressBytes = update.DownloadedBytes;
            _lastProgressTime = _speedTimer.Elapsed;
        }

        _progressStatus.Text = $"Downloading {update.ModIndex} of {update.ModCount}: {update.ModName}";
        if (update.FromCache)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
            _downloadStats.Text = $"{FormatBytes(update.DownloadedBytes)} ready from cache.";
            return;
        }

        var elapsed = _speedTimer.Elapsed;
        var interval = elapsed - _lastProgressTime;
        var byteDelta = update.DownloadedBytes - _lastProgressBytes;
        var bytesPerSecond = interval.TotalSeconds > 0.2 && byteDelta >= 0
            ? byteDelta / interval.TotalSeconds
            : 0;
        if (interval.TotalSeconds > 0.2)
        {
            _lastProgressBytes = update.DownloadedBytes;
            _lastProgressTime = elapsed;
        }

        _progress.IsIndeterminate = update.TotalBytes is not > 0;
        if (update.TotalBytes is > 0)
            _progress.Value = Math.Clamp(update.DownloadedBytes * 100d / update.TotalBytes.Value, 0, 100);
        var total = update.TotalBytes is > 0 ? $" of {FormatBytes(update.TotalBytes.Value)}" : string.Empty;
        var speed = bytesPerSecond > 0 ? $" — {FormatBytes((long)bytesPerSecond)}/s" : string.Empty;
        _downloadStats.Text = $"{FormatBytes(update.DownloadedBytes)}{total}{speed}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static IModSource? BuildFallback(ModpackSummary pack)
    {
        if (pack.Source is null)
            return null;

        return pack.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? new HttpModSource(m => $"{pack.Source.TrimEnd('/')}/{Uri.EscapeDataString(m.FileName)}")
            : new LocalFileSource(pack.Source);
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
}
