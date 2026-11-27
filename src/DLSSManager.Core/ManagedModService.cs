using System.Text.Json;

namespace DLSSManager.Core;

public sealed record ModVersion(string Label, string Proxy, Guid Snapshot, string ManifestSha256, string? ReleaseTag = null)
{
    public string Display => $"{Label} · {Proxy}";
}

public sealed record ModRevision(string Slot, Guid? Predecessor, ModVersion? Version, bool UpdatesPaused);

public sealed class ManagedModService(FileTransactions transactions, string snapshotStorage, ModIniProfiles? profiles = null)
{
    private sealed record SnapshotFile(string Path, string Sha256);

    public TransactionJournal[] History(Installation game, string executable) =>
        transactions.ModHistory(game.Root, CanonicalExecutable(game, executable));

    public byte[]? InstalledIniBase(Installation game, string executable)
    {
        var version = History(game, executable).FirstOrDefault()?.Mod?.Version;
        if (version is null) return null;
        var ini = Load(version).Files.Single(f => f.RelativePath.Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase));
        return ModIniService.ReadVerified(ini.Source, ini.Sha256);
    }

    public Guid? Install(Installation game, string executable, string frameGeneration, ModPackage package,
        string label, string? releaseTag = null, bool newerOnly = false, CancellationToken token = default)
    {
        var slot = CanonicalExecutable(game, executable);
        var current = History(game, executable).FirstOrDefault();
        if (newerOnly && (current?.Mod is not { UpdatesPaused: false, Version.ReleaseTag: { } currentTag } ||
            !DllVersionService.IsUpgrade(currentTag.TrimStart('v', 'V'), releaseTag?.TrimStart('v', 'V'))))
            return null;

        var previous = current?.Mod?.Version is { } installed ? Load(installed) : package;
        if (current?.Mod?.Version is { } existing && !current.Mod.UpdatesPaused &&
            existing.Label == label && existing.ReleaseTag == releaseTag &&
            StringComparer.OrdinalIgnoreCase.Equals(existing.Proxy, package.Proxy) &&
            previous.Files.Select(f => (f.RelativePath, f.Sha256)).ToHashSet()
                .SetEquals(package.Files.Select(f => (f.RelativePath, f.Sha256))))
            return new ModInstallationService(transactions, profiles).Install(game, executable, frameGeneration,
                previous, previous, token, new(slot, current.Id, existing, false), recordUnchanged: false);
        var version = Save(package, label, releaseTag, token);
        return new ModInstallationService(transactions, profiles).Install(game, executable, frameGeneration, Load(version),
            previous, token, new(slot, current?.Id, version, false));
    }

    public Guid? Remove(Installation game, string executable, ModPackage? legacy = null,
        CancellationToken token = default)
    {
        var slot = CanonicalExecutable(game, executable);
        var current = History(game, executable).FirstOrDefault();
        var previous = current?.Mod?.Version is { } installed ? Load(installed) : legacy;
        if (previous is null) return null;
        return new ModInstallationService(transactions, profiles).Uninstall(game, executable, previous, token,
            new(slot, current?.Id, null, false));
    }

    public Guid? Restore(Installation game, string executable, string frameGeneration, Guid revision,
        CancellationToken token = default)
    {
        var slot = CanonicalExecutable(game, executable);
        var history = History(game, executable);
        var current = history.FirstOrDefault();
        if (current is null || current.Id == revision) return null;
        var version = history.FirstOrDefault(j => j.Id == revision)?.Mod?.Version
            ?? throw new IOException("Die ausgewählte SM86-Version gehört nicht zu dieser Installation.");
        var previous = current.Mod!.Version is { } installed ? Load(installed) : null;
        return new ModInstallationService(transactions, profiles).Install(game, executable, frameGeneration,
            Load(version), previous, token, new(slot, current.Id, version, true));
    }

    public Guid? ResumeUpdates(Installation game, string executable, CancellationToken token = default)
    {
        var current = History(game, executable).FirstOrDefault();
        if (current?.Mod is not { UpdatesPaused: true, Version: not null } revision) return null;
        return transactions.Apply(game, [], token, mod: new(CanonicalExecutable(game, executable),
            current.Id, revision.Version, false));
    }

    private static string CanonicalExecutable(Installation game, string executable)
    {
        var root = SafePaths.Root(game.Root);
        // SM86 is installed beside the executable, so executables in the same directory share one slot.
        return Path.GetRelativePath(root, Path.GetDirectoryName(SafePaths.Child(root, executable))!);
    }

    private ModVersion Save(ModPackage package, string label, string? releaseTag, CancellationToken token)
    {
        var root = SafePaths.Root(snapshotStorage);
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid();
        var directory = SafePaths.Child(root, id.ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < package.Files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = package.Files[i];
                var path = SafePaths.Child(directory, FormattableString.Invariant($"{i}.bin"));
                DurableFiles.Copy(file.Source, path, token);
                if (DurableFiles.Hash(path, token) != file.Sha256)
                    throw new IOException($"SM86-Paketdatei wurde verändert: {file.RelativePath}");
            }
            var manifest = SafePaths.Child(directory, "manifest.json");
            DurableFiles.Json(manifest, package.Files.Select(f => new SnapshotFile(f.RelativePath, f.Sha256)).ToArray());
            return new(label, package.Proxy, id, DurableFiles.Hash(manifest, token)!, releaseTag);
        }
        catch
        {
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    private ModPackage Load(ModVersion version)
    {
        var directory = SafePaths.Child(SafePaths.Root(snapshotStorage), version.Snapshot.ToString("N"));
        var manifest = SafePaths.Child(directory, "manifest.json");
        using var stream = File.Open(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (DurableFiles.Hash(stream, CancellationToken.None) != version.ManifestSha256)
            throw new IOException("SM86-Paketsicherung wurde verändert.");
        stream.Position = 0;
        var files = JsonSerializer.Deserialize<SnapshotFile[]>(stream)
            ?? throw new IOException("SM86-Paketsicherung ist ungültig.");
        return new(version.Proxy, files.Select((file, index) => new ModPackageFile(file.Path,
            SafePaths.Child(directory, FormattableString.Invariant($"{index}.bin")), file.Sha256)).ToArray());
    }
}
