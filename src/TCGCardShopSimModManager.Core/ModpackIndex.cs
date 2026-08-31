using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace TCGCardShopSimModManager.Core;

/// <summary>
/// One pack as listed in modpacks/index.json. <see cref="Logo"/> and
/// <see cref="Manifest"/> are repo-relative paths, resolved against the index
/// base URL when the app fetches them.
/// </summary>
public sealed record ModpackSummary(
    string Id,
    string Name,
    string ShortDescription,
    string Logo,
    string Manifest,
    string Version,
    string? Updated = null,
    /// <summary>
    /// Optional pack-level archive source used when a mod lists neither a
    /// <see cref="ModEntry.DownloadUrl"/> nor a Nexus id. An http(s) URL is used
    /// as a base; anything else is treated as a local folder.
    /// </summary>
    string? Source = null,
    /// <summary>
    /// Legacy ids this pack used to publish under. After a pack id rename the
    /// installed-version journal may still hold the old id; matching against
    /// these aliases keeps update detection and tracking working (BUG-009).
    /// </summary>
    List<string>? FormerIds = null,
    bool Featured = true,
    bool Nsfw = false,
    long? DownloadSize = null,
    List<string>? Tags = null,
    List<string>? ModIds = null,
    List<string>? CompatibleGameBuildIds = null)
{
    /// <summary>True when <paramref name="id"/> equals this pack's canonical id
    /// or any of its legacy <see cref="FormerIds"/>, case-insensitively.</summary>
    public bool IsId(string id) =>
        Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        FormerIds?.Any(f => f.Equals(id, StringComparison.OrdinalIgnoreCase)) == true;
}

/// <summary>The modpacks/index.json document.</summary>
public sealed record ModpackIndex(int Version, List<ModpackSummary> Packs);

public enum ModpackSortOrder
{
    Catalog,
    RecentlyUpdated,
    Name,
    SmallestDownload,
    LargestDownload
}

public sealed record ModpackCatalogFilter(
    string? Search = null,
    bool IncludeNonFeatured = true,
    bool IncludeNsfw = false,
    long? MaxDownloadSize = null,
    string? Mod = null,
    bool ExcludeMod = false,
    string? Tag = null,
    bool InstalledOnly = false);

public static class ModpackCatalogOrdering
{
    public static IReadOnlyList<ModpackSummary> FilterAndSort(
        IEnumerable<ModpackSummary> packs,
        ModpackCatalogFilter filter,
        IEnumerable<string> installedPackIds,
        ModpackSortOrder order)
    {
        var installed = installedPackIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var search = filter.Search?.Trim();
        var mod = filter.Mod?.Trim();
        var tag = filter.Tag?.Trim();
        var visible = packs.Where(pack =>
            (string.IsNullOrEmpty(search) ||
             pack.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             pack.ShortDescription.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (filter.IncludeNonFeatured || pack.Featured) &&
            (filter.IncludeNsfw || !pack.Nsfw) &&
            (filter.MaxDownloadSize is null ||
             (pack.DownloadSize is not null && pack.DownloadSize <= filter.MaxDownloadSize)) &&
            (string.IsNullOrEmpty(mod) ||
             (pack.ModIds?.Any(value => value.Contains(mod, StringComparison.OrdinalIgnoreCase)) == true) !=
             filter.ExcludeMod) &&
            (string.IsNullOrEmpty(tag) ||
             pack.Tags?.Any(value => value.Contains(tag, StringComparison.OrdinalIgnoreCase)) == true) &&
            (!filter.InstalledOnly || installed.Any(pack.IsId)));

        return Sort(visible, order);
    }

    public static IReadOnlyList<ModpackSummary> Sort(
        IEnumerable<ModpackSummary> packs,
        ModpackSortOrder order)
    {
        var ordered = order switch
        {
            ModpackSortOrder.RecentlyUpdated => packs
                .OrderByDescending(pack => UpdatedDayNumber(pack.Updated))
                .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase),
            ModpackSortOrder.Name => packs
                .OrderBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase),
            ModpackSortOrder.SmallestDownload => packs
                .OrderBy(pack => pack.DownloadSize is null)
                .ThenBy(pack => pack.DownloadSize)
                .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase),
            ModpackSortOrder.LargestDownload => packs
                .OrderBy(pack => pack.DownloadSize is null)
                .ThenByDescending(pack => pack.DownloadSize)
                .ThenBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase),
            _ => packs
        };

        return ordered.ToList();
    }

    private static int UpdatedDayNumber(string? updated) =>
        DateOnly.TryParseExact(updated, "yyyy-MM-dd", out var date)
            ? date.DayNumber
            : int.MinValue;
}

/// <summary>
/// Where the hosted modpack index lives. One constant so moving the repo only
/// touches a single line.
/// </summary>
public static class ModpackCatalog
{
    public const string DefaultIndexBaseUrl =
        "https://raw.githubusercontent.com/Lewis-Barton/TCGCardShopSimModManager/main/modpacks/";
}

