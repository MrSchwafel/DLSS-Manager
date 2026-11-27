using System.Diagnostics;
using System.Text.Json;

namespace DLSSManager.Core;

public sealed record FileChange(string RelativePath, string? ExpectedSha256, string? Payload, string? PayloadSha256);
public sealed record Installation(string Root, string[] Executables);
public enum TransactionState { Applying, Committed, RollingBack, RolledBack }
public sealed record TransactionEntry(string Path, string? Before, string? After);
public sealed record TransactionJournal(Guid Id, string Root, TransactionEntry[] Entries,
    int Armed, TransactionState State, int SchemaVersion = 1, string[]? CreatedDirectories = null, ModRevision? Mod = null,
    string? Component = null, Guid? ComponentPredecessor = null, bool ComponentRemoved = false, string? IniBase = null, string? ComponentReleaseTag = null);
public interface IInstallationGuard { void RequireClosed(Installation installation); }

public sealed class GameRunningException(string message) : IOException(message);

public sealed class WindowsInstallationGuard : IInstallationGuard
{
    public void RequireClosed(Installation installation)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (installation.Executables.Length == 0) throw new IOException("Keine verifizierte Spiel-EXE hinterlegt.");
        var names = installation.Executables.Select(path =>
        {
            var executable = SafePaths.Child(installation.Root, path);
            if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                throw new IOException($"Spiel-EXE konnte nicht bestätigt werden: {path}");
            return Path.GetFileNameWithoutExtension(executable);
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                if (process.Id is 0 or 4) continue;
                try
                {
                    if (names.Contains(process.ProcessName))
                        throw new GameRunningException("Spiel oder gleichnamiger Prozess läuft.");
                    var executable = process.MainModule?.FileName;
                    if (executable is not null && SafePaths.Contains(installation.Root, executable))
                        throw new GameRunningException("Ein Prozess aus dem Installationsordner läuft.");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A named game process is blocked above even if its executable path is unreadable.
                    if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: not (5 or 299 or 87) })
                        throw new IOException("Prozesszustand konnte nicht geprüft werden.", ex);
                }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

public sealed class FileTransactions(string journalStorage, IInstallationGuard guard)
{
    public Guid Apply(Installation installation, IReadOnlyList<FileChange> changes,
        CancellationToken token = default, bool createTargetDirectories = false, ModRevision? mod = null,
        string? component = null, Guid? componentPredecessor = null, bool componentRemoved = false, string? iniBase = null, string? componentReleaseTag = null)
    {
        var (root, storage) = Roots(installation);
        installation = installation with { Root = root };
        using var transactionLock = Lock(storage);
        RequireNoPending(storage, root);
        if (mod is not null && LatestMod(storage, root, mod.Slot)?.Id != mod.Predecessor)
            throw new IOException("Die SM86-Zuordnung wurde zwischenzeitlich geändert.");
        if (component is not null)
        {
            var heads = ComponentHeads(Journals(storage).Select(x => x.Journal), component);
            if (heads.Any(x => !x.ComponentRemoved && Overlap(x.Root, root) &&
                !x.Root.Equals(root, StringComparison.OrdinalIgnoreCase)) ||
                heads.SingleOrDefault(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase))?.Id != componentPredecessor)
                throw new IOException($"Die {component}-Zuordnung wurde zwischenzeitlich geändert.");
        }
        if (changes.Count == 0 && mod is null && component is null)
            throw new ArgumentException("Dateiplan ist leer.", nameof(changes));
        guard.RequireClosed(installation);

