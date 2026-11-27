using static DLSSManager.Core.LocalizationService;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSSManager.Core;

[Flags]
public enum RtxFeature { None = 0, SR = 1, FG = 2, MFG = 4, RR = 8, DLAA = 16, RT = 32, PathTracing = 64 }

public sealed record RtxGame(string Title, RtxFeature Features, string MultiFrameGeneration, bool Transformer)
{
    public int? RecommendedFrames => MultiFrameGeneration.Split(',', StringSplitOptions.TrimEntries)
        .Select(value => value.ToUpperInvariant() switch { "6X" => 5, "5X" => 4, "4X" => 3, "3X" => 2, "2X" => 1, _ => (int?)null }).Max();
    public string Display => Features == RtxFeature.None ? "RTX" :
        Features.ToString() + (Transformer ? " · Transformer SR" : "") +
        (MultiFrameGeneration.Length != 0 ? $" · {MultiFrameGeneration}" : "");
}

public sealed partial class RtxDatabase
{
    private readonly Dictionary<string, RtxGame?> index;
    public int Count { get; }

    public RtxDatabase(IEnumerable<RtxGame> games)
    {
        var entries = games.GroupBy(g => Normalize(g.Title)).Where(g => g.Key.Length > 0).ToArray();
        index = entries.ToDictionary(g => g.Key, g => g.DistinctBy(v =>
            (v.Features, v.MultiFrameGeneration, v.Transformer)).Take(2).Count() == 1 ? g.First() : null);
        Count = entries.Length;
    }

    public RtxGame? Find(string title)
    {
        var key = Normalize(title);
        if (index.TryGetValue(key, out var exact)) return exact;
        var alias = Normalize(EditionSuffix().Replace(title, ""));
        return alias != key ? index.GetValueOrDefault(alias) : null;
    }

    [GeneratedRegex(@"(?:[\s:–-]+(?:game of the year|goty|complete edition|definitive edition|director['’]s cut|win64 shipping))+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionSuffix();

    private static string Normalize(string title) => string.Concat(title.Normalize(NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit)).ToUpperInvariant();

    public static RtxDatabase Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new IOException(GetText("RtxDatabase.DLSSdbContainsNoValidGameList"));
        var games = new List<RtxGame>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string Value(string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim() : "";
            if (Value("type") != "Game" || Value("name") is not { Length: > 0 } title) continue;
            var mfg = Value("dlss multi frame generation");
            var sr = Value("dlss super resolution");
            var rt = Value("ray tracing");
            var features = RtxFeature.None;
            if (sr.Length != 0) features |= RtxFeature.SR;
            if (Value("dlss frame generation").Length != 0) features |= RtxFeature.FG;
            if (mfg.Length != 0) features |= RtxFeature.MFG;
            if (Value("dlss ray reconstruction").Length != 0) features |= RtxFeature.RR;
            if (Value("dlaa").Length != 0) features |= RtxFeature.DLAA;
            if (rt.Length != 0) features |= RtxFeature.RT;
            if (rt.Contains("Path", StringComparison.OrdinalIgnoreCase)) features |= RtxFeature.PathTracing;
            games.Add(new(title, features, mfg, sr.Split(',', StringSplitOptions.TrimEntries).Contains("T")));
        }
        return new(games);
    }
}

public sealed class RtxDatabaseService(HttpClient client, string storage)
{
    private const int Limit = 4 * 1024 * 1024;
    private static readonly Uri Source = new("https://raw.githubusercontent.com/JZ6/DLSSdb/main/public/dlss-rt-games-apps-overrides.json");
    private sealed record Cache(string Json, string? ETag);

    private string CachePath => SafePaths.Child(SafePaths.Root(storage), "rtx-database-v1.json");

    private Cache? Read()
    {
        var path = CachePath;
        if (SafePaths.Attributes(path) is null) return null;
        if (new FileInfo(path).Length > 6L * Limit) throw new IOException(GetText("RtxDatabase.TheDLSSdbCacheIsTooLarge"));
        return JsonSerializer.Deserialize<Cache>(File.ReadAllText(path));
    }

    public RtxDatabase? ReadCached() => Read() is { } cached ? ParseComplete(cached.Json) : null;

    public async Task<(RtxDatabase Database, bool Offline)> RefreshAsync(CancellationToken token = default)
    {
        Cache? previous = null;
        RtxDatabase? fallback = null;
        try
        {
            previous = Read();
            if (previous is not null) fallback = ParseComplete(previous.Json);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { previous = null; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = new HttpRequestMessage(HttpMethod.Get, Source);
        if (EntityTagHeaderValue.TryParse(previous?.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri != Source) throw new IOException(GetText("RtxDatabase.UnexpectedDLSSdbSource"));
            if (response.StatusCode == HttpStatusCode.NotModified && fallback is not null) return (fallback, false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > Limit) throw new IOException(GetText("RtxDatabase.TheDLSSdbFileIsTooLarge"));
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[32768];
            int count;
            while ((count = await input.ReadAsync(block, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > Limit) throw new IOException(GetText("RtxDatabase.TheDLSSdbFileIsTooLarge"));
                buffer.Write(block, 0, count);
            }
            var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var database = ParseComplete(json);
            timeout.Token.ThrowIfCancellationRequested();
            var path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            DurableFiles.Json(path, new Cache(json, response.Headers.ETag?.ToString()), overwrite: true);
            return (database, false);
        }
        catch (Exception e) when (fallback is not null && !token.IsCancellationRequested &&
            e is IOException or JsonException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
        { return (fallback, true); }
    }

    private static RtxDatabase ParseComplete(string json)
    {
        var database = RtxDatabase.Parse(json);
        if (database.Count < 100) throw new IOException(GetText("RtxDatabase.DLSSdbContainsTooFewGamesForA"));
        return database;
    }
}
