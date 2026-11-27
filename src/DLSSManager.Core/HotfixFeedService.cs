using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DLSSManager.Core;

public sealed record HotfixFeedOptions(
    string? CustomFeedUrl = null,
    string? GoogleSearchApiKey = null,
    string? GoogleSearchCx = null);

public sealed record HotfixResolutionResult(
    DriverUpdateInfo? LatestHotfix,
    DriverUpdateInfo? PreviousHotfix,
    DriverUpdateInfo[] AllHotfixes);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HotfixSourceFetchStatus
{
    Success,
    CacheHit,
    Blocked403_429,
    TimeoutOrError
}

public sealed record HotfixSourceStatus(
    string SourceName,
    HotfixSourceFetchStatus Status,
    int FoundCount,
    DateTime LastCheckedUtc);

public sealed record HotfixSourceState(
    DateTimeOffset? BlockedUntilUtc = null,
    string? ETag = null,
    DateTimeOffset? LastModifiedUtc = null,
    DriverUpdateInfo[]? CachedEntries = null);

public sealed record HotfixFeedCacheDocument(
    DateTimeOffset? FetchedAtUtc,
    string? SourceName,
    DriverUpdateInfo[] Entries,
    Dictionary<string, HotfixSourceState>? Sources = null,
    DateTimeOffset? LastManualRefreshUtc = null,
    int SchemaVersion = 0,
    HotfixSourceStatus[]? SourceStatuses = null);

public sealed record HotfixScanResult(
    DriverUpdateInfo[] Drivers,
    bool Checked,
    bool FromCache,
    string? SourceName,
    HotfixSourceStatus[]? SourceStatuses = null,
    DateTimeOffset? LastScanUtc = null);

public static partial class HotfixFeedScanner
{
    public const int CurrentSchemaVersion = 5;

    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(6);
    public static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinBlockDuration = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PerSourceTimeout = TimeSpan.FromSeconds(8);

    public const string DefaultHotfixFeedUrl = "https://raw.githubusercontent.com/MrSchwafel/DLSS-Manager/main/hotfixes.json";
    public const string DefaultGitHubHotfixFeedUrl = DefaultHotfixFeedUrl;
    public const string NvidiaForumUrl = "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/";
    public const string NvidiaForumSearchUrl = "https://www.nvidia.com/en-us/geforce/forums/game-ready-drivers/13/?q=GeForce+Hotfix+Display+Driver";
    public const string TechPowerUpUrl = "https://www.techpowerup.com/download/nvidia-geforce-graphics-drivers/";
    public const string Guru3DUrl = "https://www.guru3d.com/download/category/videocards-nvidia-geforce-vista-win-10-64-bit/";
    public const string Guru3DRssUrl = "https://www.guru3d.com/rss/downloads/";
    public const string GoogleNewsRssUrl = "https://news.google.com/rss/search?q=%22GeForce+Hotfix+Display+Driver%22+OR+%22NVIDIA+GeForce+Hotfix%22&hl=en-US&gl=US&ceid=US:en";
    public const string GoogleCustomSearchApiBaseUrl = "https://www.googleapis.com/customsearch/v1";
    public const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    public const string AcceptLanguage = "en-US,en;q=0.9,de;q=0.8";
    private const string HtmlAcceptHeader = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string FeedAcceptHeader = "application/json,application/rss+xml,application/xml,text/xml;q=0.9,*/*;q=0.8";

    private const int MaxRedirects = 5;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ScanLocks = new(StringComparer.OrdinalIgnoreCase);

    private sealed record FetchOutcome(
        bool Skipped,
        bool NotModified,
        bool Succeeded,
        bool Blocked,
        string? Content,
        HttpStatusCode? StatusCode = null);

