using static DLSSManager.Core.LocalizationService;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSSManager.Core;

public enum NvidiaDriverType
{
    WHQL,
    Hotfix
}

public sealed record DriverUpdateInfo(
    NvidiaDriverType DriverType,
    string Version,
    Version ParsedVersion,
    string? DirectInstallerUrl = null,
    string? ReleasePageUrl = null,
    string? ReleaseSummary = null,
    bool IsCached = false,
    string? ReleaseDate = null)
{
    public bool IsHotfix => DriverType == NvidiaDriverType.Hotfix;
    public string? HotfixPageUrl => IsHotfix ? ReleasePageUrl : null;
}

public sealed record PreviousDriverOptions(
    DriverUpdateInfo? PreviousWhqlDriver = null,
    DriverUpdateInfo? PreviousHotfixDriver = null,
    DriverUpdateInfo[]? AllPreviousDrivers = null)
{
    public DriverUpdateInfo[] Drivers => AllPreviousDrivers ??
        new[] { PreviousHotfixDriver, PreviousWhqlDriver }
            .OfType<DriverUpdateInfo>()
            .OrderByDescending(d => d.ParsedVersion)
            .ThenByDescending(d => d.IsHotfix)
            .ToArray();

    public bool HasAny => PreferredDriver is not null;

    // Prefers the latest Hotfix if newer than or equal to the WHQL predecessor, otherwise the latest WHQL predecessor.
    public DriverUpdateInfo? PreferredDriver => (PreviousHotfixDriver, PreviousWhqlDriver) switch
    {
        ({ } hf, { } whql) => hf.ParsedVersion >= whql.ParsedVersion ? hf : whql,
        ({ } hf, null) => hf,
        (null, { } whql) => whql,
        _ => Drivers.FirstOrDefault()
    };
}

public sealed record DriverHistoryInfo(
    DriverUpdateInfo? LatestWhqlDriver,
    DriverUpdateInfo? LatestHotfixDriver,
    PreviousDriverOptions PreviousDrivers,
    bool HotfixChecked,
    DriverUpdateInfo[]? AllDrivers = null,
    HotfixSourceStatus[]? HotfixSourceStatuses = null,
    DateTimeOffset? HotfixLastScanUtc = null,
    bool HotfixFromCache = false,
    HotfixResolutionResult? HotfixResolution = null)
{
    public DriverUpdateInfo? AvailableUpdate(Version installedVersion) =>
        LatestHotfixDriver is { } hf && hf.ParsedVersion > installedVersion
            ? hf
            : LatestWhqlDriver is { } whql && whql.ParsedVersion > installedVersion
                ? whql
                : null;
}

public sealed record DriverDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public int? Percent => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes.Value, 0L, 100L)
        : null;
}

public static partial class NvidiaDriverLookup
{
    public const string DriverPage = "https://www.nvidia.com/en-us/drivers/";
    private const string Service = "https://gfwsl.geforce.com";
    private const string SupportPage = "https://nvidia.custhelp.com";
    public const string HotfixListUrl = SupportPage + "/app/answers/list/kw/Hotfix";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DLSSManager/1.0";
    private const int MaxRedirects = 5;

    public static Version? GeForceVersion(string windowsVersion)
    {
        if (!Version.TryParse(windowsVersion, out var version) || version.Build < 0 || version.Revision is < 0 or > 9999)
            return null;
        var number = version.Build % 10 * 10000 + version.Revision;
        return new(number / 100, number % 100);
    }

    public static string Display(Version version) => $"{version.Major}.{version.Minor:D2}";

    public static bool IsNotebookGpu(string? gpuName) =>
        gpuName is not null &&
        (gpuName.Contains("Laptop", StringComparison.OrdinalIgnoreCase) ||
         gpuName.Contains("Notebook", StringComparison.OrdinalIgnoreCase));

    public static async Task<DriverUpdateInfo?> WhqlAsync(
        HttpClient client,
        string gpuName,
        CancellationToken token = default,
        int? osIdOverride = null) =>
        (await WhqlDriversAsync(client, gpuName, maxResults: 1, token, osIdOverride).ConfigureAwait(false))
            .FirstOrDefault();

