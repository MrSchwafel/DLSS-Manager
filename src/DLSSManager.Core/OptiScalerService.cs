namespace DLSSManager.Core;

public sealed class OptiScalerService(FileTransactions transactions, ModIniProfiles? profiles = null)
{
    internal static readonly string[] SupportFiles = ["OptiScaler.ini", "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_vk.dll",
        "libxell.dll", "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "fakenvapi.dll", "fakenvapi.ini"];
    public static readonly IReadOnlyList<string> Proxies = ["dxgi.dll", "version.dll", "winmm.dll", "d3d12.dll"];
    private const string Component = "OptiScaler";
    private const string IniName = "OptiScaler.ini";

    public TransactionJournal? Installed(string root) =>
        transactions.ComponentHead(Component, SafePaths.Root(root)) is { ComponentRemoved: false } active ? active : null;

    public string? InstalledProxy(TransactionJournal journal) => InstalledProxyEntry(journal)?.Path;

    public TransactionEntry? InstalledProxyEntry(TransactionJournal journal) => transactions.ComponentHistory(journal)
        .SelectMany(j => j.Entries)
        .DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(e => e.After is not null && IsProxy(e.Path));

    public static string? PackageReleaseTag(DllCatalogEntry package)
    {
        const string prefix = "OptiScaler ";
        if (package.Published is null || !package.Source.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var tag = package.Source[prefix.Length..];
        return DllVersionService.Parse(tag.TrimStart('v', 'V')) is null ? null : tag;
    }

    public Guid Install(Installation game, string executable, DllCatalogEntry package, string proxy,
        CancellationToken token, Guid? expectedInstallation = null)
    {
        if (!Proxies.Contains(proxy))
            throw new IOException("Einen unterstützten OptiScaler-Proxy wählen.");
        if (!game.Executables.Any(e => SafePaths.Child(game.Root, e).Equals(
            SafePaths.Child(game.Root, executable), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Die Ziel-EXE gehört nicht zur bestätigten Installation.");
        if (package is not { Kind: Component, LocalPath: { } source })
            throw new IOException("OptiScaler.dll zuerst in den Katalog importieren.");
        var metadata = DllDownloadService.VerifyCatalogFile(source, package, token);
        var directory = Path.GetDirectoryName(SafePaths.Child(game.Root, executable))!;
        var target = Path.GetRelativePath(game.Root, SafePaths.Child(directory, proxy));
        var predecessor = transactions.ComponentHead(Component, SafePaths.Root(game.Root));
        var existing = predecessor is { ComponentRemoved: false } ? predecessor : null;
        if (expectedInstallation is Guid expected && existing?.Id != expected)
            throw new IOException("Die OptiScaler-Zuordnung wurde zwischenzeitlich geändert.");
        if (profiles is null && existing is not null) throw new IOException("OptiScaler wird bereits für diese Installation verwaltet.");
        var oldProxy = existing is null ? null : InstalledProxy(existing);
        if (oldProxy is not null && !string.Equals(Path.GetDirectoryName(oldProxy), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            throw new IOException("OptiScaler ist an eine andere Spiel-EXE gebunden. Zum Wechsel zuerst entfernen.");

        var history = existing is null ? [] : transactions.ComponentHistory(existing);
        var owned = history.SelectMany(j => j.Entries).DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var changes = new List<FileChange>();
        Add(target, source, metadata.Sha256);
        if (oldProxy is not null && !oldProxy.Equals(target, StringComparison.OrdinalIgnoreCase))
            Add(oldProxy, null, null);

        var support = SafePaths.Root(source + ".files");
        var iniSource = SafePaths.Child(support, IniName);
        var original = profiles is null ? null : File.Exists(iniSource) ? ModIniService.ReadFile(iniSource) :
            existing?.IniBase is { } oldBase ? profiles.ReadOptiBase(oldBase) : ModIniProfiles.Defaults(IniMod.OptiScaler);
        var iniBase = original is null ? null : profiles!.RememberOptiBase(original);
        using var merged = original is null ? null : profiles!.Stage(profiles.Merge(IniMod.OptiScaler, game.Root, original));
        if (merged is not null)
            Add(Path.GetRelativePath(game.Root, SafePaths.Child(directory, IniName)), merged.Path, merged.Hash, isIni: true);

        foreach (var name in SupportFiles)
        {
            if (name == IniName && merged is not null) continue;
            var relative = Path.GetRelativePath(game.Root, SafePaths.Child(directory, name));
            var payload = SafePaths.Child(support, name);
            if (SafePaths.Attributes(payload) is null) continue;
            if (!owned.ContainsKey(relative) && SafePaths.Attributes(SafePaths.Child(game.Root, relative)) is not null) continue;
            var hash = DurableFiles.Hash(payload, token);
            if (name.EndsWith(".dll", StringComparison.Ordinal) && DllDetectionService.Inspect(payload, token) is not
                { IsDll: true, Machine: System.Reflection.PortableExecutable.Machine.Amd64 })
                throw new IOException($"Ungültige OptiScaler-Paketdatei: {name}");
            Add(relative, payload, hash);
        }
        var tag = PackageReleaseTag(package);
        if (changes.Count == 0 && existing is not null && existing.IniBase == iniBase && existing.ComponentReleaseTag == tag)
            return existing.Id;
        return transactions.Apply(game, changes, token, component: Component, componentPredecessor: predecessor?.Id,
            iniBase: iniBase, componentReleaseTag: tag);

        void Add(string relative, string? payload, string? hash, bool isIni = false)
        {
            var current = DurableFiles.Hash(SafePaths.Child(game.Root, relative), token);
            if (!isIni && current != owned.GetValueOrDefault(relative)?.After)
                throw new IOException($"Fremde oder geänderte Datei blockiert OptiScaler: {relative}");
            if (current != hash) changes.Add(new(relative, current, payload, hash));
        }
    }

    public void Remove(Installation game, Guid expected, CancellationToken token = default)
    {
        var journal = Installed(game.Root);
        if (journal?.Id != expected)
            throw new IOException("Die OptiScaler-Zuordnung hat sich geändert.");
        if (profiles is null && journal.ComponentPredecessor is null)
        {
            transactions.Rollback(game, expected);
            return;
        }

        var changes = new List<FileChange>();
        var history = transactions.ComponentHistory(journal);
        var paths = history.SelectMany(j => j.Entries.Select((entry, index) => (journal: j, entry, index)))
            .GroupBy(x => x.entry.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            var latest = path.First().entry;
            var first = path.Last();
            var current = DurableFiles.Hash(SafePaths.Child(game.Root, path.Key), token);
            if (current == first.entry.Before) continue;
            var isIni = Path.GetFileName(path.Key).Equals(IniName, StringComparison.OrdinalIgnoreCase);
            if (!isIni && current != latest.After)
                throw new IOException($"Fremdänderung blockiert OptiScaler-Deinstallation: {path.Key}");
            changes.Add(new(path.Key, current, first.entry.Before is null ? null :
                transactions.ComponentOriginal(first.journal, first.index), first.entry.Before));
        }
        transactions.Apply(game, changes, token, component: Component, componentPredecessor: expected, componentRemoved: true);
    }

    private static bool IsProxy(string path) => Proxies.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
}
