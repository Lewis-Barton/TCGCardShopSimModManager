using System.Net.Http;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TCGCardShopSimModManager.Core;

namespace TCGCardShopSimModManager.App;

public sealed class ModpackAuthorUpdateWindow : Window
{
    private readonly IReadOnlyList<ModpackSummary> _packs;
    private readonly ModpackIndexReader _reader;
    private readonly HttpClient _http;
    private readonly ProgressBar _progress = new() { Minimum = 0, IsVisible = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _results = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = FontFamily.Parse("Consolas")
    };
    private readonly Button _run = new() { Content = "Check again", IsEnabled = false };
    private CancellationTokenSource? _cancellation;

    public ModpackAuthorUpdateWindow(
        IReadOnlyList<ModpackSummary> packs,
        ModpackIndexReader reader,
        HttpClient http)
    {
        _packs = packs;
        _reader = reader;
        _http = http;

        Title = "Modpack author tools — pinned Nexus files";
        Width = 820;
        Height = 650;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _progress.Maximum = Math.Max(1, packs.Count);
        _run.Click += async (_, _) => await RunCheckAsync();
        Closed += (_, _) => _cancellation?.Cancel();

        var heading = new TextBlock
        {
            Text = "Modpack authors only",
            FontSize = 20,
            FontWeight = FontWeight.Bold
        };
        var notice = AuthorNotice();
        var progressPanel = ProgressPanel();
        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Children =
            {
                heading,
                notice,
                progressPanel,
                _results,
                _run
            }
        };

        Grid.SetRow(notice, 1);
        Grid.SetRow(progressPanel, 2);
        Grid.SetRow(_results, 3);
        Grid.SetRow(_run, 4);
        _results.Margin = new Thickness(0, 12, 0, 12);
        _run.HorizontalAlignment = HorizontalAlignment.Left;

        Opened += async (_, _) => await RunCheckAsync();
    }

    private Control AuthorNotice()
    {
        return new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Text = "This tool is not needed to install or use modpacks. It does not change manifests or install files. Review and test every suggested Nexus replacement before publishing a pack update."
        };
    }

    private Control ProgressPanel()
    {
        return new StackPanel
        {
            Spacing = 6,
            Children = { _status, _progress }
        };
    }

    private async Task RunCheckAsync()
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        var cancellationToken = _cancellation.Token;
        _run.IsEnabled = false;
        _progress.IsVisible = true;
        _progress.Value = 0;
        _results.Text = string.Empty;

        try
        {
            var output = new StringBuilder();
            using var api = new NexusApi(
                NexusApi.ApiBaseUrl(), NexusApi.GameDomain, NexusApi.UserAgent, _http);
            var auth = NexusAuth.Unified(_http);
            var filesByMod = new Dictionary<long, IReadOnlyList<NexusFileInfo>>();
            async Task<IReadOnlyList<NexusFileInfo>> ListFilesAsync(long modId, CancellationToken token)
            {
                if (filesByMod.TryGetValue(modId, out var existing))
                    return existing;
                var files = await api.ListFilesAsync(modId, auth, token);
                filesByMod.Add(modId, files);
                return files;
            }
            var checker = new ModpackUpdateChecker(
                ListFilesAsync);

            for (var index = 0; index < _packs.Count; index++)
            {
                var pack = _packs[index];
                _status.Text = $"Checking {pack.Name} ({index + 1} of {_packs.Count})...";
                var manifest = await _reader.FetchManifestAsync(pack, cancellationToken: cancellationToken);
                var results = await checker.CheckAsync(manifest, cancellationToken);
                AppendPack(output, manifest.Name, results);
                _results.Text = output.ToString();
                _progress.Value = index + 1;
            }

            _status.Text = "Check complete. No files or manifests were changed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _status.Text = "Check cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not complete the check: {ex.Message}";
            Diagnostic.Write(ex.ToString(), "modpack-author-update-check");
        }
        finally
        {
            _progress.IsVisible = false;
            _run.IsEnabled = true;
        }
    }

    private static void AppendPack(
        StringBuilder output,
        string packName,
        IReadOnlyList<ModpackUpdateResult> results)
    {
        var updates = results.Where(result => result.Status == ModpackUpdateStatus.UpdateAvailable).ToArray();
        var missing = results.Count(result => result.Status == ModpackUpdateStatus.MissingOrArchived);
        var current = results.Count(result => result.Status == ModpackUpdateStatus.Current);
        var skipped = results.Count(result => result.Status == ModpackUpdateStatus.NotChecked);

        if (output.Length > 0)
            output.AppendLine();
        output.AppendLine(packName);
        output.AppendLine(new string('-', packName.Length));
        foreach (var result in results.Where(result => result.Status != ModpackUpdateStatus.Current))
            AppendResult(output, result);
        if (updates.Length == 0 && missing == 0 && skipped == 0)
            output.AppendLine("All pinned Nexus files are current.");

        output.AppendLine($"Summary: {updates.Length} update(s), {missing} missing or archived, {current} current, {skipped} not checked.");
        if (updates.Length == 0)
            return;

        output.AppendLine("Suggested selectors (review and test before use):");
        foreach (var result in updates)
        {
            var role = result.Mod.Id.Equals(ModListConventions.BepInExModId, StringComparison.OrdinalIgnoreCase)
                ? "bepinex"
                : result.Mod.Required ? "required" : "optional";
            output.AppendLine($"{role} nexus:{result.Mod.NexusModId}:{result.SuggestedFile!.FileId} # {result.Mod.Name}");
        }
    }

    private static void AppendResult(StringBuilder output, ModpackUpdateResult result)
    {
        var status = result.Status switch
        {
            ModpackUpdateStatus.UpdateAvailable => "UPDATE",
            ModpackUpdateStatus.MissingOrArchived => "MISSING",
            _ => "NOT CHECKED"
        };
        var version = string.IsNullOrWhiteSpace(result.PinnedFile?.Version ?? result.Mod.Version)
            ? "version unknown"
            : $"v{result.PinnedFile?.Version ?? result.Mod.Version}";
        output.Append($"[{status}] {result.Mod.Name} {version}");
        if (result.SuggestedFile is { } suggested)
            output.Append($" -> v{suggested.Version ?? "unknown"} (nexus:{result.Mod.NexusModId}:{suggested.FileId})");
        output.AppendLine();
        if (result.Message is not null)
            output.AppendLine($"  {result.Message}");
    }
}