    public static async Task<DriverUpdateInfo[]> WhqlDriversAsync(
        HttpClient client,
        string gpuName,
        int maxResults = 10,
        CancellationToken token = default,
        int? osIdOverride = null)
    {
        var model = gpuName.Replace("NVIDIA ", "", StringComparison.OrdinalIgnoreCase).Trim();
        var match = Series().Match(model);
        if (!match.Success) return [];

        var prefix = match.Groups[1].Value;
        var seriesName = $"GeForce {prefix} Series";
        if (IsNotebookGpu(gpuName))
            seriesName += " (Notebooks)";

        using var menu = await MenuAsync(client, null, token).ConfigureAwait(false);
        var series = Find(menu.RootElement[1], seriesName);
        if (series is null) return [];

        using var products = await MenuAsync(client, series, token).ConfigureAwait(false);
        var product = Find(products.RootElement[2], model);
        if (product is null) return [];

        var os = osIdOverride ?? (Environment.OSVersion.Version.Build >= 22000 ? 135 : 57);
        var count = Math.Clamp(maxResults, 1, 15);
        var url = $"{Service}/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup" +
            $"&psid={series}&pfid={product}&osID={os}&languageCode=1033&isWHQL=1&dch=1&upCRD=0&upGRD=1&numberOfResults={count}";
        var payload = await GetStringWithRedirectAsync(client, url, IsNvidiaWebHost, token).ConfigureAwait(false);
        using var data = JsonDocument.Parse(payload);
        if (!data.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<DriverUpdateInfo>();
        var seenVersions = new HashSet<Version>();
        foreach (var result in ids.EnumerateArray())
        {
            if (!result.TryGetProperty("downloadInfo", out var info) || info.ValueKind != JsonValueKind.Object)
                continue;
            if (!info.TryGetProperty("IsWHQL", out var whql) || whql.GetString() != "1")
                continue;
            if (!info.TryGetProperty("Version", out var versionElement) ||
                !Version.TryParse(versionElement.GetString(), out var version) ||
                !seenVersions.Add(version))
                continue;

            var directUrl = info.TryGetProperty("DownloadURL", out var downloadElement)
                ? TryNormalizeInstallerUrl(downloadElement.GetString())
                : null;
            var releasePageUrl = info.TryGetProperty("DetailsURL", out var detailsElement)
                ? TryNormalizeReleasePageUrl(detailsElement.GetString())
                : null;
            if (directUrl is null && releasePageUrl is null)
                continue;

            var rawNotes = info.TryGetProperty("ReleaseNotes", out var releaseNotesElement)
                ? releaseNotesElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(rawNotes) && info.TryGetProperty("OtherNotes", out var otherNotesElement))
                rawNotes = otherNotesElement.GetString();
            var summary = SummarizeHtml(rawNotes, preferBulletList: false);

            var rawDate = info.TryGetProperty("ReleaseDateTime", out var dateElement)
                ? dateElement.GetString()?.Trim()
                : null;
            var releaseDate = string.IsNullOrWhiteSpace(rawDate)
                ? null
                : DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate)
                    ? parsedDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : rawDate;

            results.Add(new DriverUpdateInfo(
                NvidiaDriverType.WHQL,
                Display(version),
                version,
                directUrl,
                releasePageUrl,
                summary,
                ReleaseDate: releaseDate));
        }