/// <summary>
/// Fetches and parses the hosted modpack index, and resolves the absolute URLs
/// for each pack's logo and manifest.
/// </summary>
public sealed class ModpackIndexReader : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cachePath;
    private readonly bool _cachePathWasProvided;
    private readonly int _maxAttempts;
    private readonly int _retryBaseDelayMs;

    public bool LastFetchUsedCache { get; private set; }

    public ModpackIndexReader(
        HttpClient? http = null,
        string? cachePath = null,
        int maxAttempts = 3,
        int retryBaseDelayMs = 500)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _cachePathWasProvided = cachePath is not null;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCGCardShopSimModManager",
            "modpack-index.json");
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryBaseDelayMs = Math.Max(0, retryBaseDelayMs);
    }

    public async Task<ModpackIndex> FetchIndexAsync(string? baseUrl = null, CancellationToken cancellationToken = default)
    {
        var url = Combine(baseUrl ?? ModpackCatalog.DefaultIndexBaseUrl, "index.json");
        var useCache = baseUrl is null || _cachePathWasProvided;
        LastFetchUsedCache = false;

        try
        {
            var json = await FetchStringWithRetryAsync(url, cancellationToken);
            var index = JsonSerializer.Deserialize<ModpackIndex>(json, Options)
                ?? throw new InvalidOperationException($"Failed to parse modpack index: {url}");

            if (useCache)
                WriteCacheBestEffort(index);
            return index;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (useCache && TryReadCache(out var cached))
        {
            LastFetchUsedCache = true;
            LogBestEffort($"Using cached modpack index after refresh failed: {ex.Message}");
            return cached!;
        }
    }

    public string LogoUrl(ModpackSummary summary, string? baseUrl = null) =>
        Combine(baseUrl ?? ModpackCatalog.DefaultIndexBaseUrl, summary.Logo);

    /// <summary>Returns the last saved catalog without making a network request.</summary>
    public ModpackIndex? ReadCachedIndex() => TryReadCache(out var index) ? index : null;

    /// <summary>Returns the catalog snapshot shipped with this build.</summary>
    public ModpackIndex ReadBundledIndex()
    {
        using var stream = typeof(ModpackIndexReader).Assembly.GetManifestResourceStream(
            "TCGCardShopSimModManager.Core.modpack-index.json")
            ?? throw new InvalidOperationException("The bundled modpack catalog is missing.");
        return JsonSerializer.Deserialize<ModpackIndex>(stream, Options)
            ?? throw new InvalidOperationException("The bundled modpack catalog is invalid.");
    }

    public string ManifestUrl(ModpackSummary summary, string? baseUrl = null) =>
        Combine(baseUrl ?? ModpackCatalog.DefaultIndexBaseUrl, summary.Manifest);

    public async Task<ModListManifest> FetchManifestAsync(
        ModpackSummary summary, string? baseUrl = null, CancellationToken cancellationToken = default)
    {
        var json = await FetchStringWithRetryAsync(ManifestUrl(summary, baseUrl), cancellationToken);
        var manifest = JsonSerializer.Deserialize<ModListManifest>(json, Options)
            ?? throw new InvalidOperationException($"Failed to parse manifest for pack '{summary.Id}'.");

        // Mirror ManifestReader: a manifest that omits dependencies/conflicts
        // deserialises those lists as null; treat "not declared" as "empty".
        return manifest with
        {
            Mods = (manifest.Mods ?? new List<ModEntry>())
                .Select(m => m with
                {
                    Dependencies = m.Dependencies ?? new List<string>(),
                    Conflicts = m.Conflicts ?? new List<string>()
                })
                .ToList()
        };
    }

    private async Task<string> FetchStringWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "TCGCardShopSimModManager");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(cancellationToken);

            if (attempt >= _maxAttempts || !IsTransient(response.StatusCode))
            {
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException($"Request failed: {url}");
            }

            var delay = RetryDelay(response, attempt);
            LogBestEffort($"Modpack request returned {(int)response.StatusCode}; retrying in {delay.TotalSeconds:0.##} seconds.");
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ClampDelay(delta);
        if (retryAfter?.Date is { } date)
            return ClampDelay(date - DateTimeOffset.UtcNow);

        return TimeSpan.FromMilliseconds(_retryBaseDelayMs * Math.Pow(2, attempt - 1));
    }

    private static TimeSpan ClampDelay(TimeSpan delay) =>
        TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 10_000));

    private void WriteCacheBestEffort(ModpackIndex index)
    {
        try
        {
            CacheFile().Write(index);
        }
        catch (Exception ex)
        {
            LogBestEffort($"Could not cache the modpack index: {ex.Message}");
        }
    }

    private bool TryReadCache(out ModpackIndex? index)
    {
        try
        {
            index = CacheFile().Read();
            return index is not null;
        }
        catch (Exception ex)
        {
            LogBestEffort($"Could not read the cached modpack index: {ex.Message}");
            index = null;
            return false;
        }
    }

    private AtomicJsonFile<ModpackIndex?> CacheFile() =>
        new(_cachePath, Options, () => null, recoverCorrupt: true);

    private static void LogBestEffort(string message)
    {
        try { Diagnostic.Write(message, "modpack-index"); }
        catch { /* diagnostics must not replace the catalog result */ }
    }

    private static string Combine(string baseUrl, string relative) =>
        baseUrl.TrimEnd('/') + "/" + relative.TrimStart('/');

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