        var entries = new List<TransactionEntry>();
        var missingDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var id = Guid.NewGuid();
        var folder = SafePaths.Child(storage, id.ToString("N"));
        Directory.CreateDirectory(folder);
        TransactionJournal journal;
        try
        {
            for (var i = 0; i < changes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var change = changes[i];
                var target = SafePaths.Child(root, change.RelativePath);
                if (!targets.Add(target))
                    throw new IOException($"Doppeltes Ziel: {change.RelativePath}");
                for (var parent = Path.GetDirectoryName(target)!; !Directory.Exists(parent); parent = Path.GetDirectoryName(parent)!)
                {
                    if (!createTargetDirectories || change.Payload is null || SafePaths.Attributes(parent) is not null)
                        throw new IOException($"Zielordner fehlt oder ist blockiert: {parent}");
                    missingDirectories.Add(parent);
                }
                var before = NormalizeHash(change.ExpectedSha256);
                var after = NormalizeHash(change.PayloadSha256);
                if ((change.Payload is null) != (after is null) || before == after)
                    throw new IOException("Dateiplan enthält eine leere oder inkonsistente Änderung.");
                RequireHash(target, before);
                if (before is not null) CopyVerified(target, Asset(folder, i, "original"), before, token);
                if (after is not null) CopyVerified(Path.GetFullPath(change.Payload!), Asset(folder, i, "payload"), after, token);
                entries.Add(new(change.RelativePath, before, after));
            }
            if (missingDirectories.Overlaps(targets))
                throw new IOException("Datei- und Ordnerziele überschneiden sich.");
            token.ThrowIfCancellationRequested();
            journal = new TransactionJournal(id, root, [.. entries], 0, TransactionState.Applying,
                SchemaVersion: mod is null ? (missingDirectories.Count == 0 ? 1 : 2) : 3,
                CreatedDirectories: missingDirectories.OrderBy(p => p.Length).Select(p => Path.GetRelativePath(root, p)).ToArray(),
                Mod: mod, Component: component, ComponentPredecessor: componentPredecessor,
                ComponentRemoved: componentRemoved, IniBase: iniBase, ComponentReleaseTag: componentReleaseTag);
            Save(folder, journal);
        }
        catch
        {
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
            throw;
        }
        try
        {
            foreach (var directory in journal.CreatedDirectories!)
            {
                token.ThrowIfCancellationRequested();
                guard.RequireClosed(installation);
                Directory.CreateDirectory(SafePaths.Child(root, directory));
            }
            for (var i = 0; i < journal.Entries.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = journal.Entries[i];
                var target = SafePaths.Child(root, entry.Path);
                journal = journal with { Armed = i + 1 };
                Save(folder, journal);
                Exchange(installation, folder, i, target, entry.Before, entry.After, "payload", token);
            }
            Save(folder, journal with { State = TransactionState.Committed });
        }
        catch (Exception original)
        {
            try { Restore(installation, folder, journal); }
            catch (Exception restoreError)
            {
                throw new AggregateException($"Änderung {id} fehlgeschlagen; Rollback bleibt ausstehend.",
                    original, restoreError);
            }
            throw;
        }
        return id;
    }

    public TransactionJournal[] ModHistory(string root, string executable)
    {
        var storage = SafePaths.Root(journalStorage);
        if (!Directory.Exists(storage)) return [];
        using var transactionLock = Lock(storage);
        return CommittedMods(storage, SafePaths.Root(root), executable).ToArray();
    }

    private static TransactionJournal? LatestMod(string storage, string root, string executable) =>
        CommittedMods(storage, root, executable).FirstOrDefault();

