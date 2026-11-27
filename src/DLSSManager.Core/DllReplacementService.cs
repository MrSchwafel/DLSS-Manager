using System.Reflection.PortableExecutable;

namespace DLSSManager.Core;

public sealed class DllReplacementService(FileTransactions transactions)
{
    public sealed record Update(string RelativePath, string CurrentHash, string Payload, string PayloadHash);

    public Guid? Replace(Installation installation, string relativePath, string expectedCurrentSha256,
        string payload, string expectedPayloadSha256, CancellationToken token = default) =>
        ReplaceAll(installation, [new(relativePath, expectedCurrentSha256, payload, expectedPayloadSha256)], token);

    public Guid? ReplaceAll(Installation installation, IEnumerable<Update> updates, CancellationToken token = default)
    {
        var changes = new List<FileChange>();
        foreach (var update in updates)
        {
            var target = Resolve(installation, update.RelativePath);
            var original = RequireDll(target, update.CurrentHash, token);
            var replacement = RequireDll(update.Payload, update.PayloadHash, token);
            if (original.Sha256 == replacement.Sha256) continue;

            var backup = SafePaths.Child(SafePaths.Root(installation.Root), update.RelativePath + ".bak");
            if (SafePaths.Attributes(backup) is null)
                changes.Add(new(update.RelativePath + ".bak", null, target, original.Sha256));
            else
                RequireDll(backup, expectedHash: null, token);
            changes.Add(new(update.RelativePath, original.Sha256, update.Payload, replacement.Sha256));
        }
        return changes.Count == 0 ? null : transactions.Apply(installation, changes, token);
    }

    public Guid? RestoreOriginal(Installation installation, string relativePath, string expectedCurrentSha256,
        string expectedBackupSha256, CancellationToken token = default) =>
        RestoreAll(installation, [(relativePath, expectedCurrentSha256, expectedBackupSha256)], token);

    public Guid? RestoreAll(Installation installation,
        IEnumerable<(string RelativePath, string CurrentHash, string BackupHash)> files, CancellationToken token = default)
    {
        var changes = new List<FileChange>();
        foreach (var (relativePath, currentHash, backupHash) in files)
        {
            var target = Resolve(installation, relativePath);
            var current = RequireDll(target, currentHash, token);
            var backup = SafePaths.Child(SafePaths.Root(installation.Root), relativePath + ".bak");
            var original = RequireDll(backup, backupHash, token);
            if (original.Sha256 != current.Sha256)
                changes.Add(new(relativePath, current.Sha256, backup, original.Sha256));
        }
        return changes.Count == 0 ? null : transactions.Apply(installation, changes, token);
    }

    private static string Resolve(Installation installation, string relative)
    {
        var root = SafePaths.Root(installation.Root);
        var target = SafePaths.Child(root, relative);
        if (DllDetectionService.UpscalerKind(Path.GetFileName(target)) is not ("SR" or "FG" or "RR" or "XeSS" or "FSR") ||
            Path.GetRelativePath(root, target).Split(Path.DirectorySeparatorChar)
                .Contains("dlssg_sm86", StringComparer.OrdinalIgnoreCase))
            throw new IOException("Nur native Upscaling-Dateien können einzeln ausgetauscht werden.");
        return target;
    }

    private static DllMetadata RequireDll(string path, string? expectedHash, CancellationToken token)
    {
        var dll = DllDetectionService.Inspect(path, token);
        if (!dll.IsDll || dll.Machine != Machine.Amd64)
            throw new IOException("Datei ist keine x64-PE-DLL.");
        if (expectedHash is not null && !StringComparer.OrdinalIgnoreCase.Equals(expectedHash, dll.Sha256))
            throw new IOException("SHA-256-Prüfung der DLL fehlgeschlagen.");
        return dll;
    }
}
