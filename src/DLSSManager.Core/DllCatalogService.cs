using static DLSSManager.Core.LocalizationService;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DLSSManager.Core;

public sealed record DllCatalogEntry(string Kind, string DllName, Version Version, Uri ArchiveUrl,
    string ArchiveMd5, string DllMd5, string Source, long? Size = null, DateTimeOffset? Signed = null,
    string? Sha256 = null, string? LocalPath = null, DateTimeOffset? Published = null)
{
    public string Display => $"{Version} · {Source}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourceLabelKey => Source == "Lokaler Import" ? "Catalog.LocalImport" : Source;
    public string Availability => LocalPath is not null || Sha256 is not null ? "Lokal" : "Online";
    public DateTimeOffset? CatalogDate => Published ?? Signed;
}

public sealed record DllCatalogResult(DllCatalogEntry[] Entries, string? Notice);

public sealed class DllCatalogService(HttpClient client, string cacheDirectory)
{
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private static readonly Uri Manifest = new(
        "https://raw.githubusercontent.com/beeradmoore/dlss-swapper-manifest-builder/refs/heads/main/manifest.json");
    private const string ArtifactHost = "dlss-swapper-downloads.beeradmoore.com";
    private sealed record Snapshot(string Manifest, string? ETag);

    public DllCatalogEntry[] ReadCached() => ReadSnapshot() is { } snapshot ? Parse(snapshot.Manifest) : [];

    public async Task<DllCatalogResult> RefreshAsync(CancellationToken token = default)
    {
        Snapshot? cached = null;
        DllCatalogEntry[] entries = [];
        try
        {
            cached = ReadSnapshot();
            if (cached is not null) entries = Parse(cached.Manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            cached = null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, Manifest);
        if (EntityTagHeaderValue.TryParse(cached?.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                return new(entries, null);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri != Manifest)
                throw new IOException(GetText("DllCatalogService.UnexpectedRedirectWhileFetchingTheDLLCatalog"));
            if (response.Content.Headers.ContentLength > MaxManifestBytes)
                throw new IOException(GetText("DllCatalogService.TheDLLCatalogExceedsTheSizeLimit"));
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[32 * 1024];
            int count;
            while ((count = await source.ReadAsync(block, timeout.Token).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > MaxManifestBytes)
                    throw new IOException(GetText("DllCatalogService.TheDLLCatalogExceedsTheSizeLimit"));
                buffer.Write(block, 0, count);
            }
            var manifest = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var updated = Parse(manifest);
            timeout.Token.ThrowIfCancellationRequested();
            var root = SafePaths.Root(cacheDirectory);
            Directory.CreateDirectory(root);
            DurableFiles.Json(SafePaths.Child(root, "dlss-catalog-v1.json"),
                new Snapshot(manifest, response.Headers.ETag?.ToString()), overwrite: true);
            return new(updated, null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && entries.Length > 0)
        {
            return new(entries, GetText("DllCatalogService.TheRequestTimedOutShowingTheSaved"));
        }
        catch (Exception ex) when (entries.Length > 0 &&
            ex is HttpRequestException or IOException or JsonException or UnauthorizedAccessException)
        {
            return new(entries, GetText("DllCatalogService.ShowingTheSavedDLLCatalog", ex.Message));
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new IOException(GetText("DllCatalogService.TimedOutWhileFetchingTheDLLCatalog"), ex);
        }
    }

    private Snapshot? ReadSnapshot()
    {
        var file = SafePaths.Child(SafePaths.Root(cacheDirectory), "dlss-catalog-v1.json");
        if (SafePaths.Attributes(file) is null) return null;
        if (new FileInfo(file).Length > 2L * MaxManifestBytes)
            throw new IOException(GetText("DllCatalogService.TheSavedDLLCatalogExceedsTheSize"));
        return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file))
            ?? throw new IOException(GetText("DllCatalogService.TheSavedDLLCatalogIsEmpty"));
    }

    public static DllCatalogEntry[] Parse(string manifest)
    {
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        var entries = new List<DllCatalogEntry>();
        Add("dlss", "SR", "nvngx_dlss.dll");
        Add("dlss_g", "FG", "nvngx_dlssg.dll");
        Add("dlss_d", "RR", "nvngx_dlssd.dll");
        Add("xess", "XeSS", "libxess.dll", required: false);
        Add("fsr_31_dx12", "FSR", "amd_fidelityfx_dx12.dll", required: false);
        return entries.OrderBy(e => e.Kind).ThenByDescending(e => e.Version)
            .DistinctBy(e => (e.Kind, e.Version, e.DllMd5)).ToArray();

        void Add(string category, string kind, string name, bool required = true)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(category, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                if (required) throw new IOException(GetText("DllCatalogService.TheDLLCatalogHasNoValidCategory", category));
                return;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !Flag(item, "is_signature_valid", JsonValueKind.True) ||
                    !Flag(item, "is_dev_file", JsonValueKind.False) ||
                    DllVersionService.Parse(Value(item, "version")) is not { } version ||
                    !Uri.TryCreate(Value(item, "download_url"), UriKind.Absolute, out var url) ||
                    !IsArtifactUri(url) ||
                    !IsMd5(Value(item, "zip_md5_hash")) || !IsMd5(Value(item, "md5_hash")))
                    continue;

                var size = item.TryGetProperty("file_size", out var bytes) && bytes.ValueKind == JsonValueKind.Number &&
                    bytes.TryGetInt64(out var length) && length > 0 ? (long?)length : null;
                var signed = DateTimeOffset.TryParse(Value(item, "signed_datetime"), out var date) && date.Year > 1
                    ? (DateTimeOffset?)date : null;
                entries.Add(new(kind, name, version, url, Value(item, "zip_md5_hash").ToUpperInvariant(),
                    Value(item, "md5_hash").ToUpperInvariant(), Value(item, "dll_source"), size, signed));
            }
            if (required && !entries.Any(e => e.Kind == kind))
                throw new IOException(GetText("DllCatalogService.TheDLLCatalogHasNoUsableEntries", kind));
        }
    }

    private static string Value(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private static bool Flag(JsonElement item, string key, JsonValueKind expected) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == expected;

    internal static bool IsArtifactUri(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        url.IdnHost == ArtifactHost && url.IsDefaultPort && url.UserInfo.Length == 0;

    internal static bool IsMd5(string value) => value.Length == 32 && value.All(Uri.IsHexDigit);
}
