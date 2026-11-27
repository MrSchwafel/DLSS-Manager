using static DLSSManager.Core.LocalizationService;
using System.Reflection.PortableExecutable;

namespace DLSSManager.Core;

public sealed class LocalDllCatalog(string directory)
{
    private sealed record Provenance(string Source, DateTimeOffset Date);

    public DllCatalogEntry Import(string source, CancellationToken token, string? release = null, DateTimeOffset? date = null)
    {
        var name = Path.GetFileName(source);
        var kind = DllDetectionService.UpscalerKind(name)
            ?? throw new IOException(GetText("LocalDllCatalog.UnknownDLL", name));
        var metadata = Inspect(source, token);
        var root = SafePaths.Root(directory);
        Directory.CreateDirectory(root);
        var target = SafePaths.Child(root, $"{metadata.Sha256}-{name}");
        if (File.Exists(target))
        {
            if (DurableFiles.Hash(target, token) != metadata.Sha256)
                throw new IOException(GetText("LocalDllCatalog.TheLocalCatalogFileIsCorrupt"));
        }
        else
        {
            var staged = SafePaths.Child(root, $"{Guid.NewGuid():N}.tmp");
            try
            {
                DurableFiles.Copy(source, staged, token);
                if (DurableFiles.Hash(staged, token) != metadata.Sha256)
                    throw new IOException(GetText("LocalDllCatalog.TheDLLChangedDuringImport"));
                File.Move(staged, target);
            }
            finally { File.Delete(staged); }
        }
        if (release is not null && date is { } published)
            DurableFiles.Json(target + ".json", new Provenance(release, published), overwrite: true);
        return Entry(target, kind, name, metadata);
    }

    public DllCatalogEntry[] Read(CancellationToken token)
    {
        var root = SafePaths.Root(directory);
        if (!Directory.Exists(root)) return [];
        var entries = new List<DllCatalogEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*.dll"))
        {
            token.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(file);
            if (fileName.Length < 66 || fileName[64] != '-') continue;
            var name = fileName[65..];
            if (DllDetectionService.UpscalerKind(name) is not { } kind) continue;
            try
            {
                var metadata = Inspect(file, token);
                if (fileName.StartsWith(metadata.Sha256, StringComparison.OrdinalIgnoreCase))
                    entries.Add(Entry(file, kind, name, metadata));
            }
            catch (IOException) { }
        }
        return [.. entries];
    }

    private static DllMetadata Inspect(string path, CancellationToken token)
    {
        var metadata = DllDetectionService.Inspect(path, token);
        if (!metadata.IsDll || metadata.Machine != Machine.Amd64)
            throw new IOException(GetText("LocalDllCatalog.OnlyX64PEDLLsCanBeImported"));
        return metadata;
    }

    private static DllCatalogEntry Entry(string path, string kind, string name, DllMetadata metadata)
    {
        Provenance? provenance = null;
        try
        {
            var sidecar = path + ".json";
            SafePaths.RejectLinks(sidecar);
            if (File.Exists(sidecar) && new FileInfo(sidecar).Length <= 4096)
                provenance = System.Text.Json.JsonSerializer.Deserialize<Provenance>(File.ReadAllText(sidecar));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        return new(kind, name, metadata.NumericFileVersion ?? new Version(0, 0), new Uri(path), "", "",
            provenance?.Source ?? "Lokaler Import", new FileInfo(path).Length, Sha256: metadata.Sha256, LocalPath: path,
            Published: provenance?.Date);
    }
}
