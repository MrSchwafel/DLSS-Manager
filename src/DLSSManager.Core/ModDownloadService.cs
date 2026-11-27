using static DLSSManager.Core.LocalizationService;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;

namespace DLSSManager.Core;

public sealed record DownloadedMod(string Tag, string Directory, string[] Proxies);

public sealed class ModDownloadService(HttpClient client, string cacheDirectory)
{
    private const string Repository = "sdli1995/dlssg_for_sm86";
    private const long MaxBytes = 512L * 1024 * 1024;
    private sealed record Release(string tag_name, bool draft, bool prerelease, Asset[] assets);
    private sealed record CachedRelease(string Tag, string Hash);
    private sealed record Asset(string name, long size, string? digest, string browser_download_url);

    public async Task<DownloadedMod> GetLatestAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        var uri = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("DLSSManager-CSharp");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden &&
             response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))
            throw new IOException(GetText("ModDownloadService.GitHubRateLimitReachedTryAgainLater"));
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri != uri)
            throw new IOException(GetText("ModDownloadService.UnexpectedRedirectWhileLookingUpTheSM86"));

        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: token).ConfigureAwait(false);
        if (release is not { tag_name: { } tag, assets: { } releaseAssets } || release.draft || release.prerelease ||
            DllVersionService.Parse(tag.TrimStart('v', 'V')) is null)
            throw new IOException(GetText("ModDownloadService.GitHubDidNotReportAValidStable"));
        var name = $"dlssg-release-{tag.TrimStart('v', 'V')}-x64.zip";
        var assets = releaseAssets.Where(a => a.name == name).Take(2).ToArray();
        if (assets.Length != 1 || assets[0] is not { size: > 0 and <= MaxBytes, digest: { Length: 71 } digest } ||
            !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !digest[7..].All(Uri.IsHexDigit))
            throw new IOException(GetText("ModDownloadService.TheSM86ReleaseHasNoUniqueX64"));

        var asset = assets[0];
        var download = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{name}");
        if (!string.Equals(asset.browser_download_url, download.AbsoluteUri, StringComparison.Ordinal))
            throw new IOException(GetText("ModDownloadService.UnexpectedSM86PackageSource"));

        var root = SafePaths.Root(cacheDirectory);
        Directory.CreateDirectory(root);
        var hash = digest[7..].ToUpperInvariant();
        var zipPath = SafePaths.Child(root, hash + ".zip");
        if (DurableFiles.Hash(zipPath, token) != hash)
            await DownloadAsync(download, zipPath, asset.size, hash, token).ConfigureAwait(false);

        var package = ExtractPackage(root, zipPath, tag, hash, token);
        DurableFiles.Json(SafePaths.Child(root, "release.json"), new CachedRelease(tag, hash), overwrite: true);
        return package;
    }

    public DownloadedMod? ReadCached(CancellationToken token)
    {
        var root = SafePaths.Root(cacheDirectory);
        var manifest = SafePaths.Child(root, "release.json");
        if (!File.Exists(manifest)) return null;
        var release = System.Text.Json.JsonSerializer.Deserialize<CachedRelease>(File.ReadAllText(manifest));
        if (release is not { Hash.Length: 64 } || !release.Hash.All(Uri.IsHexDigit) ||
            DllVersionService.Parse(release.Tag.TrimStart('v', 'V')) is null)
            throw new IOException(GetText("ModDownloadService.InvalidUnlockerCatalogData"));
        var archive = SafePaths.Child(root, release.Hash + ".zip");
        return ExtractPackage(root, archive, release.Tag, release.Hash, token);
    }

    private static DownloadedMod ExtractPackage(string root, string zipPath, string tag, string hash, CancellationToken token)
    {
        // Derive the installation files anew from the verified archive, including on cache hits.
        var directory = SafePaths.Child(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Extract(zipPath, directory, hash, token);
            _ = ModPackage.Read(directory, "version.dll", token);
            var proxies = ModPackage.ReadProxies(directory);
            token.ThrowIfCancellationRequested();
            return new(tag, directory, proxies);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private async Task DownloadAsync(Uri download, string target, long expectedSize, string hash, CancellationToken token)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await client.GetAsync(download, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect &&
                response.Headers.Location is { IsAbsoluteUri: true } redirect &&
                IsReleaseAsset(redirect))
            {
                using var redirected = await client.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                await SaveResponse(redirected).ConfigureAwait(false);
            }
            else await SaveResponse(response).ConfigureAwait(false);

            if (DurableFiles.Hash(temporary, token) != hash)
                throw new IOException(GetText("ModDownloadService.SM86ArchiveSHAVerificationFailed"));
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(target);
            File.Move(temporary, target, overwrite: true);
        }
        finally { File.Delete(temporary); }

        async Task SaveResponse(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } final || (final != download && !IsReleaseAsset(final)))
                throw new IOException(GetText("ModDownloadService.UnexpectedSM86PackageDownloadDestination"));
            if (response.Content.Headers.ContentLength is { } length && length != expectedSize)
                throw new IOException(GetText("ModDownloadService.TheSM86ArchiveSizeDoesNotMatch"));
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > expectedSize) throw new IOException(GetText("ModDownloadService.TheSM86ArchiveIsLargerThanExpected"));
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (total != expectedSize) throw new IOException(GetText("ModDownloadService.TheSM86DownloadIsIncomplete"));
            output.Flush(flushToDisk: true);
        }
    }

    private static bool IsReleaseAsset(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.IdnHost == "release-assets.githubusercontent.com";

    private static void Extract(string zipPath, string directory, string hash, CancellationToken token)
    {
        using var stream = File.Open(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (DurableFiles.Hash(stream, token) != hash) throw new IOException(GetText("ModDownloadService.TheCachedSM86ArchiveChanged"));
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 1024) throw new IOException(GetText("ModDownloadService.TheSM86ArchiveContainsTooManyEntries"));
        long total = 0;
        var buffer = new byte[64 * 1024];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var target = SafePaths.Child(directory, relative.TrimEnd('/'));
            if (entry.Name.Length == 0) continue;
            if (!paths.Add(target)) throw new IOException(GetText("ModDownloadService.DuplicateFileInTheSM86Archive"));
            var isDll = relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            if (!relative.Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase) &&
                !(isDll && (relative.StartsWith("dlssg_sm86/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("alternatives/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("version.dll", StringComparison.OrdinalIgnoreCase))))
                continue;
            if (entry.Length > MaxBytes - total) throw new IOException(GetText("ModDownloadService.TheExtractedSM86PackageIsTooLarge"));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                total += count;
                if (total > MaxBytes) throw new IOException(GetText("ModDownloadService.TheExtractedSM86PackageIsTooLarge"));
                output.Write(buffer, 0, count);
            }
        }
    }
}
