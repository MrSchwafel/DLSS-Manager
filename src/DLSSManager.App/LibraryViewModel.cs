using static DLSSManager.Core.LocalizationService;
using System.Windows;
using System.Net.Http;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public sealed record ModHistoryRow(Guid Id, ModVersion Version)
{
    public string Display => Version.Display;
}

public sealed record LibraryRow(string Title, string Path, string Proxy, string Executable, RtxGame? Rtx = null,
    Guid Id = default, string? Icon = null, bool Ignored = false, string Launcher = "Lokal", string Arguments = "",
    ScanGame? Inventory = null, bool UpdateAvailable = false, bool Overlay = false,
    bool ManagedOpti = false)
{
    public static string IconFilter => GetText("Library.GameIconsIcoPngJpgJpegExe");

    public System.Windows.Media.ImageSource? IconImage => GameIcons.Resolve(Icon, Executable, Inventory);

    public string DisplayLauncher => LauncherLabels.FromPath(Path) is var label && label != "Lokal" ? label :
        Launcher switch { "Ubisoft Connect" => "Ubisoft", "Xbox / Game Pass" => "Xbox", "Steam" or "Epic" or "Ubisoft" or "GOG" or "Xbox" => Launcher, _ => "Lokal" };
    public string LauncherLabelKey => LauncherLabels.LabelKey(DisplayLauncher);
    public string SrBadge => VersionBadge("DLSS", "SR");
    public string FgBadge => VersionBadge("FG", "FG");
    public bool HasSr => NativeDll("SR") is not null;
    public bool HasFg => NativeDll("FG") is not null;
    public bool SrOnly => HasSr && !HasFg && NativeDll("RR") is null;
    public bool HasDlss => HasSr || HasFg;
    public bool HasUnlocker => Has("SM86");
    public bool HasOptiScaler => ManagedOpti || Inventory?.OptiScalerPresent == true || Has("OptiScaler");
    public bool Modified => HasOptiScaler || HasUnlocker || HasBackup;
    public bool HasLibraryBadges => HasDlss || Modified;
    public bool HasBackup => Inventory?.Dlls.Any(d => d.HasOriginalBackup) == true;
    public string StateKey => Ignored ? "Library.Excluded" : Modified ? "Library.ModDetected" :
        HasBackup ? "Library.BackupAvailable" : Inventory is null ? "Library.ScanPending" : "Library.InventoryChecked";
    private bool Has(string kind) => Inventory?.Dlls.Any(d => d.Kind == kind && d.Metadata?.IsDll == true) == true;
    private DllFinding? NativeDll(string kind) =>
        Inventory?.Dlls.FirstOrDefault(d => !d.ModComponent && d.Kind == kind && d.Metadata?.IsDll == true);

    private string VersionBadge(string label, string kind) => NativeDll(kind)?.Version is { Length: > 0 } version
        ? $"{label} {version}" : label;

    public static System.Windows.Media.ImageSource? ReadIcon(string? Icon)
    {
        if (string.IsNullOrEmpty(Icon) || !System.IO.Path.IsPathFullyQualified(Icon) || !File.Exists(Icon)) return null;
        try
        {
            if (System.IO.Path.GetExtension(Icon).ToLowerInvariant() is ".exe" or ".dll")
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Icon);
                if (icon is null) return null;
                var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            using var source = File.OpenRead(Icon);
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = source;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or Win32Exception)
        { return null; }
    }

    public string[] RtxCapabilities => (Rtx?.Display ?? "—")
        .Split([",", "·"], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
public sealed record FeatureFilter(string LabelKey, RtxFeature Feature)
{
    public string Label => GetText(LabelKey);
}
public sealed record LibraryFilterCounts(int All, int Dlss, int FrameGen, int Backup, int Unlocker, int OptiScaler);

public sealed partial class LibraryViewModel : INotifyPropertyChanged
{
    private readonly string storage = DataFolders.Active;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly DllCatalogService catalog;
    private readonly GameLibraryService library;
    private readonly RtxDatabaseService rtxService;
    private RtxDatabase? rtx;
    private LibraryRow[] allGames = [];
    private string libraryQuery = "";
    private FeatureFilter? selectedFeature;
    private DllCatalogEntry[] catalogEntries = [];
    private CancellationTokenSource? active;
    private LocalizedText status = Message("Library.Ready");
    private LibraryRow[] games = [];
    private LibraryRow? selectedLibraryGame;
    private ScanGame[] findings = [];
    private LocalizedText[] notes = [];
    private ScanGame? selectedFinding;
    private DllFinding? selectedDll;
    private string? selectedExecutable;
    private DllCatalogEntry? selectedReplacement;
    private double percent;
    private bool indeterminate;
    private int selectedTab;
    private string? modPackageDirectory;
    private string? modReleaseTag;
    private ModHistoryRow[] modHistory = [];
    private ModHistoryRow? selectedModHistory;
    private string[] modProxies = [];
    private string? selectedModProxy;
    private TransactionJournal[] pendingJournals = [];
    private TransactionJournal? selectedJournal;

    public LibraryViewModel()
    {
        library = new(storage);
        ResetLibraryFilters = new Command(() =>
        {
            ResetLibraryFilter();
            return Task.CompletedTask;
        }, () => true);
        CheckStableRelease = new Command(CheckStableReleaseAsync, () => active is null);
        CheckBetaRelease = new Command(() => RunAsync(t => CheckReleaseAsync(ReleaseChannel.Beta, t)), () => active is null);
        ExportDiagnosis = new Command(() => RunAsync(ExportDiagnosisAsync), () => active is null);
        AddGame = new Command(() => RunAsync(t => EditGameAsync(null, t)), () => active is null);
        EditGame = new Command(() => RunAsync(t => EditGameAsync(SelectedLibraryGame, t)), HasGame);
        RemoveGame = new Command(() => RunAsync(t => RemoveGamesAsync(false, t)), HasGame);
        ClearGames = new Command(() => RunAsync(t => RemoveGamesAsync(true, t)), () => active is null && allGames.Length != 0);
        ReloadGames = new Command(() => RunAsync(LoadGamesAsync), () => active is null);
        ToggleIgnored = new Command(() => RunAsync(t =>
        {
            var game = SelectedLibraryGame!;
            return SaveGamesAsync(allGames.Select(g => g.Id == game.Id ? g with { Ignored = !g.Ignored } : g).ToArray(), t);
        }), HasGame);
        LaunchGame = new Command(() => RunAsync(LaunchGameAsync), HasGame);
        ChooseGameIcon = new Command(() => RunAsync(t => ChangeGameIconAsync(false, t)), HasGame);
        ResetGameIcon = new Command(() => RunAsync(t => ChangeGameIconAsync(true, t)),
            () => HasGame() && !string.IsNullOrEmpty(SelectedLibraryGame!.Icon));
        OpenGameFolder = new Command(() => RunAsync(_ => OpenShell(SafePaths.Root(SelectedLibraryGame!.Path))), HasGame);
        catalog = new(Http, Path.Combine(storage, "cache"));
        rtxService = new(Http, Path.Combine(storage, "cache"));
        SelectedFeature = FeatureFilters[0];
        RefreshRtx = new Command(() => RunAsync(RefreshRtxAsync), () => active is null);
        RefreshCatalog = new Command(RefreshCatalogAsync, () => active is null);
        ReplaceDll = new Command(() => ChangeDllAsync(false), () => CanChangeDll() && SelectedReplacement is not null &&
            SelectedFinding?.AntiCheat is not { Length: > 0 });
        RestoreDll = new Command(() => ChangeDllAsync(true), () => CanChangeDll() && SelectedDll!.HasOriginalBackup);
        RefreshRecovery = new Command(() => RunAsync(RefreshRecoveryAsync), () => active is null);
        RollbackPending = new Command(RollbackPendingAsync, () => active is null && SelectedJournal is not null);
        DownloadMod = new Command(DownloadModAsync, () => active is null);
        InstallMod = new Command(() => ChangeModAsync("install"), () => CanChangeMod() && modPackageDirectory is not null &&
            SelectedModProxy is not null && NativeFg() is not null);
        UpdateMod = new Command(() => ChangeModAsync("update"), () => CanChangeMod() && modReleaseTag is not null &&
            SelectedModProxy is not null && NativeFg() is not null);
        RestoreMod = new Command(() => ChangeModAsync("restore"), () => CanChangeMod() && SelectedModHistory is not null && NativeFg() is not null);
        ResumeModUpdates = new Command(() => RunAsync(ResumeModUpdatesAsync), CanChangeMod);
        ReadModHistory = new Command(() => RunAsync(ReadModHistoryAsync), CanChangeMod);
        EditModIni = new Command(() => RunAsync(EditModIniAsync), CanChangeMod);
        RemoveMod = new Command(() => ChangeModAsync("remove"), CanChangeMod);
        ScanLaunchers = new Command(() => ScanAsync(null), () => active is null);
        ScanFolder = new Command(async () =>
        {
            var dialog = new OpenFolderDialog { Title = GetText("Library.ChooseAGameFolderToScan") };
            if (dialog.ShowDialog() == true) await ScanAsync(dialog.FolderName);
        }, () => active is null);
        ScanLibraryGame = new Command(() => SelectedLibraryGame is { } game
            ? ScanAsync(game.Path, game) : Task.CompletedTask,
            () => active is null && SelectedLibraryGame is { } game && Path.IsPathFullyQualified(game.Path));
        Cancel = new Command(() => { RequestCancel(); return Task.CompletedTask; }, () => active is not null);
        InitializeWorkspaceCommands();
    }

    public ICommand CheckStableRelease { get; }
    public ICommand CheckBetaRelease { get; }
    public ICommand ExportDiagnosis { get; }
    public ICommand AddGame { get; }
    public ICommand EditGame { get; }
    public ICommand RemoveGame { get; }
    public ICommand ClearGames { get; }
    public ICommand ReloadGames { get; }
    public ICommand ToggleIgnored { get; }
    public ICommand LaunchGame { get; }
    public ICommand ChooseGameIcon { get; }
    public ICommand ResetGameIcon { get; }
    public ICommand OpenGameFolder { get; }
    private bool HasGame() => active is null && SelectedLibraryGame is not null;

    private async Task LoadGamesAsync(CancellationToken token)
    {
        var entries = await Task.Run(library.Read, token) ?? [];
        allGames = entries.Select(g => new LibraryRow(g.Title, g.Path, g.Proxy, g.Executable,
            Id: g.Id, Icon: g.Icon, Ignored: g.Ignored, Launcher: g.Launcher, Arguments: g.Arguments, Overlay: g.Overlay)).ToArray();
        MatchLibraryFeatures();
        Status = Message("Library.LoadedGames", allGames.Length);
    }

    private async Task SaveGamesAsync(LibraryRow[] rows, CancellationToken token)
    {
        var entries = rows.Select(g => new GameLibraryEntry(g.Id, g.Title, g.Path, g.Executable, g.Proxy, g.Icon,
            g.Ignored, g.Launcher, g.Arguments, g.Overlay)).ToArray();
        await Task.Run(() => library.Save(entries), token);
        allGames = rows;
        MatchLibraryFeatures();
        Status = Message("Library.SavedGames", rows.Length);
    }

    private async Task EditGameAsync(LibraryRow? game, CancellationToken token)
    {
        var dialog = new GameEntryWindow(game, settings.DefaultProxy) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var updated = dialog.Entry!;
        if (allGames.Any(g => g.Id != updated.Id && SamePath(g.Path, updated.Path) &&
            (g.Executable.Length == 0 && updated.Executable.Length == 0 || SamePath(g.Executable, updated.Executable))))
            throw new IOException(GetText("Library.ThisInstallationIsAlreadyInTheLibrary"));
        if (game is null && ExcludeAntiCheatGames)
            await Task.Run(() => GameScanner.RequireModifiable(updated.Path, token), token);
        await SaveGamesAsync(game is null ? [.. allGames, updated] :
            allGames.Select(g => g.Id == game.Id ? updated : g).ToArray(), token);
        SelectedLibraryGame = Games.FirstOrDefault(row => row.Id == updated.Id);
    }

    private async Task RemoveGamesAsync(bool all, CancellationToken token)
    {
        var selected = SelectedLibraryGame;
        if (ConfirmationDialog.Show(Application.Current.MainWindow, all ? GetText("Library.RemoveAllLibraryEntries") :
                GetText("Library.RemoveTheLibraryEntryFor", selected?.Title), GetText("Library.Title"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await SaveGamesAsync(all ? [] : allGames.Where(g => g.Id != selected?.Id).ToArray(), token);
    }

    private async Task ChangeGameIconAsync(bool reset, CancellationToken token)
    {
        var game = SelectedLibraryGame!;
        string? icon = null;
        if (!reset)
        {
            var picker = new OpenFileDialog { Title = GetText("Workspace.ChooseGameIcon"), InitialDirectory = game.Path, Filter = LibraryRow.IconFilter };
            if (picker.ShowDialog(Application.Current.MainWindow) != true) return;
            icon = picker.FileName;
            if (LibraryRow.ReadIcon(icon) is null) throw new IOException(GetText("Library.CouldNotReadAnIconFromThe"));
        }
        await SaveGamesAsync(allGames.Select(row => row.Id == game.Id ? row with { Icon = icon } : row).ToArray(), token);
        Status = reset ? Message("Library.RestoredTheAutomaticGameIcon") : Message("Library.GameIconSaved");
    }

    private async Task LaunchGameAsync(CancellationToken token)
    {
        var game = SelectedLibraryGame!;
        var root = SafePaths.Root(game.Path);
        var executable = game.Executable;
        var select = !File.Exists(executable);
        if (select)
        {
            var pick = new OpenFileDialog { Title = GetText("Library.SelectExecutable"), InitialDirectory = root, Filter = GetText("GameEntry.ProgramsExe") };
            if (pick.ShowDialog() != true) return;
            executable = pick.FileName;
        }
        var path = SafePaths.Child(root, Path.GetRelativePath(root, executable));
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException(GetText("Library.AGameExecutableIsRequired"));
        if (select) await SaveGamesAsync(allGames.Select(g => g.Id == game.Id ? g with { Executable = path } : g).ToArray(), token);
        var start = new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = !game.Overlay, WorkingDirectory = Path.GetDirectoryName(path)!, Arguments = game.Arguments
        };
        if (game.Overlay) start.Environment["__NGX_SHOW_INDICATOR"] = "1024";
        using var process = System.Diagnostics.Process.Start(start);
    }

    internal Task CheckStableReleaseAsync() => RunAsync(t => CheckReleaseAsync(ReleaseChannel.Stable, t));

    private async Task CheckReleaseAsync(ReleaseChannel channel, CancellationToken token)
    {
        Status = Message("Cockpit.CheckingGitHubReleases");
        Indeterminate = true;
        var release = await UpdateService.CheckManagerAsync(Http, AppVersion, channel, token);
        Status = release is null ? Message("Library.NoNewerGitHubVersionWasFoundIn", channel) :
            Message("Library.GitHubReleaseIsAvailableCheckPackageCompatibility", release.Tag);
        Percent = 100;
    }

    private async Task ExportDiagnosisAsync(CancellationToken token)
    {
        var dialog = new SaveFileDialog { Title = GetText("Workspace.ExportDiagnostics"), FileName = "DLSSManager-Diagnose.json", Filter = "JSON|*.json" };
        if (dialog.ShowDialog() != true) return;
        var pending = await Task.Run(() => CreateTransactions().Pending(), token);
        var report = new { CreatedUtc = DateTimeOffset.UtcNow, Version = typeof(LibraryViewModel).Assembly.GetName().Version?.ToString(),
            Windows = Environment.OSVersion.VersionString, Runtime = Environment.Version.ToString(),
            Games = allGames.Length, ScanResults = Findings.Length, PendingTransactions = pending.Length };
        await File.WriteAllTextAsync(dialog.FileName, System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), token);
        Status = Message("Library.DiagnosticsExported");
    }

    private static Task OpenShell(string target, string? workingDirectory = null)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            { UseShellExecute = true, WorkingDirectory = workingDirectory ?? "" });
        return Task.CompletedTask;
    }

    public ICommand RefreshRtx { get; }
    public FeatureFilter[] FeatureFilters { get; } =
    [
        new("Library.AllGames", RtxFeature.None), new("Super Resolution", RtxFeature.SR), new("Frame Generation", RtxFeature.FG),
        new("Multi Frame Generation", RtxFeature.MFG), new("Ray Reconstruction", RtxFeature.RR),
        new("DLAA", RtxFeature.DLAA), new("Ray Tracing", RtxFeature.RT), new("Path Tracing", RtxFeature.PathTracing)
    ];
    public string LibraryQuery
    {
        get => libraryQuery;
        set { Set(ref libraryQuery, value); FilterLibrary(); }
    }
    public FeatureFilter? SelectedFeature
    {
        get => selectedFeature;
        set { Set(ref selectedFeature, value); FilterLibrary(); }
    }
    public string LibraryCount => GetText("Library.Games", Games.Length, allGames.Length);

    public ICommand RefreshCatalog { get; }
    public DllCatalogEntry[] CatalogEntries
    {
        get => catalogEntries;
        private set
        {
            Set(ref catalogEntries, value);
            NotifyReplacementVersions();
            SelectedReplacement = null;
            FilterLibrary();
            RefreshInspector();
            NotifyCatalogSummary();
            Notify(nameof(CatalogCounts));
            Notify(nameof(CatalogRows));
        }
    }

    public ICommand RefreshRecovery { get; }
    public ICommand RollbackPending { get; }
    public TransactionJournal[] PendingJournals
    {
        get => pendingJournals;
        private set
        {
            var selection = SelectedJournal?.Id;
            Set(ref pendingJournals, value);
            SelectedJournal = value.FirstOrDefault(journal => journal.Id == selection) ?? value.FirstOrDefault();
            Notify(nameof(PendingJournalHeading));
            Notify(nameof(HasPendingJournals));
        }
    }
    public string PendingJournalHeading => GetText("Library.PendingTransactions", PendingJournals.Length);
    public bool HasPendingJournals => PendingJournals.Length != 0;
    public TransactionJournal? SelectedJournal
    {
        get => selectedJournal;
        set
        {
            if (selectedJournal == value) return;
            Set(ref selectedJournal, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand DownloadMod { get; }
    public ICommand InstallMod { get; }
    public ICommand UpdateMod { get; }
    public ICommand RestoreMod { get; }
    public ICommand ResumeModUpdates { get; }
    public ICommand ReadModHistory { get; }
    public ModHistoryRow[] ModHistory { get => modHistory; private set => Set(ref modHistory, value); }
    public ModHistoryRow? SelectedModHistory { get => selectedModHistory; set => Set(ref selectedModHistory, value); }
    public ICommand EditModIni { get; }
    public ICommand RemoveMod { get; }
    public string[] ModProxies { get => modProxies; private set { Set(ref modProxies, value); Notify(nameof(HasUnlockerProxies)); } }
    public string? SelectedModProxy { get => selectedModProxy; set => Set(ref selectedModProxy, value); }

    public ICommand ScanLibraryGame { get; }
    public LibraryRow? SelectedLibraryGame
    {
        get => selectedLibraryGame;
        set
        {
            if (selectedLibraryGame == value) return;
            IsPathCopied = false;
            IsExeCopied = false;
            Set(ref selectedLibraryGame, value);
            RefreshInspector();
        }
    }

    public ICommand ReplaceDll { get; }
    public ICommand RestoreDll { get; }
    public DllFinding? SelectedDll
    {
        get => selectedDll;
        set
        {
            Set(ref selectedDll, value);
            NotifyReplacementVersions();
            SelectedReplacement = null;
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string? EffectiveExecutable => SelectedExecutable ?? SelectedFinding?.Executable;
    public bool NeedsExecutable => EffectiveExecutable is null;

    public string? SelectedExecutable
    {
        get => selectedExecutable;
        set
        {
            if (selectedExecutable == value) return;
            Set(ref selectedExecutable, value);
            PropertyChanged?.Invoke(this, new(nameof(EffectiveExecutable)));
            PropertyChanged?.Invoke(this, new(nameof(NeedsExecutable)));
            ModHistory = [];
            SelectedModHistory = null;
        }
    }
    public DllCatalogEntry? SelectedReplacement { get => selectedReplacement; set => Set(ref selectedReplacement, value); }
    private void NotifyReplacementVersions()
    {
        Notify(nameof(ReplacementVersions));
        Notify(nameof(HasReplacementVersions));
        Notify(nameof(ReplacementHint));
    }

    public bool HasReplacementVersions => ReplacementVersions.Length > 0;
    public string? ReplacementHint => SelectedFinding is null ? null : SelectedDll is null ?
        GetText("Library.SelectADLLInTheInventoryFirst") : !HasReplacementVersions ? GetText("Library.NoCatalogVersionsAreLoadedForThis") : null;
    public DllCatalogEntry[] ReplacementVersions => SelectedDll is { } dll && CanReplace(dll)
        ? CatalogEntries.Where(e => e.Kind == dll.Kind).ToArray() : [];

    public ICommand ScanLaunchers { get; }
    public ICommand ScanFolder { get; }
    public ICommand Cancel { get; }
    public LibraryRow[] Games { get => games; private set => Set(ref games, value); }
    public ScanGame[] Findings { get => findings; private set => Set(ref findings, value); }
    public ScanGame? SelectedFinding
    {
        get => selectedFinding;
        set
        {
            if (selectedFinding == value) return;
            ModHistory = [];
            SelectedModHistory = null;
            SelectedDll = null;
            SelectedExecutable = null;
            Set(ref selectedFinding, value);
            Notify(nameof(ReplacementHint));
            PropertyChanged?.Invoke(this, new(nameof(EffectiveExecutable)));
            PropertyChanged?.Invoke(this, new(nameof(NeedsExecutable)));
            if (value?.Executable is null && value?.Alternatives is { Length: 1 } candidates)
                SelectedExecutable = candidates[0];
            var native = value?.Dlls.Where(d => !d.ModComponent && d.Kind is "SR" or "FG" or "RR"
                && d.Metadata is { IsDll: true }).Take(2).ToArray();
            if (native is { Length: 1 }) SelectedDll = native[0];
        }
    }
    public LocalizedText[] Notes { get => notes; private set { Set(ref notes, value); Notify(nameof(Logs)); } }
    public LocalizedText Status
    {
        get => status;
        private set
        {
            Set(ref status, value);
            if (value.Length != 0)
                Activity = [.. Activity.TakeLast(179), $"{DateTime.Now:HH:mm:ss}  " + value];
            Notify(nameof(Logs));
        }
    }
    public double Percent { get => percent; private set => Set(ref percent, value); }
    public bool Indeterminate { get => indeterminate; private set => Set(ref indeterminate, value); }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public Task LoadAsync() => RunAsync(async token =>
    {
        await LoadWorkspaceAsync(token);
        await LoadGamesAsync(token);
        try
        {
            rtx = await Task.Run(rtxService.ReadCached, token);
            MatchLibraryFeatures();
            RefreshIniStatus();
            if (rtx is not null) await ApplyDatabaseFramesAsync(token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Notes = [.. Notes, Message("Library.CouldNotReadTheDLSSdbCache", e.Message)]; }
        try { await LoadCatalogAsync(await Task.Run(catalog.ReadCached, token), token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { Notes = [.. Notes, Message("Library.CouldNotReadTheSavedDLLCatalog", e.Message)]; }
        try { await RefreshRecoveryAsync(CancellationToken.None, reportStatus: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { Notes = [.. Notes, Message("Library.CouldNotReadTheTransactionJournal", e.Message)]; }
    });

    public void RequestCancel()
    {
        active?.Cancel();
        driverDownloadCts?.Cancel();
    }

    private async Task RefreshRtxAsync(CancellationToken token)
    {
        Status = Message("Library.FetchingDLSSdb");
        Indeterminate = true;
        var (database, offline) = await Task.Run(() => rtxService.RefreshAsync(token), token);
        rtx = database;
        RefreshIniStatus();
        MatchLibraryFeatures();
        Status = offline ? Message("Library.DLSSdbGamesFromTheOfflineCache", database.Count) :
            Message("Library.DLSSdbGamesAvailable", database.Count);
        var applied = await ApplyDatabaseFramesAsync(token);
        if (applied > 0) Status += Message("Library.AppliedMFGDataToGames", applied);
        Percent = 100;
    }

    private void MatchLibraryFeatures()
    {
        allGames = allGames.Select(g => g with { Rtx = rtx?.Find(g.Title),
            Inventory = inventories.GetValueOrDefault(g.Path), ManagedOpti = managedOpti.Contains(g.Path) }).ToArray();
        FilterLibrary();
    }

    private void FilterLibrary()
    {
        var selection = SelectedLibraryGame;
        var feature = SelectedFeature?.Feature ?? RtxFeature.None;
        var query = LibraryQuery.Trim();
        var candidates = allGames
            .Where(g => query.Length == 0 ||
                g.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                g.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                g.Inventory?.Dlls.Any(d => d.Version?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) == true)
            .Where(g => feature == RtxFeature.None || g.Rtx?.Features.HasFlag(feature) == true)
            .ToArray();
        FilterCounts = new(candidates.Length, candidates.Count(g => g.HasDlss), candidates.Count(g => g.HasFg),
            candidates.Count(g => g.HasBackup), candidates.Count(g => g.HasUnlocker), candidates.Count(g => g.HasOptiScaler));
        Games = candidates.Select(g => g with { UpdateAvailable = UpdateCandidates(g.Inventory).Any() })
            .Where(g => LibraryFilter switch
            {
                "UpdateAvailable" => !g.Ignored && g.UpdateAvailable,
                "Modified" => g.Modified,
                "WithDlss" => g.HasDlss,
                "WithFrameGeneration" => g.HasFg,
                "ChangedDll" => g.HasBackup,
                "Unlocker" => g.HasUnlocker,
                "OptiScaler" => g.HasOptiScaler,
                "DlssSrOnly" => g.SrOnly,
                "Excluded" => g.Ignored,
                "Steam" or "Epic" or "Ubisoft" or "GOG" or "Xbox" or "Lokal" => g.DisplayLauncher == LibraryFilter,
                _ => true
            }).ToArray();
        if (selection is not null)
            SelectedLibraryGame = Games.FirstOrDefault(g => g.Id == selection.Id);
        SelectedLibraryGame ??= Games.FirstOrDefault();
        PropertyChanged?.Invoke(this, new(nameof(LibraryCount)));
        Notify(nameof(ShowEmptyLibraryFilter));
        Notify(nameof(LibraryStorageSummary));
        Notify(nameof(LibraryBadge));
        Notify(nameof(LibraryHealth));
    }

    private FileTransactions CreateTransactions() =>
        new(DataFolders.Transactions(storage), new WindowsInstallationGuard());

    private Task RefreshRecoveryAsync(CancellationToken token) => RefreshRecoveryAsync(token, reportStatus: true);

    private async Task RefreshRecoveryAsync(CancellationToken token, bool reportStatus)
    {
        PendingJournals = await Task.Run(() => CreateTransactions().Pending(), token);
        if (reportStatus) Status = PendingJournals.Length == 0 ? Message("Library.NoPendingRecoveries") :
            Message("Library.PendingRecoveries", PendingJournals.Length);
        else if (PendingJournals.Length > 0)
            Notes = [.. Notes, Message("Library.AFileTransactionIsPendingRecoveryOpen")];
    }

    private async Task RollbackPendingAsync()
    {
        if (SelectedJournal is not { } journal) return;
        var dialog = new OpenFileDialog
        {
            Title = GetText("Library.SelectTheGameExecutableForTheProcess"),
            InitialDirectory = journal.Root,
            Filter = GetText("Library.WindowsApplicationExeExe")
        };
        if (dialog.ShowDialog() != true) return;
        if (!SafePaths.Contains(journal.Root, dialog.FileName))
        {
            Status = Message("Library.TheSelectedExecutableIsOutsideThisInstallation");
            return;
        }
        var description = GetText("Library.InstallationTransactionGameExecutable", journal.Root, journal.Id, dialog.FileName) +
            GetText("Library.UpToFileOperationsWillBeReverted", journal.Armed);
        if (ConfirmationDialog.Show(System.Windows.Application.Current.MainWindow, description,
                GetText("Library.RecoverFileTransaction"), System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK)
            return;

        await RunAsync(async token =>
        {
            Indeterminate = true;
            Status = Message("Library.RecoveringFileTransaction");
            var installation = new Installation(journal.Root, [Path.GetRelativePath(journal.Root, dialog.FileName)]);
            await Task.Run(() => CreateTransactions().Rollback(installation, journal.Id), token);
            await RefreshRecoveryAsync(CancellationToken.None, reportStatus: false);
            Status = Message("Library.FileTransactionRecovered");
            Percent = 100;
        });
    }

    private Task RefreshCatalogAsync() => RunAsync(RefreshAndCacheCatalogAsync);

    internal Task SyncCatalogAndDatabaseAsync() => RunAsync(async token =>
    {
        await RefreshAndCacheCatalogAsync(token);
        var catalogStatus = Status;
        Percent = 0;
        await RefreshRtxAsync(token);
        Status = catalogStatus + " " + Status;
    });

    private async Task RefreshAndCacheCatalogAsync(CancellationToken token)
    {
        Status = Message("Library.FetchingDLLCatalog");
        Indeterminate = true;
        var result = await Task.Run(() => catalog.RefreshAsync(token), token);
        await LoadCatalogAsync(result.Entries, token);
        var downloader = Downloader();
        var latest = result.Entries.GroupBy(entry => entry.Kind)
            .Select(group => group.MaxBy(entry => entry.Version)!).ToArray();
        var cached = new Dictionary<DllCatalogEntry, string>();
        var failures = new List<string>();
        foreach (var entry in latest)
        {
            Status = Message("Library.CheckingLocalCatalog", entry.Kind, entry.Version);
            try
            {
                var sha256 = await Task.Run(async () =>
                {
                    var path = await downloader.GetAsync(entry, token);
                    return DllDownloadService.VerifyCatalogFile(path, entry, token).Sha256;
                }, token);
                cached[entry] = sha256;
            }
            catch (Exception e) when (IsPackageFailure(e) || e is OperationCanceledException && !token.IsCancellationRequested)
            { failures.Add($"{entry.Kind} {entry.Version}: {e.Message}"); }
        }
        var hashes = cached.ToDictionary(pair => (pair.Key.Kind, pair.Key.DllMd5), pair => pair.Value);
        CatalogEntries = CatalogEntries.Select(entry => entry.LocalPath is null &&
            hashes.TryGetValue((entry.Kind, entry.DllMd5), out var sha256) ? entry with { Sha256 = sha256 } : entry).ToArray();
        Notes = [.. Notes, .. failures.Select(message => (LocalizedText)message)];
        Status = Message("Library.LatestDLLsAvailableLocally", cached.Count, latest.Length) +
            (result.Notice is null ? "" : $" {result.Notice}");
        Percent = 100;
    }

    private Task DownloadModAsync() => RunAsync(async token =>
    {
        Status = Message("Library.DownloadingAndVerifyingTheUnlockerRelease");
        Indeterminate = true;
        await FetchUnlockerAsync(token);
        Status = Message("Library.UnlockerIsVerifiedAndSelectedForInstallation", modReleaseTag);
        Percent = 100;
    });

    private void SelectModPackage(string directory, string[] proxies, string? tag = null)
    {
        modPackageDirectory = directory;
        modReleaseTag = tag;
        Notify(nameof(UnlockerCacheLabel));
        ModProxies = proxies;
        SelectedModProxy = proxies.FirstOrDefault(p => p.Equals("version.dll", StringComparison.OrdinalIgnoreCase))
            ?? proxies[0];
        Notes = [directory];
    }

    private static bool SamePath(string first, string second)
    {
        if (!Path.IsPathFullyQualified(first)) return false;
        try
        {
            return StringComparer.OrdinalIgnoreCase.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)));
        }
        catch (ArgumentException) { return false; }
    }

    private bool CanChangeMod() => active is null && SelectedFinding is { } game && game.AntiCheat is not { Length: > 0 } &&
        (SelectedExecutable ?? game.Executable) is not null;

    private DllFinding? NativeFg() => SelectedFinding?.Dlls.FirstOrDefault(d => d.Kind == "FG" && !d.ModComponent &&
        d.Metadata is { IsDll: true, Machine: System.Reflection.PortableExecutable.Machine.Amd64 });

    private (Installation Game, string Exe) SelectedModTarget()
    {
        var game = SelectedFinding!;
        var executable = Path.GetRelativePath(game.Root.Path, SelectedExecutable ?? game.Executable!);
        return (new(game.Root.Path, [executable]), executable);
    }

    private ManagedModService ManagedMods(int? frames = null) => new(CreateTransactions(), Path.Combine(storage, "sm86-versions"), IniProfiles(frames));

    private async Task ReadModHistoryAsync(CancellationToken token)
    {
        var (game, exe) = SelectedModTarget();
        var history = await Task.Run(() => ManagedMods().History(game, exe), token);
        ShowModHistory(history);
        Status = history.FirstOrDefault()?.Mod is { Version: { } installed } revision
            ? Message("Library.AssociatedUnlocker", installed.Display) + (revision.UpdatesPaused ? Message("Cockpit.UpdatesPaused") : "")
            : Message("Library.ThisInstallationHasNoActiveUnlockerPackage");
    }

    private async Task ResumeModUpdatesAsync(CancellationToken token)
    {
        var (game, exe) = SelectedModTarget();
        var service = ManagedMods();
        var id = await Task.Run(() => service.ResumeUpdates(game, exe, token), token);
        await ReadModHistoryAsync(CancellationToken.None);
        if (id is not null)
        {
            RefreshModCockpit(force: true);
            Status = Message("Library.UnlockerUpdatesResumedForThisInstallation");
        }
    }

    private void ShowModHistory(TransactionJournal[] history)
    {
        var currentSnapshot = history.FirstOrDefault()?.Mod?.Version?.Snapshot;
        ModHistory = history.Skip(1).Where(j => j.Mod?.Version is { } version && version.Snapshot != currentSnapshot)
            .Select(j => new ModHistoryRow(j.Id, j.Mod!.Version!)).DistinctBy(r => r.Version.Snapshot).ToArray();
        SelectedModHistory = null;
    }

    private Task ChangeModAsync(string operation)
    {
        if (!CanChangeMod()) return Task.CompletedTask;
        var finding = SelectedFinding!;
        var (game, exe) = SelectedModTarget();
        var restore = SelectedModHistory;
        var source = modPackageDirectory;
        var proxy = SelectedModProxy;
        var tag = modReleaseTag;
        var frameGeneration = NativeFg();
        if (operation != "remove" && frameGeneration is null) return Task.CompletedTask;
        var relativeFg = frameGeneration is null ? "" : Path.GetRelativePath(game.Root, frameGeneration.Path);

        return RunAsync(async token =>
        {
            Indeterminate = true;
            var service = ManagedMods(RecommendedFrames(allGames.FirstOrDefault(row => SamePath(row.Path, game.Root))?.Title ?? finding.Root.Title));
            Status = Message("Library.CheckingTheAssociatedUnlockerPackage");
            var history = await Task.Run(() => service.History(game, exe), token);
            var tracked = history.FirstOrDefault()?.Mod?.Version;
            var usePackage = operation is "install" or "update" || (operation == "remove" && tracked is null);
            ModPackage? package = null;
            if (usePackage)
            {
                if (source is null || proxy is null)
                    throw new IOException(GetText("Library.SelectTheMatchingUnlockerPackageAndProxy"));
                package = await Task.Run(() => ModPackage.Read(source, proxy, token), token);
            }

            var action = operation switch
            {
                "remove" => GetText("Library.RemoveUnlocker", tracked?.Display ?? proxy),
                "restore" => GetText("Library.UnlockerFallback", restore!.Display),
                "update" => GetText("Library.UnlockerUpdate", tag, proxy),
                _ => GetText("Library.InstallOrRepairUnlocker", tag ?? "SM86", proxy)
            };
            token.ThrowIfCancellationRequested();
            var description = $"{action}\n\n{finding.Root.Title}\n{Path.Combine(game.Root, exe)}";
            if (ConfirmationDialog.Show(System.Windows.Application.Current.MainWindow, description, UnlockerName,
                    System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK)
            {
                Status = Message("Library.Cancelled");
                return;
            }

            var id = await Task.Run(() =>
            {
                if (operation != "remove") GameScanner.RequireModifiable(game.Root, token);
                return operation switch
                {
                    "remove" => service.Remove(game, exe, package, token),
                    "restore" => service.Restore(game, exe, relativeFg, restore!.Id, token),
                    _ => service.Install(game, exe, relativeFg, package!, tag ?? "SM86", tag,
                        newerOnly: operation == "update", token)
                };
            }, token);
            if (id is not null) await RefreshGameInventoryAsync(finding, exe);
            ShowModHistory(await Task.Run(() => service.History(game, exe)));
            Status = id is null
                ? (operation == "update" ? Message("Library.NoNewerUpdateIsAvailableOrUpdates") : Message("Library.NoApplicableSM86Change"))
                : (operation == "restore" ? Message("Library.UnlockerFallbackAppliedUpdatesArePausedFor") : Message("Library.UnlockerChangeComplete"));
            if (id is not null) Notes = [Message("Library.Transaction", id, game.Root)];
            Percent = 100;
        });
    }

    private bool CanChangeDll() =>
        active is null && SelectedFinding is { } game && (SelectedExecutable ?? game.Executable) is not null &&
        (game.AntiCheat is not { Length: > 0 } || SelectedDll?.HasOriginalBackup == true) &&
        SelectedDll is { } dll && CanReplace(dll) && game.Dlls.Contains(dll);

    private Task ChangeDllAsync(bool restore)
    {
        if (!CanChangeDll()) return Task.CompletedTask;
        var game = SelectedFinding!;
        var dll = SelectedDll!;
        var executable = SelectedExecutable ?? game.Executable!;
        var entry = SelectedReplacement;
        if (!restore && (entry is null || entry.Kind != dll.Kind || game.AntiCheat is { Length: > 0 })) return Task.CompletedTask;
        var installation = new Installation(game.Root.Path, [Path.GetRelativePath(game.Root.Path, executable)]);
        var relative = Path.GetRelativePath(installation.Root, dll.Path);
        return RunAsync(async token =>
        {
            Indeterminate = true;
            var targetDescription = GetText("Library.GameExecutable", game.Root.Title, dll.Path, executable);
            string? backupHash = null;
            if (restore)
            {
                Status = Message("Library.VerifyingTheOriginalBackup");
                var backup = await Task.Run(() => DllDetectionService.Inspect(dll.Path + ".bak", token), token);
                if (!backup.IsDll || backup.Machine != System.Reflection.PortableExecutable.Machine.Amd64)
                    throw new IOException(GetText("Library.TheOriginalBackupIsNotAnX64"));
                backupHash = backup.Sha256;
                targetDescription += GetText("Library.RestoreFromBakVersionSHA", dll.Path, backup.FileVersion, backupHash);
            }
            else targetDescription += GetText("Library.ReplaceFileVersionWith", dll.Version, entry!.Version);
            if (ConfirmationDialog.Show(System.Windows.Application.Current.MainWindow, targetDescription,
                    restore ? GetText("Library.RestoreOriginalDLL") : GetText("Workspace.ReplaceDLL"),
                    System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.Cancel) != System.Windows.MessageBoxResult.OK)
            {
                Status = Message("Library.Cancelled");
                return;
            }

            Status = restore ? Message("Library.RestoringOriginalDLL") : Message("Library.VerifyingPackageAndReplacingDLL");
            var id = await Task.Run(async () =>
            {
                var guard = new WindowsInstallationGuard();
                guard.RequireClosed(installation);
                var transactions = new FileTransactions(DataFolders.Transactions(storage), guard);
                var service = new DllReplacementService(transactions);
                if (restore)
                    return service.RestoreOriginal(installation, relative, dll.Metadata!.Sha256, backupHash!, token);
                GameScanner.RequireModifiable(installation.Root, token);
                var download = new DllDownloadService(Http, Path.Combine(storage, "Katalog", "DLLs"));
                var path = await download.GetAsync(entry!, token);
                var metadata = DllDownloadService.VerifyCatalogFile(path, entry!, token);
                return service.Replace(installation, relative, dll.Metadata!.Sha256, path, metadata.Sha256, token);
            }, token);
            Status = id is null ? Message("Library.TheSelectedFileAlreadyMatches") :
                restore ? Message("Library.OriginalDLLRestored") : Message("Library.DLLReplaced");
            if (id is not null) Notes = [Message("Library.Transaction", id, dll.Path)];
            if (id is not null) Toast = Status;

            if (id is not null)
            {
                // Refresh the committed state even when cancellation arrived after the commit.
                var updated = await Task.Run(() =>
                {
                    var file = new FileInfo(dll.Path);
                    var backup = new FileInfo(dll.Path + ".bak");
                    var metadata = DllDetectionService.Inspect(dll.Path);
                    return dll with
                    {
                        Metadata = metadata, Size = file.Length, ModifiedUtc = file.LastWriteTimeUtc,
                        HasOriginalBackup = backup.Exists, BackupSize = backup.Exists ? backup.Length : 0,
                        BackupDate = backup.Exists ? backup.LastWriteTimeUtc : null
                    };
                });
                var updatedGame = game with { Dlls = game.Dlls.Select(d => d == dll ? updated : d).ToArray() };
                Findings = Findings.Select(g => g == game ? updatedGame : g).ToArray();
                SelectedFinding = updatedGame;
                SelectedExecutable = executable == game.Executable ? null : executable;
                SelectedDll = updated;
                await RememberInventoriesAsync([updatedGame]);
            }
            Percent = 100;
        });
    }

    internal Task ScanLibraryAsync() => ScanAsync(null, quick: true);

    private Task ScanAsync(string? folder, LibraryRow? libraryGame = null, bool drives = false, bool quick = false) => RunAsync(async token =>
    {
        Status = Message("Library.PreparingScan");
        var progress = new Progress<ScanProgress>(p =>
        {
            if (active?.Token != token || token.IsCancellationRequested || p.Current.Length == 0) return;
            Percent = p.RootsTotal == 0 ? 0 : 100d * p.RootsCompleted / p.RootsTotal;
            Status = Message("Library.InstallationsN0FoldersN0Files", p.RootsCompleted, p.RootsTotal, p.Directories, p.Files);
        });
        var result = await Task.Run(() =>
        {
            var warnings = new List<string>();
            var roots = new List<ScanRoot>();
            if (libraryGame is not null)
            {
                var exe = Path.IsPathFullyQualified(libraryGame.Executable)
                    ? Path.GetRelativePath(libraryGame.Path, libraryGame.Executable) : null;
                if (exe is not null && (Path.IsPathRooted(exe) || exe.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    exe = null;
                roots.Add(new(libraryGame.Title, libraryGame.Path, libraryGame.Launcher, exe));
            }
            else if (folder is not null) roots.Add(new(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)), folder, "Manuell"));
            else
            {
                using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                using var steamMachine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if ((steamKey?.GetValue("SteamPath") ?? steamMachine?.GetValue("InstallPath")) is string steam)
                    roots.AddRange(LauncherDiscovery.Steam(steam, warnings.Add, token));
                var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
                if (Directory.Exists(epic)) roots.AddRange(LauncherDiscovery.Epic(epic, warnings.Add, token));
                roots.AddRange(WindowsLauncherDiscovery.Installed(warnings.Add, token));
            }
            if (folder is null && libraryGame is null)
            {
                roots.AddRange(settings.ScanFolders.Where(Path.IsPathFullyQualified).Select(p => new ScanRoot(Path.GetFileName(p), p, "Manuell")));
                roots.AddRange(allGames.Where(g => !g.Ignored && !IsNonGameLauncherEntry(g) && Path.IsPathFullyQualified(g.Path) &&
                    !roots.Any(r => SamePath(r.Path, g.Path))).ToArray().Select(g => new ScanRoot(g.Title, g.Path, g.DisplayLauncher,
                        Path.IsPathFullyQualified(g.Executable) ? Path.GetRelativePath(g.Path, g.Executable) : null)));
            }
            var exclusions = folder is null ? allGames.Where(g => g.Ignored).Select(g => g.Path).ToArray() : [];
            if (drives)
            {
                var driveRoots = WindowsLauncherDiscovery.GameDrives().Select(drive => new ScanRoot(drive, drive, "Laufwerk")).ToArray();
                var scanner = new GameScanner(Path.Combine(storage, "cache"));
                string[] blocked = [];
                var installations = scanner.Scan(new(roots, exclusions, blocked), progress, token);
                var extra = scanner.Scan(new(driveRoots, [.. exclusions, .. roots.Select(r => r.Path),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), storage], blocked), progress, token);
                return extra with { Games = [.. installations.Games, .. extra.Games],
                    Warnings = [.. warnings, .. installations.Warnings, .. extra.Warnings],
                    Metrics = new(extra.Metrics.WallMs + installations.Metrics.WallMs, extra.Metrics.CpuMs + installations.Metrics.CpuMs,
                        extra.Metrics.ManagedBytesDelta + installations.Metrics.ManagedBytesDelta,
                        extra.Metrics.WorkingSetDelta + installations.Metrics.WorkingSetDelta,
                        extra.Metrics.HandleDelta + installations.Metrics.HandleDelta, extra.Metrics.Directories + installations.Metrics.Directories,
                        extra.Metrics.Files + installations.Metrics.Files, extra.Metrics.MetadataReads + installations.Metrics.MetadataReads,
                        extra.Metrics.CacheHits + installations.Metrics.CacheHits) };
            }
            var scanned = new GameScanner(Path.Combine(storage, "cache"))
                .Scan(new(roots, exclusions, []), progress, token);
            return scanned with { Warnings = [.. warnings, .. scanned.Warnings] };
        }, token);
        DetectUpdateDrift(result.Games);
        Findings = result.Games.Where(g =>
            !LauncherDiscovery.IsKnownLauncherTool(g.Root.Title, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.Root.Path))) &&
            (CanAddScanGame(g) || allGames.Any(row => SamePath(row.Path, g.Root.Path)))).ToArray();
        if (libraryGame is null && !quick) SelectedTab = 3;
        SelectedFinding = Findings.FirstOrDefault();
        await RememberInventoriesAsync(result.Games);
        RefreshModCockpit(force: true);
        var added = folder is null && libraryGame is null
            ? await AddLauncherDiscoveriesAsync(Findings.Where(CanAddScanGame).ToArray(), token) : 0;
        var excluded = result.Games.Count(g => !CanAddScanGame(g) &&
            !allGames.Any(row => SamePath(row.Path, g.Root.Path)));
        lastScan = DateTime.Now;
        await WriteSettingsAsync(current => current with { LastScan = lastScan });
        Notify(nameof(LastScanLabel));
        Notify(nameof(LibraryStorageSummary));
        var m = result.Metrics;
        Status = Message("Library.ScanCompleteResultsNewLauncherGames", Findings.Length, added) +
            (excluded == 0 ? "." : Message("Library.ExcludedWithAntiCheat", excluded));
        if (quick) Toast = Status;
        Notes = [Message("Library.CPUN0MsWorkingSetN0KiB", m.CpuMs, m.WorkingSetDelta / 1024, m.HandleDelta, m.MetadataReads, m.CacheHits),
            .. result.Warnings.Take(200).Select(message => (LocalizedText)message), .. result.Warnings.Skip(200).Take(1).Select(_ => Message("Library.AdditionalScanMessages", result.Warnings.Length - 200))];
        Percent = 100;
    }, scanning: true);

    internal bool IsScanning { get; private set; }

    private async Task RunAsync(Func<CancellationToken, Task> action, bool scanning = false)
    {
        if (active is not null) return;
        using var cancellation = new CancellationTokenSource();
        active = cancellation;
        IsScanning = scanning;
        Notify(nameof(IsBusy));
        Percent = 0;
        CommandManager.InvalidateRequerySuggested();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = cancellation.IsCancellationRequested ? Message("Library.Cancelled") : Message("Library.TimeLimitReached"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or Win32Exception or HttpRequestException or AggregateException or System.Text.Json.JsonException or System.Security.SecurityException)
        { Status = Message("Library.OperationFailed", e.Message); }
        finally
        {
            active = null;
            IsScanning = false;
            Notify(nameof(IsBusy));
            Indeterminate = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }

    private sealed class Command(Func<object?, Task> execute, Func<object?, bool> canExecute) : ICommand
    {
        public Command(Func<Task> execute, Func<bool> canExecute) : this(_ => execute(), _ => canExecute()) { }

        public bool CanExecute(object? parameter) => canExecute(parameter);
        public async void Execute(object? parameter) { if (CanExecute(parameter)) await execute(parameter); }
        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