    private static IEnumerable<TransactionJournal> CommittedMods(string storage, string root, string executable)
    {
        var records = Journals(storage).Select(x => x.Journal).Where(j =>
            j.State == TransactionState.Committed && j.Mod is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(j.Mod.Slot, j.Root),
                Path.GetFullPath(executable, root))).ToDictionary(j => j.Id);
        if (records.Count == 0) yield break;
        var visited = new HashSet<Guid>();
        var ancestors = records.Values.Select(j => j.Mod!.Predecessor).OfType<Guid>().ToHashSet();
        var heads = records.Values.Where(j => !ancestors.Contains(j.Id)).ToArray();
        if (heads.Length != 1) throw new IOException("Die SM86-Versionshistorie ist mehrdeutig.");
        TransactionJournal? record = heads[0];
        while (record is not null)
        {
            if (!visited.Add(record.Id))
                throw new IOException("Die SM86-Versionshistorie enthält einen Zyklus.");
            yield return record;
            if (record.Mod!.Predecessor is not Guid id) break;
            if (!records.TryGetValue(id, out record))
                throw new IOException("Eine frühere SM86-Transaktion fehlt.");
        }
        if (visited.Count != records.Count)
            throw new IOException("Die SM86-Versionshistorie ist unvollständig.");

    }

    public TransactionJournal[] Pending()
    {
        var storage = SafePaths.Root(journalStorage);
        if (!Directory.Exists(storage)) return [];
        using var transactionLock = Lock(storage);
        return Journals(storage).Select(x => x.Journal)
            .Where(x => x.State is TransactionState.Applying or TransactionState.RollingBack).ToArray();
    }

    public void DeleteOriginalBackup(Installation installation, string relativeDll, long expectedSize,
        DateTime? expectedWriteTime, CancellationToken token = default)
    {
        var (root, storage) = Roots(installation);
        using var transactionLock = Lock(storage);
        RequireNoPending(storage, root);
        var target = SafePaths.Child(root, relativeDll);
        if (!target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Das Backup muss zu einer DLL gehören.");
        var backup = new FileInfo(SafePaths.Child(root, relativeDll + ".bak"));
        if (!backup.Exists) return;
        if (expectedWriteTime is { } writeTime && (backup.Length != expectedSize || backup.LastWriteTimeUtc != writeTime))
            throw new IOException("Das Backup wurde seit dem Scan geändert. Bitte erneut scannen.");
        guard.RequireClosed(installation with { Root = root });
        token.ThrowIfCancellationRequested();
        SafePaths.RejectLinks(backup.FullName);
        backup.Delete();
    }

    private static void RequireNoPending(string storage, string root)
    {
        if (Journals(storage).Any(x => Overlap(x.Journal.Root, root) &&
            x.Journal.State is TransactionState.Applying or TransactionState.RollingBack))
            throw new IOException("Ausstehendes Rollback für diese Installation.");
    }

    public TransactionJournal[] InstalledComponents(string component, string? root = null)
    {
        var storage = SafePaths.Root(journalStorage);
        if (!Directory.Exists(storage)) return [];
        using var transactionLock = Lock(storage);
        return ComponentHeads(Journals(storage).Select(x => x.Journal), component)
            .Where(x => !x.ComponentRemoved && (root is null || x.Root.Equals(root, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    internal TransactionJournal? ComponentHead(string component, string root)
    {
        var storage = SafePaths.Root(journalStorage);
        if (!Directory.Exists(storage)) return null;
        using var transactionLock = Lock(storage);
        return ComponentHeads(Journals(storage).Select(x => x.Journal), component)
            .SingleOrDefault(x => x.Root.Equals(root, StringComparison.OrdinalIgnoreCase));
    }

    private static TransactionJournal[] ComponentHeads(IEnumerable<TransactionJournal> journals, string component)
    {
        var records = journals.Where(x => x.State == TransactionState.Committed && x.Component == component).ToArray();
        var ancestors = records.Select(x => x.ComponentPredecessor).OfType<Guid>().ToHashSet();
        return records.Where(x => !ancestors.Contains(x.Id)).ToArray();
    }

    internal TransactionJournal[] ComponentHistory(TransactionJournal head)
    {
        var storage = SafePaths.Root(journalStorage);
        using var transactionLock = Lock(storage);
        var history = new List<TransactionJournal> { head };
        var visited = new HashSet<Guid> { head.Id };
        while (history[^1].ComponentPredecessor is Guid id)
        {
            var previous = Load(SafePaths.Child(storage, id.ToString("N")));
            if (!visited.Add(id) || previous.Component != head.Component || !previous.Root.Equals(head.Root, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Ungültige Komponenten-Historie.");
            if (previous.ComponentRemoved) break;
            history.Add(previous);
        }
        return history.ToArray();
    }

    internal string ComponentOriginal(TransactionJournal journal, int index) =>
        Asset(SafePaths.Child(SafePaths.Root(journalStorage), journal.Id.ToString("N")), index, "original");

    public void Rollback(Installation installation, Guid id)
    {
        var (root, storage) = Roots(installation);
        using var transactionLock = Lock(storage);
        var folder = SafePaths.Child(storage, id.ToString("N"));
        var journal = Load(folder);
        if (!StringComparer.OrdinalIgnoreCase.Equals(root, journal.Root))
            throw new IOException("Journal gehört zu einer anderen Installation.");
        if (journal.State == TransactionState.RolledBack) return;
        if (journal.State == TransactionState.Committed && journal.Mod is not null &&
            LatestMod(storage, root, journal.Mod.Slot)?.Id != id)
            throw new IOException("Eine neuere SM86-Transaktion ist vorhanden.");
        if (journal.State == TransactionState.Committed && journal.Component is { } component)
        {
            var heads = ComponentHeads(Journals(storage).Select(x => x.Journal), component);
            if (!heads.Any(x => x.Id == id) ||
                heads.Any(x => x.Id != id && !x.ComponentRemoved && Overlap(x.Root, root)))
                throw new IOException("Eine neuere Komponenten-Transaktion ist vorhanden.");
        }
        // Do not roll back an older committed operation across an incomplete overlapping operation.
        if (Journals(storage).Any(x => x.Journal.Id != id && Overlap(x.Journal.Root, root) &&
                x.Journal.State is TransactionState.Applying or TransactionState.RollingBack))
            throw new IOException("Eine andere Transaktion wartet auf Wiederherstellung.");
        Restore(installation with { Root = root }, folder, journal);
    }

    private (string Root, string Storage) Roots(Installation installation)
    {
        var root = SafePaths.Root(installation.Root);
        var storage = SafePaths.Root(journalStorage);
        if (!Directory.Exists(root) || Path.GetPathRoot(root) == root || Overlap(root, storage))
            throw new IOException("Installations- und Journalordner müssen getrennt und eindeutig sein.");
        Directory.CreateDirectory(storage);
        return (root, storage);
    }

    private static bool Overlap(string a, string b) => SafePaths.Contains(a, b) || SafePaths.Contains(b, a);

    private static FileStream Lock(string storage) => File.Open(SafePaths.Child(storage, "transactions.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static string Asset(string folder, int index, string kind) =>
        SafePaths.Child(folder, FormattableString.Invariant($"{index}.{kind}"));

    private static void Save(string folder, TransactionJournal journal) =>
        DurableFiles.Json(SafePaths.Child(folder, "journal.json"), journal, overwrite: true);

    private static IEnumerable<(string Folder, TransactionJournal Journal)> Journals(string storage)
    {
        foreach (var dir in Directory.EnumerateDirectories(storage))
            if (Guid.TryParseExact(Path.GetFileName(dir), "N", out _) &&
                SafePaths.Attributes(SafePaths.Child(dir, "journal.json")) is not null)
                yield return (dir, Load(dir));
    }

    private static TransactionJournal Load(string folder)
    {
        var journal = JsonSerializer.Deserialize<TransactionJournal>(
            File.ReadAllText(SafePaths.Child(folder, "journal.json")));
        if (journal is not { SchemaVersion: 1 or 2 or 3, Entries: not null } ||
            (journal.Entries.Length == 0 && journal.Mod is null && journal.Component is null) ||
            (journal.SchemaVersion == 3) != (journal.Mod is not null) ||
            journal.Id.ToString("N") != Path.GetFileName(folder) || !Path.IsPathFullyQualified(journal.Root) ||
            journal.Armed < 0 || journal.Armed > journal.Entries.Length || !Enum.IsDefined(journal.State) ||
            journal.Entries.Select(x => SafePaths.Child(journal.Root, x.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Entries.Length)
            throw new InvalidDataException("Ungültiges Transaktionsjournal.");

        if (journal.Mod is { } mod &&
            (string.IsNullOrWhiteSpace(mod.Slot) || (mod.Slot != "." && !SafePaths.Contains(journal.Root,
                SafePaths.Child(journal.Root, mod.Slot)))))
            throw new InvalidDataException("Ungültiger SM86-Zielordner im Journal.");
        var directories = journal.CreatedDirectories ?? [];
        if (directories.Length > 0 && journal.SchemaVersion == 1 ||
            directories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != directories.Length ||
            directories.Any(directory =>
            {
                var path = SafePaths.Child(journal.Root, directory);
                return !journal.Entries.Any(entry => entry.Before is null && entry.After is not null &&
                    SafePaths.Contains(path, Path.GetDirectoryName(SafePaths.Child(journal.Root, entry.Path))!));
            }))
            throw new InvalidDataException("Ungültige Transaktionsordner im Journal.");
        return journal;
    }

    private void Restore(Installation installation, string folder, TransactionJournal journal)
    {
        var preserved = new HashSet<int>();
        // Preflight the whole rollback before modifying any target.
        for (var i = 0; i < journal.Armed; i++)
        {
            var entry = journal.Entries[i];
            var current = DurableFiles.Hash(SafePaths.Child(installation.Root, entry.Path));
            if (current != entry.Before && current != entry.After)
            {
                if (journal.Component == "OptiScaler" && journal.State != TransactionState.Applying && entry.Before is null &&
                    Path.GetFileName(entry.Path).Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
                    preserved.Add(i);
                else throw new IOException($"Fremdänderung blockiert Rollback: {entry.Path}");
            }
            if (entry.Before is not null) RequireHash(Asset(folder, i, "original"), entry.Before);
        }
        Save(folder, journal with { State = TransactionState.RollingBack });
        for (var i = journal.Armed - 1; i >= 0; i--)
        {
            var entry = journal.Entries[i];
            var target = SafePaths.Child(installation.Root, entry.Path);
            if (!preserved.Contains(i) && DurableFiles.Hash(target) != entry.Before)
                Exchange(installation, folder, i, target, entry.After, entry.Before, "original");
            var staged = StagePath(folder, target);
            if (DurableFiles.Hash(staged) is { } hash)
            {
                if (hash != entry.Before && hash != entry.After)
                    throw new IOException($"Unbekannte Staging-Datei: {staged}");
                guard.RequireClosed(installation);
                File.Delete(staged);
            }
        }
        foreach (var directory in (journal.CreatedDirectories ?? []).Reverse())
        {
            var path = SafePaths.Child(installation.Root, directory);
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                guard.RequireClosed(installation);
                Directory.Delete(path);
            }
        }
        Save(folder, journal with { State = TransactionState.RolledBack });
    }

    private void Exchange(Installation installation, string folder, int index, string target,
        string? expected, string? desired, string assetKind, CancellationToken token = default)
    {
        guard.RequireClosed(installation);
        // Stage beside the target for same-volume atomic rename. Its name is derived from the journal.
        var staged = StagePath(folder, target);
        if (desired is not null)
        {
            if (SafePaths.Attributes(staged) is not null)
            {
                // A restart can leave either a prepared payload or a prepared original.
                var hash = DurableFiles.Hash(staged);
                if (hash != desired)
                {
                    if (hash != expected) throw new IOException($"Unbekannte Staging-Datei: {staged}");
                    File.Delete(staged);
                }
            }
            if (SafePaths.Attributes(staged) is null)
            {
                // A cancelled copy must leave no partial staged file for the rollback.
                try { CopyVerified(Asset(folder, index, assetKind), staged, desired, token); }
                catch (OperationCanceledException) { File.Delete(staged); throw; }
            }
        }
        if (desired is not null) guard.RequireClosed(installation);
        RequireHash(target, expected);
        if (desired is null) File.Delete(target);
        else if (expected is null) File.Move(staged, target);
        else File.Replace(staged, target, null);
        RequireHash(target, desired);
    }

    private static string StagePath(string folder, string target) =>
        target + ".dlss-" + Path.GetFileName(folder) + ".tmp";

    private static string? NormalizeHash(string? hash) => hash is null ? null :
        hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToUpperInvariant() :
        throw new ArgumentException("SHA-256 erwartet.");

    private static void CopyVerified(string source, string target, string hash, CancellationToken token)
    {
        DurableFiles.Copy(source, target, token);
        if (DurableFiles.Hash(target, token) != hash)
            throw new IOException($"Dateizustand entspricht nicht dem Plan: {target}");
    }

    private static void RequireHash(string path, string? expected)
    {
        if (DurableFiles.Hash(path) != expected)
            throw new IOException($"Dateizustand entspricht nicht dem Plan: {path}");
    }
}