    public static async Task<HotfixScanResult> ScanAsync(
        HttpClient client,
        bool isNotebook,
        string? cachePath = null,
        bool forceRefresh = false,
        Func<DateTimeOffset>? clock = null,
        bool bypassCooldown = false,
        HotfixFeedOptions? options = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var nowProvider = clock ?? (() => DateTimeOffset.UtcNow);
        var lockKey = string.IsNullOrWhiteSpace(cachePath) ? "__ephemeral__" : Path.GetFullPath(cachePath);
        var gate = ScanLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var now = nowProvider();
            var cache = LoadCache(cachePath);
            var cachedEntries = SanitizeEntries(cache?.Entries);

            if (!forceRefresh &&
                cache?.FetchedAtUtc is { } fetched &&
                now >= fetched &&
                now - fetched < CacheTtl)
            {
                return new HotfixScanResult(
                    cachedEntries,
                    Checked: true,
                    FromCache: true,
                    cache.SourceName,
                    cache.SourceStatuses ?? [],
                    cache.FetchedAtUtc);
            }

            if (forceRefresh &&
                !bypassCooldown &&
                cache?.LastManualRefreshUtc is { } lastManual &&
                now >= lastManual &&
                now - lastManual < ManualRefreshCooldown)
            {
                var hasValidCache = cache.FetchedAtUtc is not null;
                return new HotfixScanResult(
                    cachedEntries,
                    Checked: hasValidCache,
                    FromCache: cachedEntries.Length > 0,
                    cache.SourceName,
                    cache.SourceStatuses ?? [],
                    cache.FetchedAtUtc);
            }

            var sources = cache?.Sources is not null
                ? new Dictionary<string, HotfixSourceState>(cache.Sources, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, HotfixSourceState>(StringComparer.OrdinalIgnoreCase);

            var discovered = new List<DriverUpdateInfo>();
            var sourceStatuses = new List<HotfixSourceStatus>();
            var anySucceeded = false;
            var anyFreshContent = false;
            var anyNotModified = false;
            string? winningSource = null;

            // Source 1 (Prioritized): Machine-readable GitHub JSON / Custom RSS-Atom Feed
            var (feedUrl, isDefaultFeed) = ResolveStructuredFeedUrl(options?.CustomFeedUrl);
            var feedOutcome = await FetchSourceAsync(
                client, "github-rss-feed", feedUrl, NvidiaDriverLookup.IsSafeReleaseWebHost, sources, now, token,
                acceptHeader: FeedAcceptHeader).ConfigureAwait(false);
            DriverUpdateInfo[] feedEntries = [];
            HotfixSourceFetchStatus feedStatus;
            if (isDefaultFeed && feedOutcome.StatusCode == HttpStatusCode.NotFound)
            {
                feedStatus = HotfixSourceFetchStatus.Success;
            }
            else if (feedOutcome.NotModified)
            {
                anySucceeded = true;
                anyNotModified = true;
                feedStatus = HotfixSourceFetchStatus.CacheHit;
                feedEntries = GetCachedSourceEntries(sources, "github-rss-feed", cache, cachedEntries);
            }
            else if (feedOutcome.Succeeded && !string.IsNullOrWhiteSpace(feedOutcome.Content))
            {
                anySucceeded = true;
                anyFreshContent = true;
                feedStatus = HotfixSourceFetchStatus.Success;
                feedEntries = ParseStructuredFeedHotfixes(feedOutcome.Content, isNotebook);
                UpdateCachedSourceEntries(sources, "github-rss-feed", feedEntries);
            }
            else if (feedOutcome.Blocked)
            {
                feedStatus = HotfixSourceFetchStatus.Blocked403_429;
            }
            else
            {
                feedStatus = HotfixSourceFetchStatus.TimeoutOrError;
            }

            if (feedEntries.Length > 0)
            {
                discovered.AddRange(feedEntries);
                winningSource ??= "github-rss-feed";
            }
            sourceStatuses.Add(new HotfixSourceStatus("GitHub Feed", feedStatus, feedEntries.Length, now.UtcDateTime));

            // Automatic Fallback (only when GitHub Feed delivered 0 entries)
            if (feedEntries.Length == 0)
            {
                // Fallback 1: Google Search (Stage A: Google News RSS + Stage B: Optional Google Custom Search API)
                var googleNewsOutcome = await FetchSourceAsync(
                    client, "google-news", GoogleNewsRssUrl, IsGoogleNewsHost, sources, now, token,
                    acceptHeader: FeedAcceptHeader).ConfigureAwait(false);
                var googleFresh = new List<DriverUpdateInfo>();
                if (googleNewsOutcome.Succeeded && !googleNewsOutcome.NotModified && !string.IsNullOrWhiteSpace(googleNewsOutcome.Content))
                    googleFresh.AddRange(ParseRssOrAtomHotfixes(googleNewsOutcome.Content, isNotebook));

                FetchOutcome? googleCseOutcome = null;
                if (!string.IsNullOrWhiteSpace(options?.GoogleSearchApiKey) &&
                    !string.IsNullOrWhiteSpace(options?.GoogleSearchCx) &&
                    !googleNewsOutcome.Blocked)
                {
                    var cseUrl = $"{GoogleCustomSearchApiBaseUrl}?key={Uri.EscapeDataString(options.GoogleSearchApiKey.Trim())}" +
                                 $"&cx={Uri.EscapeDataString(options.GoogleSearchCx.Trim())}" +
                                 $"&q={Uri.EscapeDataString("site:nvidia.custhelp.com \"GeForce Hotfix Display Driver\"")}";
                    googleCseOutcome = await FetchSourceAsync(
                        client, "google-cse", cseUrl, IsGoogleApiHost, sources, now, token,
                        blockKeyOverride: "google-news", acceptHeader: FeedAcceptHeader).ConfigureAwait(false);
                    if (googleCseOutcome.Succeeded && !googleCseOutcome.NotModified && !string.IsNullOrWhiteSpace(googleCseOutcome.Content))
                        googleFresh.AddRange(ParseGoogleCustomSearchHotfixes(googleCseOutcome.Content, isNotebook));
                }

                var googleEntries = DeduplicateHotfixes(googleFresh);
                HotfixSourceFetchStatus googleStatus;
                if ((googleNewsOutcome.Succeeded && !googleNewsOutcome.NotModified) ||
                    (googleCseOutcome is { Succeeded: true, NotModified: false }))
                {
                    anySucceeded = true;
                    anyFreshContent = true;
                    googleStatus = HotfixSourceFetchStatus.Success;
                    UpdateCachedSourceEntries(sources, "google-news", googleEntries);
                }
                else if (googleNewsOutcome.NotModified || googleCseOutcome is { NotModified: true })
                {
                    anySucceeded = true;
                    anyNotModified = true;
                    googleStatus = HotfixSourceFetchStatus.CacheHit;
                    googleEntries = GetCachedSourceEntries(sources, "google-news", cache, cachedEntries);
                }
                else if (googleNewsOutcome.Blocked || googleCseOutcome is { Blocked: true })
                {
                    googleStatus = HotfixSourceFetchStatus.Blocked403_429;
                }
                else
                {
                    googleStatus = HotfixSourceFetchStatus.TimeoutOrError;
                }

                if (googleEntries.Length > 0)
                {
                    discovered.AddRange(googleEntries);
                    winningSource ??= "google-news";
                }
                sourceStatuses.Add(new HotfixSourceStatus("Google Suche", googleStatus, googleEntries.Length, now.UtcDateTime));

                // Fallback 2: TechPowerUp GPU Drivers Catalog
                var tpuOutcome = await FetchSourceAsync(
                    client, "techpowerup", TechPowerUpUrl, IsTechPowerUpHost, sources, now, token).ConfigureAwait(false);
                DriverUpdateInfo[] tpuEntries = [];
                HotfixSourceFetchStatus tpuStatus;
                if (tpuOutcome.NotModified)
                {
                    anySucceeded = true;
                    anyNotModified = true;
                    tpuStatus = HotfixSourceFetchStatus.CacheHit;
                    tpuEntries = GetCachedSourceEntries(sources, "techpowerup", cache, cachedEntries);
                }
                else if (tpuOutcome.Succeeded && !string.IsNullOrWhiteSpace(tpuOutcome.Content))
                {
                    anySucceeded = true;
                    anyFreshContent = true;
                    tpuStatus = HotfixSourceFetchStatus.Success;
                    tpuEntries = ParseCatalogHotfixes(tpuOutcome.Content, "https://www.techpowerup.com", isNotebook);
                    UpdateCachedSourceEntries(sources, "techpowerup", tpuEntries);
                }
                else if (tpuOutcome.Blocked)
                {
                    tpuStatus = HotfixSourceFetchStatus.Blocked403_429;
                }
                else
                {
                    tpuStatus = HotfixSourceFetchStatus.TimeoutOrError;
                }

                if (tpuEntries.Length > 0)
                {
                    discovered.AddRange(tpuEntries);
                    winningSource ??= "techpowerup";
                }
                sourceStatuses.Add(new HotfixSourceStatus("TechPowerUp", tpuStatus, tpuEntries.Length, now.UtcDateTime));

                // Fallback 3: nvidia.custhelp.com Hotfix Knowledgebase (only if still nothing found)
                if (discovered.Count == 0 && !anyNotModified)
                {
                    var custOutcome = await FetchSourceAsync(
                        client, "nvidia-custhelp", NvidiaDriverLookup.HotfixListUrl, NvidiaDriverLookup.IsNvidiaWebHost, sources, now, token).ConfigureAwait(false);
                    DriverUpdateInfo[] custEntries = [];
                    HotfixSourceFetchStatus custStatus;
                    if (custOutcome.NotModified)
                    {
                        anySucceeded = true;
                        anyNotModified = true;
                        custStatus = HotfixSourceFetchStatus.CacheHit;
                        custEntries = GetCachedSourceEntries(sources, "nvidia-custhelp", cache, cachedEntries);
                    }
                    else if (custOutcome.Succeeded && !string.IsNullOrWhiteSpace(custOutcome.Content))
                    {
                        anySucceeded = true;
                        anyFreshContent = true;
                        custStatus = HotfixSourceFetchStatus.Success;
                        custEntries = await ResolveCustHelpCandidatesAsync(
                            client, custOutcome.Content, isNotebook, sources, now, token).ConfigureAwait(false);
                        UpdateCachedSourceEntries(sources, "nvidia-custhelp", custEntries);
                    }
                    else if (custOutcome.Blocked)
                    {
                        custStatus = HotfixSourceFetchStatus.Blocked403_429;
                    }
                    else
                    {
                        custStatus = HotfixSourceFetchStatus.TimeoutOrError;
                    }

                    if (custEntries.Length > 0)
                    {
                        discovered.AddRange(custEntries);
                        winningSource ??= "nvidia-custhelp";
                    }
                    sourceStatuses.Add(new HotfixSourceStatus("custhelp.com", custStatus, custEntries.Length, now.UtcDateTime));
                }
            }