        return [.. results.OrderByDescending(item => item.ParsedVersion)];
    }

    public static async Task<(DriverUpdateInfo? Driver, bool Checked)> HotfixAsync(
        HttpClient client,
        Version installed,
        Version? whql,
        string gpuName,
        CancellationToken token = default,
        string? hotfixCachePath = null,
        bool forceHotfixRefresh = false)
    {
        var (latest, _, _, scan, _) = await HotfixHistoryInternalAsync(
            client, installed, whql, gpuName, includePrevious: false, hotfixCachePath, forceHotfixRefresh, bypassHotfixCooldown: false, token).ConfigureAwait(false);
        return (latest, scan.Checked);
    }

    public static async Task<(DriverUpdateInfo? LatestHotfix, DriverUpdateInfo? PreviousHotfix, bool Checked)> HotfixHistoryAsync(
        HttpClient client,
        Version installed,
        Version? latestWhql,
        string gpuName,
        CancellationToken token = default,
        string? hotfixCachePath = null,
        bool forceHotfixRefresh = false)
    {
        var (latest, previousList, _, scan, _) = await HotfixHistoryInternalAsync(
            client, installed, latestWhql, gpuName, includePrevious: true, hotfixCachePath, forceHotfixRefresh, bypassHotfixCooldown: false, token).ConfigureAwait(false);
        return (latest, previousList.FirstOrDefault(), scan.Checked);
    }

    private static async Task<(DriverUpdateInfo? LatestHotfix, DriverUpdateInfo[] PreviousHotfixes, DriverUpdateInfo[] AllHotfixes, HotfixScanResult Scan, HotfixResolutionResult Resolution)> HotfixHistoryInternalAsync(
        HttpClient client,
        Version installed,
        Version? latestWhql,
        string gpuName,
        bool includePrevious,
        string? hotfixCachePath,
        bool forceHotfixRefresh,
        bool bypassHotfixCooldown,
        CancellationToken token,
        HotfixFeedOptions? hotfixFeedOptions = null)
    {
        var isNotebook = IsNotebookGpu(gpuName);
        var scan = await HotfixFeedScanner.ScanAsync(
            client,
            isNotebook,
            hotfixCachePath,
            forceHotfixRefresh,
            bypassCooldown: bypassHotfixCooldown,
            options: hotfixFeedOptions,
            token: token).ConfigureAwait(false);

        var resolution = HotfixFeedScanner.ResolveHotfixes(scan.Drivers, installed);

        var latestHotfix = resolution.LatestHotfix is { } top &&
                           top.ParsedVersion > installed &&
                           (latestWhql is null || top.ParsedVersion > latestWhql)
            ? top
            : null;

        var previousHotfixes = includePrevious
            ? resolution.AllHotfixes.Where(d => d.ParsedVersion < installed).ToArray()
            : [];

        return (latestHotfix, previousHotfixes, resolution.AllHotfixes, scan, resolution);
    }

    public static async Task<DriverHistoryInfo> HistoryAsync(
        HttpClient client,
        string gpuName,
        Version installedVersion,
        CancellationToken token = default,
        int? osIdOverride = null,
        string? hotfixCachePath = null,
        bool forceHotfixRefresh = false,
        bool bypassHotfixCooldown = false,
        HotfixFeedOptions? hotfixFeedOptions = null)
    {
        token.ThrowIfCancellationRequested();

        DriverUpdateInfo[] whqlDrivers = [];
        try
        {
            using var whqlCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            whqlCts.CancelAfter(TimeSpan.FromSeconds(15));
            whqlDrivers = await WhqlDriversAsync(client, gpuName, maxResults: 10, whqlCts.Token, osIdOverride).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or IOException ||
                                  (e is OperationCanceledException && !token.IsCancellationRequested))
        {
        }

        var latestWhql = whqlDrivers.FirstOrDefault();
        var previousWhqls = whqlDrivers.Where(d => d.ParsedVersion < installedVersion).ToArray();

        DriverUpdateInfo? latestHotfix = null;
        DriverUpdateInfo[] previousHotfixes = [];
        DriverUpdateInfo[] allHotfixes = [];
        HotfixScanResult? hotfixScan = null;
        HotfixResolutionResult? hotfixResolution = null;
        var hotfixChecked = false;
        try
        {
            using var hotfixCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            hotfixCts.CancelAfter(TimeSpan.FromSeconds(30));
            (latestHotfix, previousHotfixes, allHotfixes, hotfixScan, hotfixResolution) = await HotfixHistoryInternalAsync(
                client,
                installedVersion,
                latestWhql?.ParsedVersion,
                gpuName,
                includePrevious: true,
                hotfixCachePath,
                forceHotfixRefresh,
                bypassHotfixCooldown,
                hotfixCts.Token,
                hotfixFeedOptions).ConfigureAwait(false);
            hotfixChecked = hotfixScan.Checked;
        }
        catch (Exception e) when (e is HttpRequestException or IOException ||
                                  (e is OperationCanceledException && !token.IsCancellationRequested))
        {
        }

        var allPrevious = previousWhqls
            .Concat(previousHotfixes)
            .OrderByDescending(d => d.ParsedVersion)
            .ThenByDescending(d => d.IsHotfix)
            .ToArray();

        var allMap = new Dictionary<(NvidiaDriverType Type, Version Version), DriverUpdateInfo>();
        foreach (var item in whqlDrivers.Concat(allHotfixes))
        {
            if (item.ParsedVersion is null) continue;
            var key = (item.DriverType, item.ParsedVersion);
            if (!allMap.ContainsKey(key))
                allMap[key] = item;
        }

        var allDrivers = allMap.Values
            .OrderByDescending(d => d.ParsedVersion)
            .ThenByDescending(d => d.IsHotfix)
            .ToArray();

        return new DriverHistoryInfo(
            latestWhql,
            latestHotfix,
            new PreviousDriverOptions(previousWhqls.FirstOrDefault(), hotfixResolution?.PreviousHotfix ?? previousHotfixes.FirstOrDefault(), allPrevious),
            hotfixChecked,
            AllDrivers: allDrivers,
            HotfixSourceStatuses: hotfixScan?.SourceStatuses ?? [],
            HotfixLastScanUtc: hotfixScan?.LastScanUtc,
            HotfixFromCache: hotfixScan?.FromCache ?? false,
            HotfixResolution: hotfixResolution);
    }

    public static async Task<DriverUpdateInfo> ResolveDirectInstallerUrlAsync(
        HttpClient client,
        DriverUpdateInfo update,
        bool isNotebook,
        CancellationToken token = default)
    {
        if (TryNormalizeInstallerUrl(update.DirectInstallerUrl) is { } existing)
            return update with { DirectInstallerUrl = existing };

        string? directUrl = null;
        var summary = CleanReleaseNotes(update.ReleaseSummary, preferBulletList: update.IsHotfix);
        var pageUrl = TryNormalizeReleasePageUrl(update.ReleasePageUrl);

        if (pageUrl is not null)
        {
            try
            {
                var html = await GetStringWithRedirectAsync(client, pageUrl, IsNvidiaWebHost, token).ConfigureAwait(false);
                directUrl = ExtractDirectInstallerUrl(html, isNotebook);
                if (string.IsNullOrWhiteSpace(summary))
                    summary = CleanReleaseNotes(html, preferBulletList: update.IsHotfix);
            }
            catch (Exception e) when (update.IsHotfix &&
                                      (e is HttpRequestException or IOException ||
                                       (e is OperationCanceledException && !token.IsCancellationRequested)))
            {
            }
        }

        if (directUrl is null && update.IsHotfix)
        {
            directUrl = await HotfixFeedScanner.ProbeCdnInstallerUrlAsync(
                client, update.ParsedVersion, isNotebook, token).ConfigureAwait(false);
        }

        return update with
        {
            DirectInstallerUrl = directUrl,
            ReleasePageUrl = pageUrl ?? update.ReleasePageUrl,
            ReleaseSummary = summary
        };
    }

    public static string? ExtractDirectInstallerUrl(string? html, bool isNotebook)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InstallerExeUrl().Matches(html))
        {
            var decoded = WebUtility.HtmlDecode(match.Value);
            var normalized = TryNormalizeInstallerUrl(decoded);
            if (normalized is null) continue;
            if (normalized.Contains("32bit", StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(normalized)) candidates.Add(normalized);
        }

        if (candidates.Count == 0) return null;

        var targetFormFactor = isNotebook ? "notebook" : "desktop";
        return candidates.FirstOrDefault(u =>
                   u.Contains(targetFormFactor, StringComparison.OrdinalIgnoreCase) &&
                   u.Contains("international", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(u => u.Contains(targetFormFactor, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(u => u.Contains("international", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    public static string? SummarizeHtml(string? html, bool preferBulletList = false) =>
        CleanReleaseNotes(html, preferBulletList, maxLength: 1200);

    public static string? CleanReleaseNotes(string? raw, bool preferBulletList = false, int maxLength = 1200)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var decoded = UnescapePercentSequencesSafe(raw);
        var cleaned = ScriptOrStyle().Replace(decoded, " ");
        if (cleaned.Contains("&lt;", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Contains("&gt;", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = WebUtility.HtmlDecode(cleaned);
            cleaned = ScriptOrStyle().Replace(cleaned, " ");
        }

        if (preferBulletList)
        {
            var scope = cleaned;
            var anchor = HotfixChangesAnchor().Match(cleaned);
            if (anchor.Success)
                scope = cleaned[anchor.Index..];

            var bullets = ListItem().Matches(scope)
                .Select(m => NormalizeSummaryLine(WebUtility.HtmlDecode(Tags().Replace(m.Groups[1].Value, " "))))
                .Where(text => text.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .Select(text => text.StartsWith("• ", StringComparison.Ordinal) ? text : "• " + text)
                .ToArray();
            if (bullets.Length > 0)
                return TruncateSummary(string.Join("\n", bullets), maxLength);
        }

        var normalized = BlockTags().Replace(cleaned, "\n");
        normalized = ListItemStart().Replace(normalized, "\n• ");
        normalized = InlineTags().Replace(normalized, "");
        normalized = Tags().Replace(normalized, " ");
        normalized = WebUtility.HtmlDecode(normalized);

        var lines = new List<string>();
        string? previous = null;
        foreach (var rawLine in normalized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = NormalizeSummaryLine(rawLine);
            if (line.Length == 0 || line == "•" || line == "-" || string.Equals(line, previous, StringComparison.Ordinal))
                continue;
            lines.Add(line);
            previous = line;
            if (lines.Count >= 12)
                break;
        }

        return lines.Count == 0 ? null : TruncateSummary(string.Join("\n", lines), maxLength);
    }

    private static string NormalizeSummaryLine(string rawLine)
    {
        var line = rawLine.Replace('\u00A0', ' ').Replace('\t', ' ');
        line = Whitespace().Replace(line, " ").Trim();
        while (line.StartsWith("• • ", StringComparison.Ordinal))
            line = line[2..];
        if (line.StartsWith("• - ", StringComparison.Ordinal))
            line = "• " + line[4..];
        return line.Trim();
    }

    private static string UnescapePercentSequencesSafe(string input)
    {
        var current = input;
        for (var pass = 0; pass < 3 && current.Contains('%'); pass++)
        {
            var next = DecodePercentPass(current);
            if (string.Equals(next, current, StringComparison.Ordinal))
                break;
            current = next;
        }
        return current;
    }

    private static string DecodePercentPass(string input)
    {
        var sb = new StringBuilder(input.Length);
        var bytes = new List<byte>();

        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] == '%' && i + 2 < input.Length &&
                TryHexByte(input[i + 1], input[i + 2], out var b))
            {
                bytes.Add(b);
                i += 2;
                continue;
            }

            if (bytes.Count > 0)
            {
                sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                bytes.Clear();
            }
            sb.Append(input[i]);
        }

        if (bytes.Count > 0)
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));

        return sb.ToString();
    }

    private static bool TryHexByte(char hi, char lo, out byte value)
    {
        var h = HexVal(hi);
        var l = HexVal(lo);
        if (h < 0 || l < 0)
        {
            value = 0;
            return false;
        }
        value = (byte)((h << 4) | l);
        return true;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    public static string? TryNormalizeInstallerUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var candidate = raw.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            candidate = "https:" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme == Uri.UriSchemeHttp && IsInstallerHost(uri))
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        return uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } &&
               IsInstallerHost(uri) &&
               uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    public static string? TryNormalizeReleasePageUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var candidate = raw.Trim();
        if (candidate.StartsWith("//", StringComparison.Ordinal))
            candidate = "https:" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme == Uri.UriSchemeHttp && IsSafeReleaseWebHost(uri))
            uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;

        return uri is { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } && IsSafeReleaseWebHost(uri)
            ? uri.AbsoluteUri
            : null;
    }

    internal static bool IsSafeReleaseWebHost(Uri uri) =>
        uri.HostNameType == UriHostNameType.Dns &&
        uri.IdnHost.Contains('.') &&
        !uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
        !IPAddress.TryParse(uri.IdnHost, out _);

    internal static bool IsNvidiaWebHost(Uri uri) =>
        uri.IdnHost.Equals("www.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("gfwsl.geforce.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsReleaseHost(Uri uri) =>
        uri.IdnHost.Equals("www.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("nvidia.custhelp.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("www.techpowerup.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("techpowerup.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("www.guru3d.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Equals("guru3d.com", StringComparison.OrdinalIgnoreCase);

    internal static bool IsInstallerHost(Uri uri) =>
        uri.IdnHost.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase);

    internal static async Task<HttpResponseMessage> SendWithNvidiaRedirectAsync(
        HttpClient client,
        Uri uri,
        Func<Uri, bool> isAllowedHost,
        Uri? referer,
        HttpCompletionOption completion,
        CancellationToken token)
    {
        var current = uri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (current is not { Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } || !isAllowedHost(current))
                throw new IOException(GetText("Drivers.DisallowedNVIDIAURL", current));

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (referer is not null)
                request.Headers.Referrer = referer;

            var response = await client.SendAsync(request, completion, token).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                referer ??= current;
                current = next;
                continue;
            }

            return response;
        }

        throw new IOException(GetText("Drivers.TooManyRedirectsInTheNVIDIARequest"));
    }

    private static async Task<string> GetStringWithRedirectAsync(
        HttpClient client,
        string url,
        Func<Uri, bool> isAllowedHost,
        CancellationToken token)
    {
        using var response = await SendWithNvidiaRedirectAsync(
            client,
            new Uri(url),
            isAllowedHost,
            referer: null,
            HttpCompletionOption.ResponseContentRead,
            token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static string TruncateSummary(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        var cut = text.LastIndexOf(' ', maxLength - 1);
        return (cut > maxLength / 2 ? text[..cut] : text[..(maxLength - 1)]).TrimEnd() + "…";
    }

    private static string? Find(JsonElement menu, string name) => menu.EnumerateArray()
        .Where(e => string.Equals(e.GetProperty("menutext").GetString(), name, StringComparison.OrdinalIgnoreCase))
        .Select(e => e.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture))
        .FirstOrDefault();

    private static async Task<JsonDocument> MenuAsync(HttpClient client, string? series, CancellationToken token)
    {
        var query = Uri.EscapeDataString(JsonSerializer.Serialize(new { pt = 1, pst = series, sa = "1", driverType = "all" }));
        var url = Service + "/nvidia_web_services/controller.php?com.nvidia.services.Drivers.getMenuArrays/" + query;
        var data = await GetStringWithRedirectAsync(client, url, IsNvidiaWebHost, token).ConfigureAwait(false);
        return JsonDocument.Parse(data);
    }

    [GeneratedRegex(@"^GeForce ((?:RTX|GTX|MX|GT)\s?\d{1,2})\d{2}(?:\b|[A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Series();

    internal static Regex CustHelpHotfixLink() => HotfixLink();
    internal static Regex CustHelpHotfixTitle() => HotfixTitle();

    [GeneratedRegex(@"<h3>\s*<a\b[^>]*?/a_id/(\d+)[^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HotfixLink();

    [GeneratedRegex(@"^GeForce.*?Hotfix Display Driver version (\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HotfixTitle();

    [GeneratedRegex(@"(?:https?:)?//(?:[a-z0-9-]+\.)?download\.nvidia\.com/[^\s""'<>()]+?\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerExeUrl();

    [GeneratedRegex(@"<(?:script|style)\b[^>]*>.*?</(?:script|style)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"\b(?:addresses|following)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HotfixChangesAnchor();

    [GeneratedRegex(@"<li\b[^>]*>(.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemStart();

    [GeneratedRegex(@"<(?:br\s*/?|/(?:p|div|li|ul|ol|tr|h[1-6]))>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTags();

    [GeneratedRegex(@"</?(?:b|strong|i|em|u|span|a|code|small|font)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineTags();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

public sealed partial class NvidiaDriverService(
    HttpClient client,
    string cacheDirectory,
    long minInstallerBytes = NvidiaDriverService.DefaultMinInstallerBytes)
{
    public const long DefaultMinInstallerBytes = 50L * 1024 * 1024;
    public const string WagnardsoftDduUrl = "https://www.wagnardsoft.com/display-driver-uninstaller-DDU-";
    private const long MaxInstallerBytes = 2560L * 1024 * 1024;
    private static readonly TimeSpan StalePartAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(60);

    public string CacheDirectory => SafePaths.Root(cacheDirectory);

    public static string GetInstallerFileName(DriverUpdateInfo update) =>
        $"NVIDIA-{(update.IsHotfix ? "Hotfix" : "WHQL")}-{NvidiaDriverLookup.Display(update.ParsedVersion)}.exe";

    public string GetInstallerPath(DriverUpdateInfo update) =>
        SafePaths.Child(CacheDirectory, GetInstallerFileName(update));

    public static string BuildInstallerArguments(bool silentInstall, bool cleanInstall) => !silentInstall
        ? ""
        : cleanInstall
            ? "-s -clean -noreboot"
            : "-s -noreboot";

    public static string? FindDduExecutable(string? configuredPath = null, string? storageDirectory = null)
    {
        var candidates = new List<string?> { configuredPath };

        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Tools", "DDU", "Display Driver Uninstaller.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "DDU", "Display Driver Uninstaller.exe"));

        if (!string.IsNullOrWhiteSpace(storageDirectory))
        {
            candidates.Add(Path.Combine(storageDirectory, "Tools", "DDU", "Display Driver Uninstaller.exe"));
            candidates.Add(Path.Combine(storageDirectory, "DDU", "Display Driver Uninstaller.exe"));
        }

        var specialRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };

        foreach (var root in specialRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            candidates.Add(Path.Combine(root, "Display Driver Uninstaller", "Display Driver Uninstaller.exe"));
            candidates.Add(Path.Combine(root, "DDU", "Display Driver Uninstaller.exe"));
        }

        foreach (var candidate in candidates)
        {
            if (TryValidateDduPath(candidate) is { } valid)
                return valid;
        }

        if (OperatingSystem.IsWindows())
            return FindDduInRegistry();

        return null;
    }

    private static string? TryValidateDduPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
            return null;

        try
        {
            var full = Path.GetFullPath(path);
            SafePaths.RejectLinks(full);
            return full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindDduInRegistry()
    {
        foreach (var (hive, subKey) in new[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
        })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var uninstall = baseKey.OpenSubKey(subKey);
                if (uninstall is null) continue;

                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(name);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName) ||
                        !displayName.Contains("Display Driver Uninstaller", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry?.GetValue("InstallLocation") is string installLocation &&
                        !string.IsNullOrWhiteSpace(installLocation))
                    {
                        var candidate = Path.Combine(installLocation.Trim('"'), "Display Driver Uninstaller.exe");
                        if (TryValidateDduPath(candidate) is { } valid)
                            return valid;
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
        return null;
    }

    public DriverUpdateInfo[] GetCachedInstallers()
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return [];

        var results = new List<DriverUpdateInfo>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.exe", options))
        {
            var match = CachedInstallerFileName().Match(file.Name);
            if (!match.Success || !Version.TryParse(match.Groups[2].Value, out var version))
                continue;

            try
            {
                VerifyInstaller(file.FullName);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var driverType = match.Groups[1].Value.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)
                ? NvidiaDriverType.Hotfix
                : NvidiaDriverType.WHQL;

            results.Add(new DriverUpdateInfo(
                driverType,
                NvidiaDriverLookup.Display(version),
                version,
                ReleasePageUrl: NvidiaDriverLookup.DriverPage,
                IsCached: true));
        }

        return [.. results.OrderByDescending(item => item.ParsedVersion)];
    }

    public static DriverUpdateInfo ParseLocalInstallerInfo(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        var isHotfix = fileName.Contains("hotfix", StringComparison.OrdinalIgnoreCase) ||
                       LocalHotfixTag().IsMatch(fileName);
        var driverType = isHotfix ? NvidiaDriverType.Hotfix : NvidiaDriverType.WHQL;
        var versionMatch = LocalInstallerVersion().Match(fileName);
        if (versionMatch.Success && Version.TryParse(versionMatch.Groups[1].Value, out var parsed))
        {
            return new DriverUpdateInfo(
                driverType,
                NvidiaDriverLookup.Display(parsed),
                parsed,
                ReleasePageUrl: NvidiaDriverLookup.DriverPage,
                IsCached: true);
        }

        return new DriverUpdateInfo(
            driverType,
            Path.GetFileNameWithoutExtension(fileName),
            new Version(0, 0),
            IsCached: true);
    }

    public PreviousDriverOptions EnrichPreviousDrivers(PreviousDriverOptions online, Version installedVersion)
    {
        var map = new Dictionary<(NvidiaDriverType Type, Version Version), DriverUpdateInfo>();

        foreach (var remote in online.Drivers.Where(d => d.ParsedVersion < installedVersion))
        {
            var cached = GetCachedInstaller(remote) is not null;
            map[(remote.DriverType, remote.ParsedVersion)] = remote with { IsCached = cached };
        }

        foreach (var local in GetCachedInstallers().Where(d => d.ParsedVersion < installedVersion))
        {
            var key = (local.DriverType, local.ParsedVersion);
            map[key] = map.TryGetValue(key, out var existing)
                ? existing with { IsCached = true }
                : local;
        }

        var merged = map.Values
            .OrderByDescending(d => d.ParsedVersion)
            .ThenByDescending(d => d.IsHotfix)
            .ToArray();

        return new PreviousDriverOptions(
            merged.FirstOrDefault(d => !d.IsHotfix),
            merged.FirstOrDefault(d => d.IsHotfix),
            merged);
    }

    public DriverUpdateInfo[] EnrichDriverList(DriverUpdateInfo[] onlineDrivers)
    {
        var map = new Dictionary<(NvidiaDriverType Type, Version Version), DriverUpdateInfo>();

        foreach (var remote in onlineDrivers)
        {
            if (remote.ParsedVersion is null) continue;
            var cached = GetCachedInstaller(remote) is not null;
            map[(remote.DriverType, remote.ParsedVersion)] = remote with { IsCached = cached };
        }

        foreach (var local in GetCachedInstallers())
        {
            if (local.ParsedVersion is null) continue;
            var key = (local.DriverType, local.ParsedVersion);
            map[key] = map.TryGetValue(key, out var existing)
                ? existing with { IsCached = true }
                : local;
        }

        return
        [
            .. map.Values
                .OrderByDescending(d => d.ParsedVersion)
                .ThenByDescending(d => d.IsHotfix)
        ];
    }

    public string? GetCachedInstaller(DriverUpdateInfo update)
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return null;
        var path = SafePaths.Child(root, GetInstallerFileName(update));
        if (!File.Exists(path)) return null;

        try
        {
            VerifyInstaller(path);
            return path;
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            TryDeleteFile(path);
            return null;
        }
    }

    public void VerifyInstaller(string path)
    {
        SafePaths.RejectLinks(path);
        if (!File.Exists(path))
            throw new IOException(GetText("Drivers.TheDownloadedDriverFileDoesNotExist"));

        var length = new FileInfo(path).Length;
        if (length < minInstallerBytes)
            throw new InvalidDataException(GetText("Drivers.TheDownloadedFileIsSmallerThanMB"));

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> mz = stackalloc byte[2];
        if (stream.Read(mz) < 2 || mz[0] != (byte)'M' || mz[1] != (byte)'Z')
            throw new InvalidDataException(GetText("Drivers.TheDownloadedFileHasNoValidMZ"));

        stream.Position = 0;
        try
        {
            var pe = new PEHeaders(stream);
            if (pe.PEHeader is null ||
                pe.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) ||
                pe.CoffHeader.Machine is not (Machine.Amd64 or Machine.I386))
                throw new InvalidDataException(GetText("Drivers.TheDownloadedFileIsNotAValid"));
        }
        catch (BadImageFormatException)
        {
            throw new InvalidDataException(GetText("Drivers.TheDownloadedFileHasNoValidPE"));
        }
    }

    public async Task<string> DownloadAsync(
        DriverUpdateInfo update,
        bool isNotebook = false,
        IProgress<DriverDownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        if (GetCachedInstaller(update) is { } cached)
        {
            var cachedSize = new FileInfo(cached).Length;
            progress?.Report(new DriverDownloadProgress(cachedSize, cachedSize));
            return cached;
        }

        var resolved = await NvidiaDriverLookup.ResolveDirectInstallerUrlAsync(client, update, isNotebook, token)
            .ConfigureAwait(false);
        var downloadUrl = NvidiaDriverLookup.TryNormalizeInstallerUrl(resolved.DirectInstallerUrl)
            ?? throw new InvalidOperationException(GetText("Drivers.CouldNotResolveADirectDownloadLink"));

        var root = CacheDirectory;
        Directory.CreateDirectory(root);
        var finalPath = SafePaths.Child(root, GetInstallerFileName(resolved));
        var partPath = SafePaths.Child(root, GetInstallerFileName(resolved) + ".part");
        TryDeleteFile(partPath);

        Uri? referer = Uri.TryCreate(resolved.ReleasePageUrl, UriKind.Absolute, out var pageUri)
            ? pageUri
            : new Uri("https://www.nvidia.com/");

        try
        {
            using var response = await NvidiaDriverLookup.SendWithNvidiaRedirectAsync(
                client,
                new Uri(downloadUrl),
                NvidiaDriverLookup.IsInstallerHost,
                referer,
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(GetText("Drivers.TheServerReturnedAnHTMLPageInstead"));

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is < 0 or > MaxInstallerBytes || (totalBytes is not null && totalBytes < minInstallerBytes))
                throw new InvalidDataException(GetText("Drivers.TheDriverInstallerFileSizeIsOutside"));

            progress?.Report(new DriverDownloadProgress(0, totalBytes));
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                var lastReportTick = Environment.TickCount64;
                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    readCts.CancelAfter(ReadInactivityTimeout);
                    int count;
                    try
                    {
                        count = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        throw new IOException(GetText("Drivers.TheDriverDownloadTimedOut"));
                    }
                    if (count == 0) break;

                    received += count;
                    if (received > MaxInstallerBytes)
                        throw new InvalidDataException(GetText("Drivers.TheDriverDownloadExceedsTheSizeLimit"));

                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);

                    var nowTick = Environment.TickCount64;
                    if (nowTick - lastReportTick >= 100)
                    {
                        lastReportTick = nowTick;
                        progress?.Report(new DriverDownloadProgress(received, totalBytes));
                    }
                }

                if (totalBytes is { } expected && received != expected)
                    throw new IOException(GetText("Drivers.TheDriverDownloadIsIncomplete"));

                output.Flush(flushToDisk: true);
                progress?.Report(new DriverDownloadProgress(received, totalBytes ?? received));
            }

            VerifyInstaller(partPath);
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(finalPath);
            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
        finally
        {
            TryDeleteFile(partPath);
        }
    }

    public long GetCacheSizeBytes()
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return 0;

        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            try { total += file.Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return total;
    }

    public (long FreedBytes, int DeletedCount) ClearCache()
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return (0, 0);

        long freed = 0;
        var deleted = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (!file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                SafePaths.RejectLinks(file.FullName);
                var size = file.Length;
                file.Delete();
                freed += size;
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return (freed, deleted);
    }

    public int CleanupOnStartup(
        Version? installedVersion,
        bool deleteInstalledMatch = true,
        DateTimeOffset? now = null,
        string? protectedInstallerFileName = null)
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return 0;

        var utcNow = now ?? DateTimeOffset.UtcNow;
        var deleted = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            if (!file.Extension.Equals(".part", StringComparison.OrdinalIgnoreCase) &&
                !file.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (utcNow - file.LastWriteTimeUtc > StalePartAge)
                {
                    SafePaths.RejectLinks(file.FullName);
                    file.Delete();
                    deleted++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return deleted + CleanupOutdatedInstallers(installedVersion, latestAvailableVersion: null, deleteInstalledMatch, protectedInstallerFileName);
    }

    public int CleanupOutdatedInstallers(
        Version? installedVersion,
        Version? latestAvailableVersion,
        bool deleteInstalledMatch = true,
        string? protectedInstallerFileName = null)
    {
        var root = CacheDirectory;
        if (!Directory.Exists(root)) return 0;

        var deleted = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.exe", options))
        {
            if (!string.IsNullOrWhiteSpace(protectedInstallerFileName) &&
                file.Name.Equals(protectedInstallerFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = CachedInstallerFileName().Match(file.Name);
            if (!match.Success || !Version.TryParse(match.Groups[2].Value, out var fileVersion))
                continue;

            var outdatedByInstalled = installedVersion is not null &&
                (deleteInstalledMatch ? fileVersion <= installedVersion : fileVersion < installedVersion);
            var outdatedByAvailable = latestAvailableVersion is not null && fileVersion < latestAvailableVersion;

            if (!outdatedByInstalled && !outdatedByAvailable)
                continue;

            try
            {
                SafePaths.RejectLinks(file.FullName);
                file.Delete();
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return deleted;
    }

    public bool CleanupAfterInstall(DriverUpdateInfo installedUpdate, Version? currentSystemVersion, bool autoDeleteEnabled)
    {
        if (!autoDeleteEnabled || currentSystemVersion is null || currentSystemVersion < installedUpdate.ParsedVersion)
            return false;

        var path = GetInstallerPath(installedUpdate);
        var targetRemoved = false;
        if (File.Exists(path))
        {
            targetRemoved = TryDeleteFile(path);
        }

        CleanupOutdatedInstallers(currentSystemVersion, latestAvailableVersion: null, deleteInstalledMatch: true);
        return targetRemoved || !File.Exists(path);
    }

    public bool CleanupAfterDowngrade(DriverUpdateInfo downgradeTarget, Version? currentSystemVersion, bool autoDeleteEnabled)
    {
        if (!autoDeleteEnabled || currentSystemVersion is null || currentSystemVersion != downgradeTarget.ParsedVersion)
            return false;

        var path = GetInstallerPath(downgradeTarget);
        var targetRemoved = false;
        if (File.Exists(path))
        {
            targetRemoved = TryDeleteFile(path);
        }

        return targetRemoved || !File.Exists(path);
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            SafePaths.RejectLinks(path);
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"^NVIDIA-(WHQL|Hotfix)-(\d+\.\d+)\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CachedInstallerFileName();

    [GeneratedRegex(@"(?<!\d)(\d{3}\.\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalInstallerVersion();

    [GeneratedRegex(@"(?:^|[-_.])hf(?:[-_.]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalHotfixTag();
}
