using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;

namespace DLSSManager.Core;

[SupportedOSPlatform("windows")]
public sealed class OptiScalerDownload(HttpClient client, string cache, LocalDllCatalog catalog)
{
    private const string Repository = "optiscaler/OptiScaler";
    private const long Limit = 192L * 1024 * 1024;
    private sealed record Release(string tag_name, DateTimeOffset published_at, Asset[] assets, bool draft, bool prerelease);
    private sealed record Asset(string name, long size, string? digest, string browser_download_url);

    public async Task<DllCatalogEntry> GetAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        var api = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.UserAgent.ParseAdd("DLSSManager-CSharp");
        using var manifest = await client.SendAsync(request, token).ConfigureAwait(false);
        manifest.EnsureSuccessStatusCode();
        if (manifest.RequestMessage?.RequestUri != api) throw new IOException("Unerwartetes OptiScaler-Releaseziel.");
        var release = await manifest.Content.ReadFromJsonAsync<Release>(cancellationToken: token).ConfigureAwait(false)
            ?? throw new IOException("OptiScaler-Release fehlt.");
        if (release.draft || release.prerelease || DllVersionService.Parse(release.tag_name.TrimStart('v', 'V')) is null)
            throw new IOException("OptiScaler-Releaseversion konnte nicht gelesen werden.");
        var archives = release.assets.Where(a => a.name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) &&
            a.name.StartsWith("OptiScaler_", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (archives.Length != 1 || archives[0] is not { size: > 0 and <= Limit, digest: { Length: 71 } hash } asset ||
            !hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !hash[7..].All(Uri.IsHexDigit) ||
            asset.name != Path.GetFileName(asset.name))
            throw new IOException("Kein eindeutiges OptiScaler-Archiv mit SHA-256-Hash gefunden.");
        var url = new Uri($"https://github.com/{Repository}/releases/download/{release.tag_name}/{Uri.EscapeDataString(asset.name)}");
        if (asset.browser_download_url != url.AbsoluteUri) throw new IOException("Unerwartete OptiScaler-Quelle.");

        var root = SafePaths.Root(cache);
        Directory.CreateDirectory(root);
        var work = SafePaths.Child(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var archive = SafePaths.Child(work, "OptiScaler.7z");
        var dll = SafePaths.Child(work, "OptiScaler.dll");
        var extracted = SafePaths.Child(work, "files");
        Directory.CreateDirectory(extracted);
        try
        {
            using var first = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var redirect = first.Headers.Location;
            if ((int)first.StatusCode is 302 or 307 && redirect is { IsAbsoluteUri: true, IsDefaultPort: true, Scheme: "https", UserInfo.Length: 0 } &&
                redirect.IdnHost == "release-assets.githubusercontent.com")
            {
                using var response = await client.GetAsync(redirect, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri != redirect) throw new IOException("Unerwartetes OptiScaler-Downloadziel.");
                await WriteArchiveAsync(response, archive, asset.size, token).ConfigureAwait(false);
            }
            else await WriteArchiveAsync(first, archive, asset.size, token).ConfigureAwait(false);
            if (!hash[7..].Equals(DurableFiles.Hash(archive, token), StringComparison.OrdinalIgnoreCase))
                throw new IOException("SHA-256-Prüfung des OptiScaler-Archivs fehlgeschlagen.");
            await ExtractMemberAsync(archive, "OptiScaler.dll", dll, Limit, token).ConfigureAwait(false);
            foreach (var member in OptiScalerService.SupportFiles)
            {
                var path = SafePaths.Child(extracted, member);
                await ExtractMemberAsync(archive, member, path, member.EndsWith(".ini", StringComparison.Ordinal) ? 2 * 1024 * 1024 : Limit, token, optional: member != "OptiScaler.ini").ConfigureAwait(false);
            }
            var entry = catalog.Import(dll, token, $"OptiScaler {release.tag_name}", release.published_at);
            var support = entry.LocalPath + ".files";
            SafePaths.RejectLinks(support);
            if (!Directory.Exists(support)) Directory.Move(extracted, support);
            return entry;
        }
        finally
        {
            if (Directory.Exists(extracted))
            {
                foreach (var file in Directory.EnumerateFiles(extracted)) File.Delete(file);
                Directory.Delete(extracted);
            }
            File.Delete(dll);
            File.Delete(archive);
            Directory.Delete(work);
        }
    }

    private static async Task WriteArchiveAsync(HttpResponseMessage response, string archive, long size, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != size)
            throw new IOException("OptiScaler-Archivgröße stimmt nicht mit dem Release überein.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        if (await CopyAsync(source, archive, size, token).ConfigureAwait(false) != size)
            throw new IOException("OptiScaler-Download ist unvollständig.");
    }

    private static async Task ExtractMemberAsync(string archive, string member, string destination, long limit, CancellationToken token, bool optional = false)
    {
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (!File.Exists(tar)) throw new IOException("Windows tar.exe wird für das 7-Zip-Archiv benötigt.");
        var start = new ProcessStartInfo(tar)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // Write the single known member to stdout. No archive entry controls a destination path.
        foreach (var argument in new[] { "-xOf", archive, "--", member }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Windows tar.exe konnte nicht gestartet werden.");
        try
        {
            var error = process.StandardError.ReadToEndAsync(token);
            var copied = await CopyAsync(process.StandardOutput.BaseStream, destination, limit, token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (optional && copied == 0)
            {
                File.Delete(destination);
                return;
            }
            if (process.ExitCode != 0 || copied == 0)
                throw new IOException($"{member} konnte nicht gelesen werden. {await error.ConfigureAwait(false)}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<long> CopyAsync(Stream input, string output, long limit, CancellationToken token)
    {
        await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > limit) throw new IOException("OptiScaler-Datei überschreitet das Größenlimit.");
            await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return total;
    }
}