            var deduplicated = DeduplicateHotfixes(discovered);

            // Direct NVIDIA CDN HEAD probing for top entries lacking a direct .exe link
            if (deduplicated.Length > 0)
            {
                var probedCount = 0;
                for (var i = 0; i < deduplicated.Length && probedCount < 8; i++)
                {
                    var item = deduplicated[i];
                    if (!string.IsNullOrWhiteSpace(item.DirectInstallerUrl))
                        continue;

                    probedCount++;
                    var probedUrl = await ProbeCdnInstallerUrlInternalAsync(
                        client, item.ParsedVersion, isNotebook, sources, now, token).ConfigureAwait(false);
                    if (probedUrl is not null)
                        deduplicated[i] = item with { DirectInstallerUrl = probedUrl };
                }
            }

            var updatedManualTimestamp = forceRefresh ? now : cache?.LastManualRefreshUtc;
            var statusArray = sourceStatuses.ToArray();

            if (anyNotModified && deduplicated.Length == 0 && cachedEntries.Length > 0)
            {
                var effectiveSource = winningSource ?? cache?.SourceName;
                SaveCache(cachePath, new HotfixFeedCacheDocument(
                    now,
                    effectiveSource,
                    cachedEntries,
                    sources,
                    updatedManualTimestamp,
                    CurrentSchemaVersion,
                    statusArray));
                return new HotfixScanResult(cachedEntries, Checked: true, FromCache: true, effectiveSource, statusArray, now);
            }

            if (anySucceeded)
            {
                var effectiveSource = winningSource ?? (deduplicated.Length > 0 ? null : cache?.SourceName);
                var fromCache = anyNotModified && !anyFreshContent;
                SaveCache(cachePath, new HotfixFeedCacheDocument(
                    now,
                    effectiveSource,
                    deduplicated,
                    sources,
                    updatedManualTimestamp,
                    CurrentSchemaVersion,
                    statusArray));
                return new HotfixScanResult(deduplicated, Checked: true, FromCache: fromCache, effectiveSource, statusArray, now);
            }

