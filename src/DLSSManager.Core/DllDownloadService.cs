using System.Security.Cryptography;
using System.IO.Compression;
using System.Reflection.PortableExecutable;

namespace DLSSManager.Core;

public sealed record DllPackage(Uri ArchiveUrl, string DllName, string ArchiveSha256, string DllSha256);

public sealed class DllDownloadService(HttpClient client, string cacheDirectory)
{
    private const long MaxBytes = 256L * 1024 * 1024;

    public Task<string> GetAsync(DllPackage package, CancellationToken token = default) =>
        GetAsync(package.ArchiveUrl, package.DllName, Convert.ToHexString(ParseHash(package.ArchiveSha256)),
            Convert.ToHexString(ParseHash(package.DllSha256)), catalogEntry: null, token);

    public Task<string> GetAsync(DllCatalogEntry entry, CancellationToken token = default) =>
        entry.LocalPath is { } path ? Task.FromResult(path) :
            GetAsync(entry.ArchiveUrl, entry.DllName, entry.ArchiveMd5, entry.DllMd5, entry, token);

    public string? CachedPath(DllCatalogEntry entry)
    {
        var path = SafePaths.Child(SafePaths.Root(cacheDirectory), $"md5-{entry.DllMd5}-{entry.DllName.ToLowerInvariant()}");
        return File.Exists(path) ? path : null;
    }

    public static DllMetadata VerifyCatalogFile(string path, DllCatalogEntry entry, CancellationToken token = default)
    {
        SafePaths.RejectLinks(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var metadata = DllDetectionService.Inspect(path, token);
        var digestMatches = entry.LocalPath is not null ? metadata.Sha256 == entry.Sha256 :
            StringComparer.OrdinalIgnoreCase.Equals(DurableFiles.Hash(stream, token, HashAlgorithmName.MD5), entry.DllMd5);
        if (!metadata.IsDll || metadata.Machine != Machine.Amd64 || !digestMatches ||
            (metadata.NumericFileVersion ?? new Version(0, 0)) != entry.Version)
            throw new IOException($"DLL-Prüfsumme, Version oder Architektur konnte nicht bestätigt werden. Dateiversion {metadata.FileVersion}, Katalogversion {entry.Version}, Architektur {metadata.Machine}.");
        if (entry.LocalPath is null && !NvidiaSignature.IsValid(path, entry.Kind))
            throw new IOException("Die vertrauenswürdige Herstellersignatur konnte nicht bestätigt werden.");
        return metadata;
    }

    private async Task<string> GetAsync(Uri url, string name, string archiveHash, string dllHash,
        DllCatalogEntry? catalogEntry, CancellationToken token)
    {
        var requireNvidia = catalogEntry is not null;
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps ||
            DllDetectionService.UpscalerKind(name) is not ("SR" or "FG" or "RR" or "XeSS" or "FSR"))
            throw new ArgumentException("Ungültige DLL-Paketquelle.", nameof(url));

        if (requireNvidia && (!DllCatalogService.IsArtifactUri(url) ||
            !DllCatalogService.IsMd5(archiveHash) || !DllCatalogService.IsMd5(dllHash)))
            throw new ArgumentException("Ungültige Katalogdaten.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        token = timeout.Token;
        var root = SafePaths.Root(cacheDirectory);
        Directory.CreateDirectory(root);
        var target = SafePaths.Child(root, (requireNvidia ? "md5-" : "") + dllHash + "-" + name.ToLowerInvariant());
        if (Valid(target, dllHash, catalogEntry, token)) return target;

        var work = SafePaths.Child(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var zipPath = Path.Combine(work, "package.zip");
        var dllPath = Path.Combine(work, name);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } finalUri || finalUri.Scheme != Uri.UriSchemeHttps ||
                (requireNvidia && !DllCatalogService.IsArtifactUri(finalUri)))
                throw new IOException("Die Paketquelle muss HTTPS verwenden.");
            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new IOException("Das Archiv überschreitet das Größenlimit.");
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
                await WriteLimitedAsync(source, zipPath, token).ConfigureAwait(false);

            if (!StringComparer.OrdinalIgnoreCase.Equals(Digest(zipPath, requireNvidia, token), archiveHash))
                throw new IOException("Prüfsumme des Archivs fehlgeschlagen.");

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var matches = zip.Entries.Where(e =>
                    e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1 || matches[0].Length > MaxBytes)
                    throw new IOException("Die erwartete DLL fehlt, ist mehrdeutig oder überschreitet das Größenlimit.");
                await using var source = matches[0].Open();
                await WriteLimitedAsync(source, dllPath, token).ConfigureAwait(false);
            }

            if (catalogEntry is not null) VerifyCatalogFile(dllPath, catalogEntry, token);
            else if (!Valid(dllPath, dllHash, null, token))
                throw new IOException("DLL-Inhalt oder PE-Architektur stimmt nicht mit dem Paket überein.");
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(target);
            File.Move(dllPath, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(dllPath);
            File.Delete(zipPath);
            Directory.Delete(work);
        }
    }

    private static byte[] ParseHash(string digest)
    {
        if (digest.Length != 64) throw new ArgumentException("Ein SHA-256-Wert muss 64 Hex-Zeichen enthalten.");
        return Convert.FromHexString(digest);
    }

    private static bool Valid(string path, string hash, DllCatalogEntry? entry, CancellationToken token)
    {
        if (SafePaths.Attributes(path) is null) return false;
        if (entry is null)
            return DllDetectionService.Inspect(path, token) is
                { IsDll: true, Machine: Machine.Amd64 } metadata && metadata.Sha256 == hash;
        try { VerifyCatalogFile(path, entry, token); return true; }
        catch (IOException) { return false; }
    }

    private static string? Digest(string path, bool md5, CancellationToken token)
    {
        if (!md5) return DurableFiles.Hash(path, token);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DurableFiles.Hash(stream, token, HashAlgorithmName.MD5);
    }

    private static async Task WriteLimitedAsync(Stream source, string path, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int count;
        while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += count;
            if (total > MaxBytes) throw new IOException("Die Datei überschreitet das Größenlimit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        output.Flush(flushToDisk: true);
    }
}
