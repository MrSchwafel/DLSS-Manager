using System.Globalization;
using System.IO;
using System.Windows.Input;
using DLSSManager.Core;

namespace DLSSManager.App;

public sealed record BackupRow(ScanGame Game, DllFinding Dll)
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public string File => Path.GetFileName(Dll.Path) + ".bak";
    public string Target => Dll.Path;
    public string Size => (Dll.BackupSize / 1024d).ToString("N0", German) + " KiB";
    public string Saved => Dll.BackupDate?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", German) ?? "—";
    public string Type => Dll.Kind switch
    {
        "SR" => "DLSS",
        "FG" or "RR" or "FSR" or "XeSS" => Dll.Kind,
        _ => Path.GetFileName(Dll.Path).ToLowerInvariant() switch
        {
            "nvngx_dlss.dll" => "DLSS",
            "nvngx_dlssg.dll" => "FG",
            "nvngx_dlssd.dll" => "RR",
            string name when name.StartsWith("amd_fidelityfx_", StringComparison.Ordinal) => "FSR",
            string name when name.StartsWith("libxess", StringComparison.Ordinal) => "XeSS",
            _ => "DLL"
        }
    };
}

public sealed record BackupGame(ScanRoot Root, int Count, BackupRow[] Rows, bool Expanded)
{
    public string CountLabel => Count == 1 ? "1 Backup" : $"{Count} Backups";
    public string ShortPath => Root.Path.Length > 72 ? $"{Root.Path[..24]}…{Root.Path[^47..]}" : Root.Path;
}

public sealed partial class LibraryViewModel
{
    private string backupQuery = "";
    private int backupMode;

    public BackupRow[] Backups { get; private set; } = [];
    public BackupGame[] BackupGames { get; private set; } = [];
    public string RecoverySummary => $"{Backups.Length} {(Backups.Length == 1 ? "Backup" : "Backups")} · " +
        $"{BackupGameCount} {(BackupGameCount == 1 ? "Spiel" : "Spiele")}";
    public bool HasVisibleBackups => BackupGames.Length > 0;
    public string BackupEmptyMessage => Backups.Length == 0 ? "Keine Original-Backups vorhanden." : "Keine Backups zur Suche gefunden.";
    private int BackupGameCount { get; set; }

    public string BackupQuery
    {
        get => backupQuery;
        set
        {
            if (backupQuery == value) return;
            Set(ref backupQuery, value);
            FilterBackupGames();
        }
    }

    public int BackupMode
    {
        get => backupMode;
        set
        {
            if (backupMode == value) return;
            Set(ref backupMode, value);
            FilterBackupGames();
        }
    }

    public ICommand RestoreFile { get; private set; } = null!;
    public ICommand RestoreGame { get; private set; } = null!;
    public ICommand RestoreAllGames { get; private set; } = null!;
    public ICommand DeleteBackup { get; private set; } = null!;

    private void InitializeRecoveryCommands()
    {
        RestoreFile = new Command(row => ExecuteRestore([(BackupRow)row!]), row => !IsBusy && row is BackupRow);
        RestoreGame = new Command(card => ExecuteRestore(Backups
            .Where(row => SamePath(row.Game.Root.Path, ((BackupGame)card!).Root.Path)).ToArray()),
            card => !IsBusy && card is BackupGame);
        RestoreAllGames = new Command(() => ExecuteRestore(Backups), () => !IsBusy && Backups.Length > 0);
        DeleteBackup = new Command(row => DeleteBackupAsync((BackupRow)row!), row => !IsBusy && row is BackupRow);
    }

    private void RefreshBackups()
    {
        Backups = inventories.Values.SelectMany(game => game.Dlls.Where(dll => dll.HasOriginalBackup)
            .Select(dll => new BackupRow(game, dll)))
            .OrderBy(row => row.Game.Root.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(row => row.File, StringComparer.OrdinalIgnoreCase).ToArray();
        BackupGameCount = Backups.Select(row => row.Game.Root.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        FilterBackupGames();
        Notify(nameof(RecoverySummary));
        Notify(nameof(BackupBadge));
        Notify(nameof(LibraryStorageSummary));
    }

    private void FilterBackupGames()
    {
        var query = BackupQuery.Trim();
        BackupGames = Backups.GroupBy(row => row.Game.Root.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var root = group.First().Game.Root;
                var gameMatches = root.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    root.Path.Contains(query, StringComparison.OrdinalIgnoreCase);
                var rows = group.Where(row => gameMatches ||
                    row.File.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    row.Type.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                return new BackupGame(root, group.Count(), rows, BackupMode == 0 || query.Length > 0);
            })
            .Where(game => game.Rows.Length > 0).ToArray();
        Notify(nameof(BackupGames));
        Notify(nameof(HasVisibleBackups));
        Notify(nameof(BackupEmptyMessage));
    }

    private Task DeleteBackupAsync(BackupRow row) => RunAsync(async token =>
    {
        if (!Confirm("Backup-Datei endgültig löschen", $"{row.Target}.bak\n\nDieses Original-Backup endgültig löschen?")) return;
        var installation = InstallationFor(row.Game);
        await Task.Run(() => CreateTransactions().DeleteOriginalBackup(installation,
            Path.GetRelativePath(installation.Root, row.Target), row.Dll.BackupSize, row.Dll.BackupDate, token), token);
        await RefreshGameInventoryAsync(row.Game, installation.Executables[0]);
        Toast = Status = $"{row.File} wurde gelöscht.";
    });
}