            // All sources failed or were skipped due to active rate-limit blocks: keep stale cache and persist blocks/statuses
            SaveCache(cachePath, new HotfixFeedCacheDocument(
                cache?.FetchedAtUtc,
                cache?.SourceName,
                cachedEntries,
                sources,
                updatedManualTimestamp,
                CurrentSchemaVersion,
                statusArray));
            return new HotfixScanResult(
                cachedEntries,
                Checked: false,
                FromCache: cachedEntries.Length > 0,
                cache?.SourceName,
                statusArray,
                cache?.FetchedAtUtc ?? now);
        }
        finally
        {
            gate.Release();
        }
    }

    public static Task<string?> ProbeCdnInstallerUrlAsync(
        HttpClient client,
        Version version,
        bool isNotebook,
        CancellationToken token = default) =>
        ProbeCdnInstallerUrlInternalAsync(
            client,
            version,
            isNotebook,
            new Dictionary<string, HotfixSourceState>(StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow,
            token);

    private static async Task<string?> ProbeCdnInstallerUrlInternalAsync(
        HttpClient client,
        Version version,
        bool isNotebook,
        Dictionary<string, HotfixSourceState> sources,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (sources.TryGetValue("nvidia-cdn", out var cdnState) &&
            cdnState.BlockedUntilUtc is { } blockedUntil &&
            blockedUntil > now)
            return null;

        var ver = NvidiaDriverLookup.Display(version);
        var candidates = isNotebook
            ? new[]
            {
                $"https://international.download.nvidia.com/Windows/{ver}hf/{ver}-desktop-notebook-win10-win11-64bit-international-dch.hf.exe",
                $"https://international.download.nvidia.com/Windows/{ver}hf/{ver}-notebook-win10-win11-64bit-international-dch.hf.exe",
                $"https://international.download.nvidia.com/Windows/{ver}/{ver}-geforce-notebook-win10-win11-64bit-international-dch-hf.exe"
            }
            : new[]
            {
                $"https://international.download.nvidia.com/Windows/{ver}hf/{ver}-desktop-notebook-win10-win11-64bit-international-dch.hf.exe",
                $"https://international.download.nvidia.com/Windows/{ver}hf/{ver}-desktop-win10-win11-64bit-international-dch.hf.exe",
                $"https://international.download.nvidia.com/Windows/{ver}/{ver}-geforce-desktop-win10-win11-64bit-international-dch-hf.exe"
            };

        foreach (var candidateUrl in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(PerSourceTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Head, candidateUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    RecordBlock(sources, "nvidia-cdn", response, now);
                    return null;
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
                        return candidateUrl;
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException ||
                                      (e is OperationCanceledException && !token.IsCancellationRequested))
            {
            }
        }

        return null;
    }

    private static async Task<DriverUpdateInfo[]> ResolveCustHelpCandidatesAsync(
        HttpClient client,
        string listHtml,
        bool isNotebook,
        Dictionary<string, HotfixSourceState> sources,
        DateTimeOffset now,
        CancellationToken token)
    {
        var candidates = NvidiaDriverLookup.CustHelpHotfixLink().Matches(listHtml)
            .Select(match =>
            {
                var title = NvidiaDriverLookup.CleanReleaseNotes(match.Groups[2].Value, preferBulletList: false, maxLength: 240) ?? "";
                var version = NvidiaDriverLookup.CustHelpHotfixTitle().Match(title);
                if (!version.Success)
                    version = HotfixBeforeVersionRegex().Match(title);
                if (!version.Success)
                    version = VersionBeforeHotfixRegex().Match(title);
                return version.Success && Version.TryParse(version.Groups[1].Value, out var parsed)
                    ? (Version: parsed, Url: "https://nvidia.custhelp.com/app/answers/detail/a_id/" + match.Groups[1].Value)
                    : default;
            })
            .Where(item => item.Version is not null)
            .OrderByDescending(item => item.Version)
            .Take(8)
            .ToArray();

        var results = new List<DriverUpdateInfo>();
        foreach (var candidate in candidates)
        {
            if (sources.TryGetValue("nvidia-custhelp", out var state) &&
                state.BlockedUntilUtc is { } blocked &&
                blocked > now)
                break;

            var detailOutcome = await FetchSourceAsync(
                client,
                "nvidia-custhelp-detail:" + candidate.Url,
                candidate.Url,
                NvidiaDriverLookup.IsNvidiaWebHost,
                sources,
                now,
                token,
                blockKeyOverride: "nvidia-custhelp").ConfigureAwait(false);

            if (!detailOutcome.Succeeded || string.IsNullOrWhiteSpace(detailOutcome.Content))
                continue;

            var displayVersion = NvidiaDriverLookup.Display(candidate.Version!);
            if (!detailOutcome.Content.Contains("download.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
                !detailOutcome.Content.Contains(displayVersion, StringComparison.Ordinal))
                continue;

            var directUrl = NvidiaDriverLookup.ExtractDirectInstallerUrl(detailOutcome.Content, isNotebook);
            var summary = NvidiaDriverLookup.CleanReleaseNotes(detailOutcome.Content, preferBulletList: true);
            results.Add(new DriverUpdateInfo(
                NvidiaDriverType.Hotfix,
                displayVersion,
                candidate.Version!,
                directUrl,
                candidate.Url,
                summary));
        }

        return [.. results];
    }

    public static DriverUpdateInfo[] ParseNvidiaForumHotfixes(string html, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var results = new List<DriverUpdateInfo>();

        // Pass 1: Embedded JSON / SPA state (e.g. "title": "GeForce Hotfix Display Driver version 621.45")
        foreach (Match jsonMatch in EmbeddedJsonHotfixTitleRegex().Matches(html))
        {
            var rawTitle = DecodeJsonStringFragment(jsonMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(rawTitle) || !rawTitle.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryExtractHotfixVersion(rawTitle, allowFallbackOnShortText: true, out var parsedVersion))
                continue;

            var winStart = Math.Max(0, jsonMatch.Index - 200);
            var winLen = Math.Min(html.Length - winStart, jsonMatch.Length + 600);
            var window = html.Substring(winStart, winLen);

            var releaseUrl = ExtractJsonReleaseLink(window, "https://www.nvidia.com")
                ?? ExtractFirstReleaseLink(window, "https://www.nvidia.com");
            var directUrl = NvidiaDriverLookup.ExtractDirectInstallerUrl(window, isNotebook);
            var releaseDate = ExtractReleaseDate(window);
            var displayVersion = NvidiaDriverLookup.Display(parsedVersion!);

            results.Add(new DriverUpdateInfo(
                NvidiaDriverType.Hotfix,
                displayVersion,
                parsedVersion!,
                directUrl,
                releaseUrl,
                rawTitle.Trim(),
                ReleaseDate: releaseDate));
        }

        // Pass 2: HTML anchors (captures sticky/pinned announcement threads and regular threads regardless of nested divs)
        foreach (Match linkMatch in AnchorTagRegex().Matches(html))
        {
            var anchorText = NvidiaDriverLookup.CleanReleaseNotes(linkMatch.Groups[2].Value, preferBulletList: false, maxLength: 260) ?? "";
            var winStart = Math.Max(0, linkMatch.Index - 100);
            var winLen = Math.Min(html.Length - winStart, linkMatch.Length + 500);
            var window = html.Substring(winStart, winLen);

            if (!anchorText.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) &&
                !HotfixMarkerRegex().IsMatch(window))
                continue;

            var anchorHref = NormalizeCandidateLink(linkMatch.Groups[1].Value, "https://www.nvidia.com");
            if (TryBuildHotfixFromSnippet(anchorText, "https://www.nvidia.com", out var anchorEntry, isNotebook, fallbackLink: anchorHref))
            {
                if (TryBuildHotfixFromSnippet(window, "https://www.nvidia.com", out var windowEntry, isNotebook, fallbackLink: anchorHref) &&
                    windowEntry!.ParsedVersion == anchorEntry!.ParsedVersion)
                {
                    results.Add(anchorEntry with
                    {
                        DirectInstallerUrl = anchorEntry.DirectInstallerUrl ?? windowEntry.DirectInstallerUrl,
                        ReleaseDate = anchorEntry.ReleaseDate ?? windowEntry.ReleaseDate,
                        ReleaseSummary = PreferSummary(anchorEntry.ReleaseSummary, windowEntry.ReleaseSummary)
                    });
                }
                else
                {
                    results.Add(anchorEntry!);
                }
            }
            else if (TryBuildHotfixFromSnippet(window, "https://www.nvidia.com", out var winEntry, isNotebook, fallbackLink: anchorHref))
            {
                results.Add(winEntry!);
            }
        }

        // Pass 3: Focused window around every hotfix marker (not gated by earlier hits)
        foreach (Match anchor in HotfixMarkerRegex().Matches(html))
        {
            var start = Math.Max(0, anchor.Index - 180);
            var length = Math.Min(html.Length - start, 1080);
            var snippet = html.Substring(start, length);
            if (TryBuildHotfixFromSnippet(snippet, "https://www.nvidia.com", out var entry, isNotebook))
                results.Add(entry!);
        }

        return DeduplicateHotfixes(results);
    }

    public static DriverUpdateInfo[] ParseCatalogHotfixes(string html, string baseOrigin, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];

        var results = new List<DriverUpdateInfo>();

        // Pass 1: Anchors mentioning Hotfix directly
        foreach (Match linkMatch in AnchorTagRegex().Matches(html))
        {
            var anchorText = NvidiaDriverLookup.CleanReleaseNotes(linkMatch.Groups[2].Value, preferBulletList: false, maxLength: 260) ?? "";
            if (!anchorText.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                continue;

            var anchorHref = NormalizeCandidateLink(linkMatch.Groups[1].Value, baseOrigin);
            var winStart = Math.Max(0, linkMatch.Index - 120);
            var winLen = Math.Min(html.Length - winStart, linkMatch.Length + 600);
            var window = html.Substring(winStart, winLen);

            if (TryBuildHotfixFromSnippet(anchorText, baseOrigin, out var anchorEntry, isNotebook, fallbackLink: anchorHref))
            {
                if (TryBuildHotfixFromSnippet(window, baseOrigin, out var windowEntry, isNotebook, fallbackLink: anchorHref) &&
                    windowEntry!.ParsedVersion == anchorEntry!.ParsedVersion)
                {
                    results.Add(anchorEntry with
                    {
                        DirectInstallerUrl = anchorEntry.DirectInstallerUrl ?? windowEntry.DirectInstallerUrl,
                        ReleaseDate = anchorEntry.ReleaseDate ?? windowEntry.ReleaseDate,
                        ReleaseSummary = PreferSummary(anchorEntry.ReleaseSummary, windowEntry.ReleaseSummary)
                    });
                }
                else
                {
                    results.Add(anchorEntry!);
                }
            }
            else if (TryBuildHotfixFromSnippet(window, baseOrigin, out var windowEntry, isNotebook, fallbackLink: anchorHref))
            {
                results.Add(windowEntry!);
            }
        }

        // Pass 2: Headings mentioning Hotfix
        foreach (Match headingMatch in HeadingTagRegex().Matches(html))
        {
            var headingText = NvidiaDriverLookup.CleanReleaseNotes(headingMatch.Groups[1].Value, preferBulletList: false, maxLength: 260) ?? "";
            if (!headingText.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                continue;

            var winStart = Math.Max(0, headingMatch.Index - 150);
            var winLen = Math.Min(html.Length - winStart, headingMatch.Length + 750);
            var window = html.Substring(winStart, winLen);
            if (TryBuildHotfixFromSnippet(headingText, baseOrigin, out var headingEntry, isNotebook))
            {
                if (TryBuildHotfixFromSnippet(window, baseOrigin, out var windowEntry, isNotebook) &&
                    windowEntry!.ParsedVersion == headingEntry!.ParsedVersion)
                {
                    results.Add(headingEntry with
                    {
                        DirectInstallerUrl = headingEntry.DirectInstallerUrl ?? windowEntry.DirectInstallerUrl,
                        ReleasePageUrl = PreferReleaseUrl(headingEntry.ReleasePageUrl, windowEntry.ReleasePageUrl),
                        ReleaseDate = headingEntry.ReleaseDate ?? windowEntry.ReleaseDate,
                        ReleaseSummary = PreferSummary(headingEntry.ReleaseSummary, windowEntry.ReleaseSummary)
                    });
                }
                else
                {
                    results.Add(headingEntry!);
                }
            }
            else if (TryBuildHotfixFromSnippet(window, baseOrigin, out var winEntry, isNotebook))
            {
                results.Add(winEntry!);
            }
        }

        // Pass 3: Non-nested tr / article / li blocks containing Hotfix
        foreach (Match block in CatalogBlockRegex().Matches(html))
        {
            var chunk = block.Value;
            if (!chunk.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryBuildHotfixFromSnippet(chunk, baseOrigin, out var entry, isNotebook))
                results.Add(entry!);
        }

        // Pass 4: Every "Hotfix" occurrence (catches badge/sibling layouts across the entire driver list)
        foreach (Match marker in HotfixWordRegex().Matches(html))
        {
            var start = Math.Max(0, marker.Index - 450);
            var length = Math.Min(html.Length - start, 1350);
            var snippet = html.Substring(start, length);
            if (!snippet.Contains("GeForce", StringComparison.OrdinalIgnoreCase) &&
                !snippet.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
                !snippet.Contains("driver", StringComparison.OrdinalIgnoreCase))
                continue;
            if (TryBuildHotfixFromSnippet(snippet, baseOrigin, out var entry, isNotebook))
                results.Add(entry!);
        }

        return DeduplicateHotfixes(results);
    }

    public static HotfixResolutionResult ResolveHotfixes(IEnumerable<DriverUpdateInfo> discovered, Version installedVersion)
    {
        var sorted = DeduplicateHotfixes(discovered);
        var latest = sorted.FirstOrDefault(d => d.ParsedVersion > installedVersion);
        var previous = sorted.FirstOrDefault(d => d.ParsedVersion < installedVersion);
        return new HotfixResolutionResult(latest, previous, sorted);
    }

    public static DriverUpdateInfo[] ParseStructuredFeedHotfixes(string content, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return ParseJsonFeedHotfixes(trimmed, isNotebook);
        if (trimmed.StartsWith('<'))
            return ParseRssOrAtomHotfixes(trimmed, isNotebook);
        return [];
    }

    public static DriverUpdateInfo[] ParseJsonFeedHotfixes(string json, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement array;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                array = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (!TryGetPropertyIgnoreCase(doc.RootElement, out array, "hotfixes", "drivers", "entries", "items") ||
                    array.ValueKind != JsonValueKind.Array)
                    return [];
            }
            else
            {
                return [];
            }

            var results = new List<DriverUpdateInfo>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var rawVersion = GetStringProperty(item, "version");
                var title = GetStringProperty(item, "title", "name");
                Version? parsedVersion = null;
                if (!string.IsNullOrWhiteSpace(rawVersion))
                {
                    var match = StrictDriverVersion().Match(rawVersion);
                    if (match.Success)
                        Version.TryParse(match.Groups[1].Value, out parsedVersion);
                }
                if (parsedVersion is null && !string.IsNullOrWhiteSpace(title))
                    TryExtractHotfixVersion(title, allowFallbackOnShortText: true, out parsedVersion);
                if (parsedVersion is null)
                    continue;

                var displayVersion = NvidiaDriverLookup.Display(parsedVersion);
                var directUrl = GetStringProperty(
                    item,
                    isNotebook ? "notebookUrl" : "directUrl",
                    isNotebook ? "directUrl" : "desktopOnlyUrl",
                    isNotebook ? "directInstallerUrl" : "desktopUrl",
                    "directInstallerUrl",
                    "downloadUrl");
                var releasePageUrl = GetStringProperty(item, "pageUrl", "releasePageUrl", "forumUrl", "url", "link");
                var rawDate = GetStringProperty(item, "releaseDate", "date", "publishedAt");
                var releaseDate = NormalizeReleaseDate(rawDate);
                var summary = GetStringProperty(item, "summary", "releaseSummary", "notes", "description", "changes") ?? title;

                results.Add(new DriverUpdateInfo(
                    NvidiaDriverType.Hotfix,
                    displayVersion,
                    parsedVersion,
                    directUrl,
                    releasePageUrl,
                    summary,
                    ReleaseDate: releaseDate));
            }

            return DeduplicateHotfixes(results);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static DriverUpdateInfo[] ParseRssOrAtomHotfixes(string xml, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var stringReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(stringReader, settings);
            var doc = XDocument.Load(xmlReader, LoadOptions.None);
            if (doc.Root is null) return [];

            var results = new List<DriverUpdateInfo>();
            foreach (var item in doc.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
            {
                var title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value?.Trim() ?? "";
                var description = item.Elements().FirstOrDefault(e => e.Name.LocalName is "description" or "summary" or "content")?.Value?.Trim() ?? "";
                var combined = string.IsNullOrWhiteSpace(description) ? title : $"{title}\n{description}";

                if (!combined.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
                    continue;

                Version? parsedVersion = null;
                if (!string.IsNullOrWhiteSpace(title))
                    TryExtractHotfixVersion(title, allowFallbackOnShortText: true, out parsedVersion);
                if (parsedVersion is null)
                    TryExtractHotfixVersion(combined, allowFallbackOnShortText: false, out parsedVersion);
                if (parsedVersion is null)
                    continue;

                var linkElem = item.Elements().FirstOrDefault(e => e.Name.LocalName == "link");
                var rawLink = linkElem?.Attribute("href")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(rawLink))
                    rawLink = linkElem?.Value?.Trim();

                var rawDate = item.Elements().FirstOrDefault(e => e.Name.LocalName is "pubDate" or "updated" or "published" or "date")?.Value?.Trim();
                var releaseDate = NormalizeReleaseDate(rawDate);
                var directUrl = NvidiaDriverLookup.ExtractDirectInstallerUrl(combined, isNotebook);
                var cleanSummary = NvidiaDriverLookup.CleanReleaseNotes(
                    string.IsNullOrWhiteSpace(description) ? title : description,
                    preferBulletList: true,
                    maxLength: 1200) ?? title;

                results.Add(new DriverUpdateInfo(
                    NvidiaDriverType.Hotfix,
                    NvidiaDriverLookup.Display(parsedVersion),
                    parsedVersion,
                    directUrl,
                    rawLink,
                    cleanSummary,
                    ReleaseDate: releaseDate));
            }

            return DeduplicateHotfixes(results);
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException)
        {
            return [];
        }
    }

    public static DriverUpdateInfo[] ParseGoogleCustomSearchHotfixes(string json, bool isNotebook = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGetPropertyIgnoreCase(doc.RootElement, out var items, "items") ||
                items.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<DriverUpdateInfo>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var title = GetStringProperty(item, "title") ?? "";
                var link = GetStringProperty(item, "link") ?? "";
                var snippet = GetStringProperty(item, "snippet") ?? "";
                var combined = $"{title}\n{snippet}";

                if (!combined.Contains("Hotfix", StringComparison.OrdinalIgnoreCase) &&
                    !link.Contains("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryExtractHotfixVersion(combined, allowFallbackOnShortText: true, out var parsedVersion) || parsedVersion is null)
                    continue;

                var releaseDate = ExtractReleaseDate(combined);
                var directUrl = NvidiaDriverLookup.ExtractDirectInstallerUrl(combined, isNotebook);
                var cleanSummary = NvidiaDriverLookup.CleanReleaseNotes(
                    string.IsNullOrWhiteSpace(snippet) ? title : snippet,
                    preferBulletList: true,
                    maxLength: 1200) ?? title;

                results.Add(new DriverUpdateInfo(
                    NvidiaDriverType.Hotfix,
                    NvidiaDriverLookup.Display(parsedVersion),
                    parsedVersion,
                    directUrl,
                    link,
                    cleanSummary,
                    ReleaseDate: releaseDate));
            }

            return DeduplicateHotfixes(results);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, out JsonElement value, params string[] names)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetStringProperty(JsonElement obj, params string[] names) =>
        TryGetPropertyIgnoreCase(obj, out var prop, names) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? NormalizeReleaseDate(string? rawDate)
    {
        if (string.IsNullOrWhiteSpace(rawDate)) return null;
        if (DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return ExtractReleaseDate(rawDate) ?? rawDate.Trim();
    }

    private static bool LooksLikeXml(string content) =>
        content.TrimStart().StartsWith('<') &&
        (content.Contains("<rss", StringComparison.OrdinalIgnoreCase) ||
         content.Contains("<feed", StringComparison.OrdinalIgnoreCase) ||
         content.Contains("<?xml", StringComparison.OrdinalIgnoreCase));

    private static (string Url, bool IsDefault) ResolveStructuredFeedUrl(string? customFeedUrl)
    {
        if (!string.IsNullOrWhiteSpace(customFeedUrl) &&
            Uri.TryCreate(customFeedUrl.Trim(), UriKind.Absolute, out var uri) &&
            uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } &&
            NvidiaDriverLookup.IsSafeReleaseWebHost(uri))
        {
            return (uri.AbsoluteUri, false);
        }

        return (DefaultGitHubHotfixFeedUrl, true);
    }

    private static bool TryBuildHotfixFromSnippet(
        string snippet,
        string baseOrigin,
        out DriverUpdateInfo? entry,
        bool isNotebook = false,
        string? fallbackLink = null)
    {
        entry = null;
        if (!snippet.Contains("Hotfix", StringComparison.OrdinalIgnoreCase))
            return false;

        var fullText = NvidiaDriverLookup.CleanReleaseNotes(snippet, preferBulletList: false, maxLength: 1600);
        var searchSpace = fullText ?? snippet;
        if (!TryExtractHotfixVersion(searchSpace, allowFallbackOnShortText: searchSpace.Length <= 280, out var parsedVersion))
            return false;

        var displayVersion = NvidiaDriverLookup.Display(parsedVersion!);
        var directUrl = NvidiaDriverLookup.ExtractDirectInstallerUrl(snippet, isNotebook);
        var releaseUrl = ExtractFirstReleaseLink(snippet, baseOrigin) ?? fallbackLink;
        var releaseDate = ExtractReleaseDate(searchSpace);
        var summary = NvidiaDriverLookup.CleanReleaseNotes(snippet, preferBulletList: true, maxLength: 1200) ?? fullText;

        entry = new DriverUpdateInfo(
            NvidiaDriverType.Hotfix,
            displayVersion,
            parsedVersion!,
            directUrl,
            releaseUrl,
            summary,
            ReleaseDate: releaseDate);
        return true;
    }

    private static bool TryExtractHotfixVersion(string text, bool allowFallbackOnShortText, out Version? parsedVersion)
    {
        parsedVersion = null;
        var match = HotfixBeforeVersionRegex().Match(text);
        if (!match.Success)
            match = VersionBeforeHotfixRegex().Match(text);
        if (!match.Success && allowFallbackOnShortText)
            match = StrictDriverVersion().Match(text);

        return match.Success && Version.TryParse(match.Groups[1].Value, out parsedVersion);
    }

    private static string? NormalizeCandidateLink(string? rawHref, string baseOrigin)
    {
        if (string.IsNullOrWhiteSpace(rawHref)) return null;
        var raw = WebUtility.HtmlDecode(rawHref).Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
            return null;

        if (raw.StartsWith("//", StringComparison.Ordinal))
            raw = "https:" + raw;
        else if (raw.StartsWith('/'))
            raw = baseOrigin.TrimEnd('/') + raw;

        return NvidiaDriverLookup.TryNormalizeReleasePageUrl(raw);
    }

    private static string? ExtractJsonReleaseLink(string window, string baseOrigin)
    {
        foreach (Match match in EmbeddedJsonUrlRegex().Matches(window))
        {
            var raw = DecodeJsonStringFragment(match.Groups[1].Value);
            if (NormalizeCandidateLink(raw, baseOrigin) is { } valid)
                return valid;
        }
        return null;
    }

    private static string DecodeJsonStringFragment(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        try
        {
            return JsonSerializer.Deserialize<string>("\"" + raw + "\"") ?? raw;
        }
        catch (JsonException)
        {
            return raw.Replace("\\/", "/", StringComparison.Ordinal)
                      .Replace("\\\"", "\"", StringComparison.Ordinal);
        }
    }

    private static string? ExtractFirstReleaseLink(string snippet, string baseOrigin)
    {
        foreach (Match match in HrefLinkRegex().Matches(snippet))
        {
            var raw = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#'))
                continue;

            if (raw.StartsWith("//", StringComparison.Ordinal))
                raw = "https:" + raw;
            else if (raw.StartsWith('/'))
                raw = baseOrigin.TrimEnd('/') + raw;

            if (NvidiaDriverLookup.TryNormalizeReleasePageUrl(raw) is { } valid)
                return valid;
        }
        return null;
    }

    private static string? ExtractReleaseDate(string text)
    {
        var match = ReleaseDateRegex().Match(text);
        if (!match.Success) return null;
        if (DateTimeOffset.TryParse(match.Value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return match.Value.Trim();
    }

    public static DriverUpdateInfo[] DeduplicateHotfixes(IEnumerable<DriverUpdateInfo> items)
    {
        var map = new Dictionary<string, DriverUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in items)
        {
            if (raw.ParsedVersion is null) continue;
            var display = NvidiaDriverLookup.Display(raw.ParsedVersion);
            var cleaned = raw with
            {
                DriverType = NvidiaDriverType.Hotfix,
                Version = display,
                ReleaseSummary = NvidiaDriverLookup.CleanReleaseNotes(raw.ReleaseSummary, preferBulletList: true),
                DirectInstallerUrl = NvidiaDriverLookup.TryNormalizeInstallerUrl(raw.DirectInstallerUrl),
                ReleasePageUrl = NvidiaDriverLookup.TryNormalizeReleasePageUrl(raw.ReleasePageUrl)
            };

            if (!map.TryGetValue(display, out var existing))
            {
                map[display] = cleaned;
                continue;
            }

            map[display] = existing with
            {
                DirectInstallerUrl = existing.DirectInstallerUrl ?? cleaned.DirectInstallerUrl,
                ReleasePageUrl = PreferReleaseUrl(existing.ReleasePageUrl, cleaned.ReleasePageUrl),
                ReleaseSummary = PreferSummary(existing.ReleaseSummary, cleaned.ReleaseSummary),
                ReleaseDate = existing.ReleaseDate ?? cleaned.ReleaseDate,
                IsCached = existing.IsCached || cleaned.IsCached
            };
        }

        return [.. map.Values.OrderByDescending(d => d.ParsedVersion)];
    }

    private static string? PreferReleaseUrl(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(current)) return candidate;
        if (string.IsNullOrWhiteSpace(candidate)) return current;
        return current.Contains("nvidia.com", StringComparison.OrdinalIgnoreCase) ? current :
            candidate.Contains("nvidia.com", StringComparison.OrdinalIgnoreCase) ? candidate : current;
    }

    private static string? PreferSummary(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(current)) return candidate;
        if (string.IsNullOrWhiteSpace(candidate)) return current;
        return candidate.Length > current.Length ? candidate : current;
    }

    private static async Task<FetchOutcome> FetchSourceAsync(
        HttpClient client,
        string sourceKey,
        string url,
        Func<Uri, bool> allowedHost,
        Dictionary<string, HotfixSourceState> sources,
        DateTimeOffset now,
        CancellationToken token,
        string? blockKeyOverride = null,
        string? acceptHeader = null)
    {
        var effectiveBlockKey = blockKeyOverride ?? sourceKey;
        if (sources.TryGetValue(effectiveBlockKey, out var blockState) &&
            blockState.BlockedUntilUtc is { } blockedUntil &&
            blockedUntil > now)
        {
            return new FetchOutcome(Skipped: true, NotModified: false, Succeeded: false, Blocked: true, Content: null);
        }

        sources.TryGetValue(sourceKey, out var state);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(PerSourceTimeout);

            var current = new Uri(url);
            Uri? referer = null;
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (current is not { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } || !allowedHost(current))
                    return new FetchOutcome(Skipped: false, NotModified: false, Succeeded: false, Blocked: false, Content: null);

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
                request.Headers.TryAddWithoutValidation("Accept", acceptHeader ?? HtmlAcceptHeader);
                if (referer is not null)
                    request.Headers.Referrer = referer;

                if (hop == 0 && state is not null)
                {
                    if (!string.IsNullOrWhiteSpace(state.ETag))
                        request.Headers.TryAddWithoutValidation("If-None-Match", state.ETag);
                    if (state.LastModifiedUtc is { } lastMod)
                        request.Headers.IfModifiedSince = lastMod;
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    referer ??= current;
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotModified)
                    return new FetchOutcome(Skipped: false, NotModified: true, Succeeded: true, Blocked: false, Content: null, StatusCode: response.StatusCode);

                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    RecordBlock(sources, effectiveBlockKey, response, now);
                    return new FetchOutcome(Skipped: false, NotModified: false, Succeeded: false, Blocked: true, Content: null, StatusCode: response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                    return new FetchOutcome(Skipped: false, NotModified: false, Succeeded: false, Blocked: false, Content: null, StatusCode: response.StatusCode);

                var etag = response.Headers.ETag?.ToString();
                var lastModified = response.Content.Headers.LastModified;
                sources[sourceKey] = (state ?? new HotfixSourceState()) with
                {
                    BlockedUntilUtc = null,
                    ETag = !string.IsNullOrWhiteSpace(etag) ? etag : state?.ETag,
                    LastModifiedUtc = lastModified ?? state?.LastModifiedUtc
                };

                var content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                return new FetchOutcome(Skipped: false, NotModified: false, Succeeded: true, Blocked: false, Content: content, StatusCode: response.StatusCode);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException ||
                                  (e is OperationCanceledException && !token.IsCancellationRequested))
        {
        }

        return new FetchOutcome(Skipped: false, NotModified: false, Succeeded: false, Blocked: false, Content: null);
    }

    private static void UpdateCachedSourceEntries(
        Dictionary<string, HotfixSourceState> sources,
        string sourceKey,
        DriverUpdateInfo[] entries)
    {
        sources.TryGetValue(sourceKey, out var existing);
        sources[sourceKey] = (existing ?? new HotfixSourceState()) with
        {
            BlockedUntilUtc = null,
            CachedEntries = entries
        };
    }

    private static DriverUpdateInfo[] GetCachedSourceEntries(
        Dictionary<string, HotfixSourceState> sources,
        string sourceKey,
        HotfixFeedCacheDocument? cache,
        DriverUpdateInfo[] fallbackAllEntries)
    {
        if (sources.TryGetValue(sourceKey, out var state) && state.CachedEntries is { } perSource)
            return SanitizeEntries(perSource);

        if (string.Equals(cache?.SourceName, sourceKey, StringComparison.OrdinalIgnoreCase))
            return fallbackAllEntries;

        return [];
    }

    private static void RecordBlock(
        Dictionary<string, HotfixSourceState> sources,
        string sourceKey,
        HttpResponseMessage response,
        DateTimeOffset now)
    {
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta)
                retryAfter = delta;
            else if (header.Date is { } date)
                retryAfter = date - now;
        }

        var duration = retryAfter is { } parsed && parsed > MinBlockDuration
            ? parsed
            : MinBlockDuration;

        sources.TryGetValue(sourceKey, out var existing);
        sources[sourceKey] = (existing ?? new HotfixSourceState()) with
        {
            BlockedUntilUtc = now + duration
        };
    }

    private static DriverUpdateInfo[] SanitizeEntries(DriverUpdateInfo[]? entries) =>
        entries is { Length: > 0 } ? DeduplicateHotfixes(entries) : [];

    private static HotfixFeedCacheDocument? LoadCache(string? cachePath)
    {
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
            return null;

        try
        {
            SafePaths.RejectLinks(cachePath);
            var json = File.ReadAllText(cachePath);
            var doc = JsonSerializer.Deserialize<HotfixFeedCacheDocument>(json);
            return doc is null || doc.SchemaVersion != CurrentSchemaVersion ? null : doc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void SaveCache(string? cachePath, HotfixFeedCacheDocument document)
    {
        if (string.IsNullOrWhiteSpace(cachePath))
            return;

        try
        {
            var full = SafePaths.Root(cachePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            DurableFiles.Json(full, document, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsNvidiaForumHost(Uri uri) =>
        uri.IdnHost.Equals("www.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsTechPowerUpHost(Uri uri) =>
        uri.IdnHost.Equals("www.techpowerup.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("techpowerup.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuru3DHost(Uri uri) =>
        uri.IdnHost.Equals("www.guru3d.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("guru3d.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("feeds.guru3d.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoogleNewsHost(Uri uri) =>
        uri.IdnHost.Equals("news.google.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGoogleApiHost(Uri uri) =>
        uri.IdnHost.Equals("www.googleapis.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"GeForce\s+Hotfix|Hotfix\s+Display\s+Driver", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixMarkerRegex();

    [GeneratedRegex(@"<(?:tr|article|li)\b[^>]*>(?:(?!<(?:tr|article|li)\b).)*?</(?:tr|article|li)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CatalogBlockRegex();

    [GeneratedRegex(@"<h[1-6]\b[^>]*>(.*?)</h[1-6]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadingTagRegex();

    [GeneratedRegex(@"<a\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex AnchorTagRegex();

    [GeneratedRegex(@"""(?:title|subject|name|headline)""\s*:\s*""([^""]*Hotfix[^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedJsonHotfixTitleRegex();

    [GeneratedRegex(@"""(?:url|link|slug|permalink|canonicalUrl)""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedJsonUrlRegex();

    [GeneratedRegex(@"\bHotfix\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixWordRegex();

    [GeneratedRegex(@"(?:GeForce\s+(?:Game\s+Ready\s+)?Hotfix|Hotfix(?:\s+Display)?(?:\s+Driver)?(?:\s+version|\s+v)?)\D{0,40}\b(\d{3}\.\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixBeforeVersionRegex();

    [GeneratedRegex(@"\b(\d{3}\.\d{2})\b(?:\s*(?:WHQL|Game\s+Ready|Beta|Driver|GeForce))?\s*[-–—:/|(\[]*\s*Hotfix\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionBeforeHotfixRegex();

    [GeneratedRegex(@"\b(\d{3}\.\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex StrictDriverVersion();

    [GeneratedRegex("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefLinkRegex();

    [GeneratedRegex(@"\b(?:\d{2}\.\d{2}\.\d{4}|\d{4}-\d{2}-\d{2}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseDateRegex();
}
