using static DLSSManager.Core.LocalizationService;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public sealed record PresetChoice(string LabelKey, uint Value)
{
    public string Label => GetText(LabelKey);
}

public sealed class InspectorDll(LibraryViewModel model, ScanGame game, DllFinding dll, DllCatalogEntry[] versions)
{
    private DllCatalogEntry? target;
    private readonly string[] folders = Path.GetDirectoryName(Path.GetRelativePath(game.Root.Path, dll.Path))?
        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries) ?? [];

    public string Name => Path.GetFileName(dll.Path);
    public string TypeLabel => dll.Kind == "SR" ? "DLSS" : dll.Kind;
    public bool IsPluginCopy => folders.Contains("Plugins", StringComparer.OrdinalIgnoreCase);
    internal bool IsInPrimaryLocation => folders.Length == 0 ||
        folders.Zip(folders.Skip(1)).Any(pair =>
            pair.First.Equals("Binaries", StringComparison.OrdinalIgnoreCase) &&
            pair.Second.Equals("Win64", StringComparison.OrdinalIgnoreCase));
    public string FolderLabelKey => folders.Length == 0 ? "Library.GameFolder" : "\\" + string.Join("\\", folders);
    public string SecurityLabelKey => dll.Metadata?.OfficialSignature == true ? "Workspace.OfficialSignature" : "Workspace.UnsignedOrUnverified";
    public string SecurityIcon => dll.Metadata?.OfficialSignature == true ? "\uEA18" : "\uE7BA";
    public string SecurityColor => dll.Metadata?.OfficialSignature == true ? "#A6D962" : "#F7C56C";
    public DllFinding File => dll;
    public DllCatalogEntry[] Versions { get; } = versions;
    public bool HasVersions => Versions.Length > 0;
    public DllCatalogEntry? Target
    {
        get => target;
        set
        {
            target = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public ICommand Apply { get; } = model.DllCommand(game, dll, () => model.InspectorDlls
        .FirstOrDefault(row => ReferenceEquals(row.File, dll))?.Target);
}

public sealed partial class LibraryViewModel
{
    private readonly Dictionary<string, ScanGame> inventories = new(StringComparer.OrdinalIgnoreCase);
    private ManagerSettings settings = new([]);
    private DllCatalogEntry[] onlineCatalog = [];
    private string libraryFilter = "All";
    private LibraryFilterCounts filterCounts = new(0, 0, 0, 0, 0, 0);
    private DateTime? lastScan;
    private LocalizedText[] activity = [];
    private LocalizedText toast = "";
    private string toastKind = "Success";
    private LocalizedText toastActionLabel = "";
    private Func<Task>? toastAction;
    private LocalizedText gpu = Message("Workspace.DetectingGraphicsCard");
    private DllCatalogEntry? selectedOptiPackage;
    private bool overlay;
    private LocalizedText presetStatus = Message("Workspace.ReadNVIDIAApplicationProfile");
    private bool isPathCopied;
    private bool isExeCopied;

    public bool IsPathCopied { get => isPathCopied; private set => Set(ref isPathCopied, value); }
    public bool IsExeCopied { get => isExeCopied; private set => Set(ref isExeCopied, value); }

    public string LibraryFilter
    {
        get => libraryFilter;
        set { Set(ref libraryFilter, value); FilterLibrary(); }
    }
    public string LibraryBadge => allGames.Length.ToString();
    public LibraryFilterCounts FilterCounts
    {
        get => filterCounts;
        private set => Set(ref filterCounts, value);
    }
    public bool ShowEmptyLibraryFilter => Games.Length == 0 &&
        (LibraryFilter != LibraryFilterOptions[0].Code || !string.IsNullOrWhiteSpace(LibraryQuery) ||
            SelectedFeature?.Feature is not (null or RtxFeature.None));
    public ICommand ResetLibraryFilters { get; }

    private void ResetLibraryFilter()
    {
        Set(ref libraryFilter, LibraryFilterOptions[0].Code, nameof(LibraryFilter));
        Set(ref libraryQuery, "", nameof(LibraryQuery));
        Set(ref selectedFeature, FeatureFilters[0], nameof(SelectedFeature));
        FilterLibrary();
    }
    public bool IsBusy => active is not null;
    public string AppVersion => "v" + typeof(LibraryViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0];
    public string LibraryStorageSummary => GetText("Workspace.BackedUpDLLsLastScan", Backups.Length, lastScan?.ToString("g", Culture) ?? "—");
    public LocalizedText[] Activity { get => activity; private set => Set(ref activity, value); }
    public string[] Logs => [.. Notes.Concat(Activity.Reverse()).Select(message => message.Text)];
    private System.Windows.Threading.DispatcherTimer? toastTimer;
    public LocalizedText Toast
    {
        get => toast;
        private set
        {
            toastTimer?.Stop();
            toastAction = null;
            Set(ref toastActionLabel, (LocalizedText)"", nameof(ToastActionLabel));
            Notify(nameof(HasToastAction));
            Set(ref toastKind, "Success", nameof(ToastKind));
            Set(ref toast, value);
            if (value.Length == 0)
            {
                CommandManager.InvalidateRequerySuggested();
                return;
            }
            EnsureToastTimer(5);
            toastTimer!.Start();
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public string ToastKind { get => toastKind; private set => Set(ref toastKind, value); }
    public LocalizedText ToastActionLabel
    {
        get => toastActionLabel;
        private set
        {
            Set(ref toastActionLabel, value);
            Notify(nameof(HasToastAction));
        }
    }
    public bool HasToastAction => ToastActionLabel.Length > 0;

    private void EnsureToastTimer(int seconds)
    {
        toastTimer ??= new();
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        toastTimer.Tick -= OnToastTimerTick;
        toastTimer.Tick += OnToastTimerTick;
    }

    private void OnToastTimerTick(object? sender, EventArgs args) => ClearToast();

    internal void ClearToast()
    {
        toastTimer?.Stop();
        toastAction = null;
        ToastActionLabel = "";
        ToastKind = "Success";
        Set(ref toast, (LocalizedText)"", nameof(Toast));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void SetPinnedToast(LocalizedText message, string kind = "Active", LocalizedText? actionLabel = null, Func<Task>? action = null)
    {
        toastTimer?.Stop();
        toastAction = action;
        ToastActionLabel = actionLabel ?? "";
        ToastKind = kind;
        Set(ref toast, message, nameof(Toast));
        CommandManager.InvalidateRequerySuggested();
    }

    internal void SetTimedToast(LocalizedText message, string kind = "Success", int seconds = 6, LocalizedText? actionLabel = null, Func<Task>? action = null)
    {
        toastTimer?.Stop();
        toastAction = action;
        ToastActionLabel = actionLabel ?? "";
        ToastKind = kind;
        Set(ref toast, message, nameof(Toast));
        if (message.Length != 0)
        {
            EnsureToastTimer(seconds);
            toastTimer!.Start();
        }
        CommandManager.InvalidateRequerySuggested();
    }

    internal void ShowStatusToast() => Toast = Status;
    public LocalizedText Gpu { get => gpu; private set => Set(ref gpu, value); }
    public string DataPath => storage;
    public bool ScanLaunchersOnStartup => settings.ScanLaunchersOnStartup;
    public bool ExcludeAntiCheatGames => settings.ExcludeAntiCheatGames;
    public Task ScanOnStartupAsync() => ScanLaunchersOnStartup ? ScanAsync(null, quick: true) : Task.CompletedTask;
    private bool CanAddScanGame(ScanGame game) => !ExcludeAntiCheatGames || game.AntiCheat is not { Length: > 0 };
    public string ScanPaths => settings.ScanFolders.Length == 0 ? GetText("Workspace.NoAdditionalFolders") : string.Join(Environment.NewLine, settings.ScanFolders);
    public string BatchTargets => settings.BatchTargets is { Length: > 0 } targets ?
        string.Join("  ·  ", targets.Select(t => $"{t.Kind} {t.Version}")) : GetText("Workspace.LatestOnlineVersionPerDLLType");
    public string DefaultProxy { get; set; } = "";
    public string? AntiCheatNotice => SelectedLibraryGame?.Inventory?.AntiCheat is { Length: > 0 } names ?
        GetText("Workspace.AntiCheatDetectedChangingFilesFromThe", string.Join(", ", names)) : null;
    public bool OptiInstalled => optiInstalledProxy is not null ||
        SelectedLibraryGame is { } row && managedOpti.Contains(row.Path);
    public string OptiStatus => OptiInstalled ? optiInstalledProxy is { } proxy ? GetText("Workspace.Installed", proxy) : GetText("Drivers.Installed") :
        SelectedLibraryGame?.Inventory?.OptiScalerPresent == true ? GetText("Workspace.OptiScalerFilesDetected") : GetText("Cockpit.NotInstalled");
    public bool OptiHasFiles => OptiInstalled || SelectedLibraryGame?.Inventory?.OptiScalerPresent == true;
    public string OptiBadge => OptiInstalled ? GetText("Drivers.Installed") : OptiHasFiles ? GetText("Cockpit.Detected") : GetText("Cockpit.Missing");
    public string OptiInstallLabel => OptiHasFiles ? GetText("Cockpit.Reinstall") : GetText("Cockpit.Install");

    private void NotifyOptiCard()
    {
        Notify(nameof(OptiInstalled));
        Notify(nameof(OptiStatus));
        Notify(nameof(OptiHasFiles));
        Notify(nameof(OptiBadge));
        Notify(nameof(OptiInstallLabel));
    }

    public bool OverlayEnabled { get => overlay; private set { Set(ref overlay, value); Notify(nameof(OverlayStatus)); } }
    public string OverlayStatus => OverlayEnabled ? GetText("Workspace.ON") : GetText("Workspace.OFF");
    public PresetChoice[] Presets { get; } = [new("Workspace.ProfileDefault", 0),
        .. Enumerable.Range(1, 15).Select(n => new PresetChoice(n is >= 10 ? $"{(char)('A' + n - 1)} · Transformer" :
            $"{(char)('A' + n - 1)}", (uint)n)), new("Workspace.LatestDriverPreset", 0x00ffffff)];
    private PresetChoice? gamePreset;
    public PresetChoice? GamePreset
    {
        get => gamePreset;
        set
        {
            if (gamePreset == value) return;
            Set(ref gamePreset, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public LocalizedText PresetStatus { get => presetStatus; private set => Set(ref presetStatus, value); }
    public bool GameOverlay => SelectedLibraryGame?.Overlay == true;
    public bool HasInventory => SelectedLibraryGame?.Inventory is not null;
    public string InventoryCount => InspectorDlls.Length == 1 ? GetText("Workspace.FileDetected") : GetText("Workspace.FilesDetected", InspectorDlls.Length);
    public string? InventoryStatus => !HasInventory ? GetText("Workspace.ScanDLLsToDetectInstalledVersions") :
        InspectorDlls.Length == 0 ? GetText("Workspace.NoSupportedUpscalingDLLsFound") :
        CatalogEntries.Length == 0 ? GetText("Workspace.LoadTargetVersionsInTheDLLCatalog") : null;
    public bool HasBatchTargets => settings.BatchTargets is { Length: > 0 };
    public InspectorDll[] InspectorDlls { get; private set; } = [];
    public bool HasInspectorDlls => InspectorDlls.Length > 0;
    public DllCatalogEntry[] OptiPackages => CatalogEntries.Where(c => c.Kind == "OptiScaler").ToArray();
    public DllCatalogEntry? SelectedOptiPackage
    {
        get => selectedOptiPackage;
        set
        {
            Set(ref selectedOptiPackage, value);
            Notify(nameof(SelectedOptiPackageLabel));
        }
    }
    public string? SelectedOptiPackageLabel => SelectedOptiPackage is { } package
        ? $"{package.Version} · {GetText(package.SourceLabelKey)}" : null;

    public ICommand ToggleStartupScan { get; private set; } = null!;
    public ICommand ToggleAntiCheatFilter { get; private set; } = null!;
    public ICommand ScanDrives { get; private set; } = null!;
    public ICommand AddDiscovery { get; private set; } = null!;
    public ICommand ImportDll { get; private set; } = null!;
    public ICommand ResetBatchTargets { get; private set; } = null!;
    public ICommand BatchUpdate { get; private set; } = null!;
    public ICommand ApplyLibraryBatchTargets { get; private set; } = null!;
    public ICommand RestoreLibraryGame { get; private set; } = null!;
    public ICommand SaveDefaults { get; private set; } = null!;
    public ICommand EditScanPaths { get; private set; } = null!;
    public ICommand ClearScanPaths { get; private set; } = null!;
    public ICommand CopyPath { get; private set; } = null!;
    public ICommand CopyExe { get; private set; } = null!;
    public ICommand OpenDataFolder { get; private set; } = null!;
    public ICommand OpenOptiReleases { get; private set; } = null!;
    public ICommand DownloadOpti { get; private set; } = null!;
    public ICommand InstallOpti { get; private set; } = null!;
    public ICommand RemoveOpti { get; private set; } = null!;
    public ICommand ToggleOverlay { get; private set; } = null!;
    public ICommand ToggleGameOverlay { get; private set; } = null!;
    public ICommand OpenNvidia { get; private set; } = null!;
    public ICommand ReadPreset { get; private set; } = null!;
    public ICommand SavePreset { get; private set; } = null!;
    public ICommand DismissToast { get; private set; } = null!;
    public ICommand InvokeToastAction { get; private set; } = null!;

    private readonly HashSet<string> managedOpti = new(StringComparer.OrdinalIgnoreCase);

    private void InitializeWorkspaceCommands()
    {
        InitializeCockpitCommands();
        InitializeExecutableCommands();
        ToggleStartupScan = new Command(() => RunAsync(t => SaveSettingsAsync(settings with
            { ScanLaunchersOnStartup = !ScanLaunchersOnStartup }, t)), () => !IsBusy);
        ToggleAntiCheatFilter = new Command(() => RunAsync(t => SaveSettingsAsync(settings with
            { ExcludeAntiCheatGames = !ExcludeAntiCheatGames }, t)), () => !IsBusy);
        ScanDrives = new Command(() => ScanAsync(null, drives: true), () => !IsBusy);
        AddDiscovery = new Command(() => RunAsync(AddDiscoveryAsync), () => !IsBusy &&
            SelectedFinding is { } game && CanAddScanGame(game) && !allGames.Any(g => SamePath(g.Path, game.Root.Path)));
        ImportDll = new Command(() => RunAsync(ImportDllAsync), () => !IsBusy);
        ResetBatchTargets = new Command(() => RunAsync(t => SaveSettingsAsync(settings with { BatchTargets = [] }, t)),
            () => !IsBusy && settings.BatchTargets is { Length: > 0 });
        BatchUpdate = new Command(() => RunAsync(BatchUpdateAsync), () => !IsBusy);
        ApplyLibraryBatchTargets = new Command(() => RunAsync(ApplyLibraryBatchTargetsAsync),
            () => HasGame() && HasBatchTargets && SelectedLibraryGame?.Inventory is { } game &&
                ExecutableFor(game) is not null && UpdateCandidates(game, pinnedOnly: true).Any());
        InitializeRecoveryCommands();
        RestoreLibraryGame = new Command(() => ExecuteRestore(Backups.Where(b => SamePath(b.Game.Root.Path, SelectedLibraryGame!.Path)).ToArray()),
            () => HasGame() && SelectedLibraryGame!.HasBackup);
        SaveDefaults = new Command(() => RunAsync(t => SaveSettingsAsync(settings with { DefaultProxy = DefaultProxy }, t)), () => !IsBusy);
        EditScanPaths = new Command(() => RunAsync(EditScanPathsAsync), () => !IsBusy);
        ClearScanPaths = new Command(() => RunAsync(t => SaveSettingsAsync(settings with { ScanFolders = [] }, t)),
            () => !IsBusy && settings.ScanFolders.Length > 0);
        CopyPath = CreateCopyCommand(game => game.Path, copied => IsPathCopied = copied);
        CopyExe = CreateCopyCommand(game => game.Executable, copied => IsExeCopied = copied);
        OpenDataFolder = new Command(() => RunAsync(async t =>
        {
            await Task.Run(() => Directory.CreateDirectory(storage), t);
            await OpenShell(storage);
        }), () => !IsBusy);
        OpenOptiReleases = new Command(() => RunAsync(_ => OpenShell("https://github.com/optiscaler/OptiScaler/releases")), () => !IsBusy);
        DownloadOpti = new Command(() => RunAsync(DownloadOptiAsync), () => !IsBusy);
        InstallOpti = new Command(() => RunAsync(t => ChangeOptiAsync(false, t)), () => HasGame() &&
            SelectedLibraryGame?.Inventory is { } game &&
            ExecutableFor(game) is not null && (!game.OptiScalerPresent || optiInstalledProxy is not null));
        RemoveOpti = new Command(() => RunAsync(t => ChangeOptiAsync(true, t)), () => HasGame() &&
            SelectedLibraryGame?.Inventory is { } game && ExecutableFor(game) is not null && managedOpti.Contains(game.Root.Path));
        ToggleOverlay = new Command(() => RunAsync(ToggleOverlayAsync), () => !IsBusy);
        ToggleGameOverlay = new Command(() => RunAsync(t =>
        {
            var selected = SelectedLibraryGame!;
            return SaveGamesAsync(allGames.Select(g => g.Id == selected.Id ? g with { Overlay = !g.Overlay } : g).ToArray(), t);
        }), HasGame);
        OpenNvidia = new Command(() => RunAsync(_ => OpenNvidiaAsync()), () => !IsBusy);
        ReadPreset = new Command(() => RunAsync(ReadPresetAsync), CanUseNvidiaProfile);
        SavePreset = new Command(() => RunAsync(SavePresetAsync), () => CanUseNvidiaProfile() && GamePreset is not null);
        DismissToast = new Command(() =>
        {
            if (IsDriverDownloading || IsDowngradeDownloading) suppressDownloadToast = true;
            ClearToast();
            return Task.CompletedTask;
        }, () => Toast.Length > 0);
        InvokeToastAction = new Command(async () =>
        {
            var action = toastAction;
            ClearToast();
            if (action is not null) await action();
        }, () => toastAction is not null);
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

    private bool CanUseNvidiaProfile() => HasGame() && SelectedLibraryGame is { Executable.Length: > 0 };

    private async Task ReadPresetAsync(CancellationToken token)
    {
        var game = SelectedLibraryGame!;
        var value = await Task.Run(() => NvidiaProfiles.Read(ProfileExecutable(game)), token);
        if (SelectedLibraryGame?.Id != game.Id) return;
        GamePreset = Presets.FirstOrDefault(p => p.Value == value);
        PresetStatus = value is null ? Message("Workspace.NoNVIDIAProfileWasFoundForThis") :
            Message("Workspace.DriverProfile", GamePreset is { } preset ? Message(preset.LabelKey) : value);
    }

    private async Task SavePresetAsync(CancellationToken token)
    {
        var game = SelectedLibraryGame!;
        var preset = GamePreset!;
        if (!Confirm(GetText("Workspace.ChangeNVIDIAApplicationProfile"), $"{game.Title}\n{game.Executable}\nDLSS SR: {preset.Label}")) return;
        await Task.Run(() => NvidiaProfiles.Write(ProfileExecutable(game), preset.Value), token);
        if (SelectedLibraryGame?.Id == game.Id) await ReadPresetAsync(CancellationToken.None);
        Toast = Status = Message("Workspace.SavedDLSSPresetInTheNVIDIAProfile");
    }

    private static string ProfileExecutable(LibraryRow game)
    {
        var executable = SafePaths.Child(SafePaths.Root(game.Path), Path.GetRelativePath(game.Path, game.Executable));
        if (!File.Exists(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException(GetText("Workspace.CheckTheGameExecutableForTheDriver"));
        return executable;
    }
    public void OpenScanDetails()
    {
        if (SelectedLibraryGame?.Inventory is not { } game) return;
        if (!Findings.Contains(game)) Findings = [game, .. Findings.Where(g => !SamePath(g.Root.Path, game.Root.Path))];
        SelectedFinding = game;
        SelectedTab = 3;
    }

    private ICommand CreateCopyCommand(Func<LibraryRow, string> getPath, Action<bool> setCopied)
    {
        var copyVersion = 0;
        return new Command(async () =>
        {
            var copied = false;
            await RunAsync(_ =>
            {
                try
                {
                    Clipboard.SetText(getPath(SelectedLibraryGame!));
                    Status = Message("Workspace.PathCopied");
                    copied = true;
                }
                catch (ExternalException e)
                {
                    Status = Message("Workspace.CouldNotCopyPath", e.Message);
                }
                return Task.CompletedTask;
            });
            if (!copied) return;

            var completedCopy = ++copyVersion;
            setCopied(true);
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            if (completedCopy == copyVersion) setCopied(false);
        }, () => HasGame() && getPath(SelectedLibraryGame!).Length > 0);
    }

    private async Task LoadWorkspaceAsync(CancellationToken token)
    {
        try
        {
            await Task.Run(() => DataFolders.Initialize(storage), token);
            await LoadCachedUnlockerAsync(token);
            settings = await Task.Run(library.ReadSettings, token);
            languageSettingsReady = true;
            Notify(nameof(LanguagePreference));
            Notify(nameof(CanChangeLanguage));
            driverTooltipSettingsReady = true;
            Notify(nameof(DriverPreviewStyle));
            Notify(nameof(CanChooseDriverTooltipStyle));
            lastScan = settings.LastScan;
            Notify(nameof(LastScanLabel));
            DefaultProxy = string.IsNullOrWhiteSpace(settings.DefaultProxy) ? "" : settings.DefaultProxy;
            Notify(nameof(DefaultProxy));
            Notify(nameof(ScanPaths));
            Notify(nameof(ScanLaunchersOnStartup));
            LoadAutoUpdateSettings();
            Notify(nameof(ExcludeAntiCheatGames));
            Notify(nameof(SilentDriverInstall));
            Notify(nameof(DeleteDriverInstallerAfterInstall));
            GoogleTranslateApiKeyText = settings.GoogleTranslateApiKey ?? "";
            CustomHotfixFeedUrlText = string.IsNullOrWhiteSpace(settings.CustomHotfixFeedUrl)
                ? HotfixFeedScanner.DefaultHotfixFeedUrl
                : settings.CustomHotfixFeedUrl;
            GoogleSearchApiKeyText = settings.GoogleSearchApiKey ?? "";
            GoogleSearchCxText = settings.GoogleSearchCx ?? "";
            Notify(nameof(TranslateDriverSummaries));
            Notify(nameof(ClientLanguageCode));
            Notify(nameof(CanTranslateDriverSummaries));
            Notify(nameof(TranslateDriverSummariesLabel));
            NotifyDriverUi();
            Notify(nameof(BatchTargets));
            Notify(nameof(HasBatchTargets));
            Notify(nameof(CatalogRows));
            foreach (var game in await Task.Run(library.ReadInventories, token))
                inventories[game.Root.Path] = game;
            RefreshBackups();
            foreach (var journal in await Task.Run(() => CreateTransactions().InstalledComponents("OptiScaler"), token))
                managedOpti.Add(journal.Root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Notes = [.. Notes, e.Message];
        }
        await RefreshDetectedGpuAsync(token);
        await ReconcilePendingDowngradeOnCheckAsync(token);
        try
        {
            await Task.Run(() => DriverService.CleanupOnStartup(
                detectedGeForce?.Version,
                settings.DeleteDriverInstallerAfterInstall,
                protectedInstallerFileName: settings.PendingDowngrade?.InstallerFileName), token);
            await RefreshDriverCacheSizeAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Notes = [.. Notes, Message("Workspace.DriverCache", e.Message)]; }
        try { OverlayEnabled = await Task.Run(ReadOverlay, token); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Notes = [.. Notes, Message("Workspace.DLSSIndicator", e.Message)]; }
    }

    private async Task RefreshDetectedGpuAsync(CancellationToken token)
    {
        try
        {
            var gpu = await Task.Run(ReadGpu, token);
            var version = gpu.Name?.Contains("GeForce", StringComparison.OrdinalIgnoreCase) == true
                ? NvidiaDriverLookup.GeForceVersion(gpu.Driver) : null;
            detectedGeForce = version is null ? null : (gpu.Name!, version);
            Gpu = gpu.Name is null ? Message("Workspace.NoGPUDetected") : $"{gpu.Name}\nWindows {gpu.Driver}" +
                (version is null ? "" : $" · GeForce {NvidiaDriverLookup.Display(version)}");
            drsStatus = await Task.Run(NvidiaProfiles.AccessStatus, token);
            Notify(nameof(DrsStatus));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { Gpu = Message("Workspace.GPUDataUnavailable"); Notes = [.. Notes, e.Message]; }
    }

    private static (string? Name, string Driver) ReadGpu()
    {
        using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        var adapters = new List<(string Name, string Driver)>();
        foreach (var name in (display?.GetSubKeyNames() ?? []).Where(name => name.Length == 4 && name.All(char.IsAsciiDigit)))
        {
            try
            {
                using var adapter = display?.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is string desc)
                    adapters.Add((desc, adapter.GetValue("DriverVersion") as string ?? "—"));
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        }
        var gpu = adapters.FirstOrDefault(a => a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (gpu.Name is null) gpu = adapters.FirstOrDefault();
        return gpu;
    }

    private static bool ReadOverlay()
    {
        using var ngx = Registry.LocalMachine.OpenSubKey(NgxKey);
        return ngx?.GetValue("ShowDlssIndicator") is int value && value != 0;
    }

    private const string NgxKey = @"SOFTWARE\NVIDIA Corporation\Global\NGXCore";

    private async Task ToggleOverlayAsync(CancellationToken token)
    {
        var enabled = !OverlayEnabled;
        await Task.Run(() =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(NgxKey, writable: true)
                ?? throw new IOException(GetText("Workspace.TheNVIDIANGXCoreRegistryKeyIsMissing"));
            key.SetValue("ShowDlssIndicator", enabled ? 1024 : 0, RegistryValueKind.DWord);
        }, token);
        OverlayEnabled = enabled;
        Status = enabled ? Message("Workspace.GlobalDLSSIndicatorEnabled") : Message("Workspace.GlobalDLSSIndicatorDisabled");
    }

    private static Task OpenNvidiaAsync()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVIDIA app");
        var path = new[] { Path.Combine(root, "CEF", "NVIDIA app.exe"), Path.Combine(root, "NVIDIA app.exe") }
            .FirstOrDefault(File.Exists);
        if (path is null) throw new IOException(GetText("Workspace.TheNVIDIAAppWasNotFoundAt"));
        return OpenShell(path);
    }

    private async Task SaveSettingsAsync(ManagerSettings value, CancellationToken token)
    {
        await WriteSettingsAsync(_ => value, token);
        Notify(nameof(AutoUpdateDlls));
        Notify(nameof(AutoUpdateUnlocker));
        Notify(nameof(AutoUpdateOptiScaler));
        Notify(nameof(ScanPaths));
        Notify(nameof(ScanLaunchersOnStartup));
        Notify(nameof(ExcludeAntiCheatGames));
        Notify(nameof(SilentDriverInstall));
        Notify(nameof(DeleteDriverInstallerAfterInstall));
        GoogleTranslateApiKeyText = value.GoogleTranslateApiKey ?? "";
        CustomHotfixFeedUrlText = string.IsNullOrWhiteSpace(value.CustomHotfixFeedUrl)
            ? HotfixFeedScanner.DefaultHotfixFeedUrl
            : value.CustomHotfixFeedUrl;
        GoogleSearchApiKeyText = value.GoogleSearchApiKey ?? "";
        GoogleSearchCxText = value.GoogleSearchCx ?? "";
        Notify(nameof(TranslateDriverSummaries));
        Notify(nameof(ClientLanguageCode));
        Notify(nameof(CanTranslateDriverSummaries));
        Notify(nameof(TranslateDriverSummariesLabel));
        NotifyDriverUi();
        Notify(nameof(BatchTargets));
        Notify(nameof(HasBatchTargets));
        CommandManager.InvalidateRequerySuggested();
        Notify(nameof(CatalogRows));
        FilterLibrary();
        Status = Message("Settings.Saved");
    }

    private async Task EditScanPathsAsync(CancellationToken token)
    {
        var dialog = new OpenFolderDialog { Title = GetText("Workspace.ChooseCustomScanFolders"), Multiselect = true };
        if (dialog.ShowDialog(Application.Current.MainWindow) == true)
            await SaveSettingsAsync(settings with { ScanFolders = dialog.FolderNames }, token);
    }

    private async Task AddDiscoveryAsync(CancellationToken token)
    {
        var game = SelectedFinding!;
        var row = new LibraryRow(game.Root.Title, game.Root.Path, settings.DefaultProxy, EffectiveExecutable ?? "",
            Id: Guid.NewGuid(), Launcher: game.Root.Launcher, Inventory: game);
        await SaveGamesAsync([.. allGames, row], token);
        ResetLibraryFilter();
        SelectedLibraryGame = Games.FirstOrDefault(g => g.Id == row.Id);
        SelectedTab = 0;
        Toast = Message("Workspace.AddedToTheLibrary", row.Title);
    }

    private DllDownloadService Downloader() => new(Http, Path.Combine(storage, "Katalog", "DLLs"));
    private LocalDllCatalog LocalCatalog() => new(Path.Combine(storage, "Katalog", "Importe"));

    private async Task LoadCatalogAsync(DllCatalogEntry[] online, CancellationToken token)
    {
        onlineCatalog = online;
        CatalogEntries = await Task.Run(() =>
        {
            var downloader = Downloader();
            return online.Select(entry =>
            {
                try
                {
                    if (downloader.CachedPath(entry) is { } path)
                        return entry with { Sha256 = DllDownloadService.VerifyCatalogFile(path, entry, token).Sha256 };
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return entry;
            }).Concat(LocalCatalog().Read(token))
                .OrderBy(e => e.Kind).ThenByDescending(e => e.Version).ToArray();
        }, token);
        Notify(nameof(OptiPackages));
        SelectedOptiPackage ??= OptiPackages.FirstOrDefault();
        NotifyCatalogSummary();
    }

    private async Task ImportDllAsync(CancellationToken token)
    {
        var picker = new OpenFileDialog { Title = GetText("Workspace.ImportLocalUpscalingDLL"), Filter = GetText("Workspace.DLLFilesDll"), Multiselect = true };
        if (picker.ShowDialog(Application.Current.MainWindow) != true) return;
        foreach (var file in picker.FileNames)
        {
            var imported = await Task.Run(() => LocalCatalog().Import(file, token), token);
            CatalogEntries = [.. CatalogEntries.Where(e => e.LocalPath != imported.LocalPath), imported];
        }
        await LoadCatalogAsync(onlineCatalog, token);
        Notify(nameof(OptiPackages));
        Toast = Status = Message("Workspace.LocalDLLsImported");
    }

    private Task<DllCatalogEntry> GetOptiReleaseAsync(CancellationToken token) =>
        Task.Run(() => new OptiScalerDownload(Http, Path.Combine(storage, "Katalog", "OptiScaler"), LocalCatalog())
            .GetAsync(token), token);

    private async Task DownloadOptiAsync(CancellationToken token)
    {
        Status = Message("Workspace.DownloadingOptiScalerReleaseAndVerifyingSHA");
        Indeterminate = true;
        var entry = await GetOptiReleaseAsync(token);
        CatalogEntries = CatalogEntries.Where(e => e.LocalPath != entry.LocalPath).Append(entry)
            .OrderBy(e => e.Kind).ThenByDescending(e => e.Version).ToArray();
        Notify(nameof(OptiPackages));
        SelectedOptiPackage = entry;
        Toast = Status = Message("Workspace.ProxyDLLInTheLocalCatalog", Message(entry.SourceLabelKey));
    }

    private async Task RememberInventoriesAsync(IEnumerable<ScanGame> games)
    {
        foreach (var game in games) inventories[game.Root.Path] = game;
        MatchLibraryFeatures();
        RefreshBackups();
        var snapshot = inventories.Values.ToArray();
        try { await Task.Run(() => library.SaveInventories(snapshot)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Notes = [.. Notes, Message("Workspace.CouldNotSaveTheDLLInventory", e.Message)]; }
    }

    private void RefreshInspector()
    {
        RefreshExecutableChoices();
        GamePreset = null;
        PresetStatus = Message("Workspace.ReadNVIDIAApplicationProfile");
        RefreshModCockpit();
        var game = SelectedLibraryGame?.Inventory;
        InspectorDlls = game is null ? [] : game.Dlls.Where(IsSupportedUpscaler)
            .Select(d => new InspectorDll(this, game, d, CanReplace(d) ?
                CatalogEntries.Where(c => c.Kind == d.Kind).ToArray() : []))
            .OrderBy(row => row.IsPluginCopy)
            .ThenByDescending(row => row.IsInPrimaryLocation)
            .ThenBy(row => row.File.Kind switch
                { "SR" => 0, "FG" => 1, "RR" => 2, "XeSS" => 3, "FSR" => 4, _ => 5 })
            .ThenBy(row => row.File.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Notify(nameof(InspectorDlls));
        Notify(nameof(HasInspectorDlls));
        Notify(nameof(HasInventory));
        Notify(nameof(InventoryStatus));
        Notify(nameof(InventoryCount));
        Notify(nameof(GameOverlay));
        Notify(nameof(AntiCheatNotice));
        NotifyOptiCard();
        Notify(nameof(LibraryStorageSummary));
        Notify(nameof(LibraryBadge));
    }

    private static bool IsSupportedUpscaler(DllFinding dll) =>
        !dll.ModComponent && dll.Kind is "SR" or "FG" or "RR" or "XeSS" or "FSR" &&
        DllDetectionService.UpscalerKind(Path.GetFileName(dll.Path)) == dll.Kind;

    private static bool CanReplace(DllFinding dll) => IsSupportedUpscaler(dll) &&
        dll.Metadata is { IsDll: true, Machine: Machine.Amd64 };

    private string? ExecutableFor(ScanGame game) =>
        new[] { SelectedLibraryGame, allGames.FirstOrDefault(g => SamePath(g.Path, game.Root.Path)) }
            .FirstOrDefault(g => g is not null && SamePath(g.Path, game.Root.Path) &&
                Path.IsPathFullyQualified(g.Executable) && SafePaths.Contains(game.Root.Path, g.Executable))?.Executable
        ?? (ReferenceEquals(SelectedFinding, game) ? EffectiveExecutable : game.Executable)
        ?? (game.Alternatives.Length == 1 ? game.Alternatives[0] : null);

    private IEnumerable<(DllFinding Dll, DllCatalogEntry Target)> UpdateCandidates(ScanGame? game, bool pinnedOnly = false)
    {
        if (game is null || game.AntiCheat is { Length: > 0 }) yield break;
        foreach (var dll in game.Dlls)
        {
            if (!CanReplace(dll) || dll.Metadata?.NumericFileVersion is not { } installed) continue;
            var pinned = settings.BatchTargets?.FirstOrDefault(t => t.Kind == dll.Kind);
            if (pinnedOnly && pinned is null) continue;
            var target = CatalogEntries.FirstOrDefault(c => c.Kind == dll.Kind && c.Version > installed &&
                (pinned is null ? c.LocalPath is null :
                    c.Version == pinned.Version && CatalogIdentity(c) == pinned.CatalogHash));
            if (target is not null) yield return (dll, target);
        }
    }

    private async Task ApplyLibraryBatchTargetsAsync(CancellationToken token)
    {
        var game = SelectedLibraryGame!.Inventory!;
        var updates = UpdateCandidates(game, pinnedOnly: true).ToArray();
        var rows = updates.Select(u => new BatchUpdateWindow.UpdateRow(game.Root.Title,
            $"{Path.GetRelativePath(game.Root.Path, u.Dll.Path)}  ·  {u.Dll.Version} → {u.Target.Version}")).ToArray();
        var dialog = new BatchUpdateWindow(rows, 1, GetText("Workspace.ApplyBatchTarget")) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await ApplyUpdatesAsync(game, updates, token);
        Toast = Status = Message("Workspace.AppliedSavedBatchTargetsTo", game.Root.Title);
    }

    internal ICommand DllCommand(ScanGame game, DllFinding dll, Func<DllCatalogEntry?> selection) =>
        new Command(() => RunAsync(async token =>
        {
            var target = selection()!;
            if (!ConfirmAntiCheat(game)) return;
            if (!Confirm(GetText("Workspace.ApplyDLL"),
                    GetText("Workspace.ApplyDllDetails", dll.Path, dll.Version, target.Version, GetText(target.SourceLabelKey)))) return;
            await ApplyUpdatesAsync(game, [(dll, target)], token, game.AntiCheat);
            Toast = Status = Message("Workspace.DLLVerifiedAndApplied");
        }), () => !IsBusy && CanReplace(dll) &&
            ExecutableFor(game) is not null && selection() is not null);

    private static bool Confirm(string title, string message) =>
        ConfirmationDialog.Show(Application.Current.MainWindow, message, title, MessageBoxButton.OKCancel,
            MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    private static bool IsPackageFailure(Exception error) => error is IOException or UnauthorizedAccessException or
        HttpRequestException or InvalidDataException;

    private async Task BatchUpdateAsync(CancellationToken token)
    {
        await RefreshAndCacheCatalogAsync(token);
        Indeterminate = false;
        Percent = 0;
        var plan = allGames.Where(g => !g.Ignored && g.Inventory is not null && ExecutableFor(g.Inventory) is not null)
            .DistinctBy(g => g.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Game: g.Inventory!, Updates: UpdateCandidates(g.Inventory).ToArray()))
            .Where(g => g.Updates.Length > 0).ToArray();
        if (plan.Length == 0)
        {
            Toast = Status = allGames.Any(g => !g.Ignored && UpdateCandidates(g.Inventory).Any())
                ? Message("Workspace.UpdatesAreAvailableAssignAGameExecutable")
                : !allGames.Any(g => !g.Ignored && g.Inventory is not null)
                    ? Message("Workspace.RunAQuickScanFirstToDetect")
                    : Message("Workspace.NoNewerDLLsAreAvailableForThe");
            return;
        }
        var rows = plan.SelectMany(g => g.Updates.Select(u => new BatchUpdateWindow.UpdateRow(
            g.Game.Root.Title, $"{u.Dll.Kind}  ·  {u.Dll.Version} → {u.Target.Version}"))).ToArray();
        var dialog = new BatchUpdateWindow(rows, plan.Length) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var successful = 0;
        var failures = new List<string>();
        var processed = 0;
        foreach (var item in plan)
        {
            token.ThrowIfCancellationRequested();
            try { await ApplyUpdatesAsync(item.Game, item.Updates, token); successful++; }
            catch (Exception e) when (IsPackageFailure(e) || e is OperationCanceledException && !token.IsCancellationRequested)
            { failures.Add($"{item.Game.Root.Title}: {e.Message}"); }
            Percent = 100d * ++processed / plan.Length;
        }
        Notes = [.. Notes, .. failures.Select(message => (LocalizedText)message)];
        Toast = Status = Message("Workspace.UpdatedGames", successful, plan.Length) +
            (failures.Count > 0 ? Message("Workspace.FailedSeeNotesAndDiagnosticsForDetails", failures.Count) : "");
    }

    private Installation InstallationFor(ScanGame game)
    {
        var executable = ExecutableFor(game) ?? throw new IOException(GetText("Workspace.SelectTheGameExecutableFor", game.Root.Title));
        return new(game.Root.Path, [Path.GetRelativePath(game.Root.Path, executable)]);
    }

    private async Task ApplyUpdatesAsync(ScanGame game, (DllFinding Dll, DllCatalogEntry Target)[] updates, CancellationToken token, IReadOnlyCollection<string>? acknowledged = null)
    {
        var installation = InstallationFor(game);
        Status = Message("Workspace.VerifyingDownloadsAndOriginalFiles", game.Root.Title);
        var id = await Task.Run(async () =>
        {
            GameScanner.RequireModifiable(game.Root.Path, token, acknowledged);
            var downloader = Downloader();
            var files = new List<DllReplacementService.Update>();
            foreach (var (dll, target) in updates)
            {
                var path = await downloader.GetAsync(target, token);
                var metadata = DllDownloadService.VerifyCatalogFile(path, target, token);
                files.Add(new(Path.GetRelativePath(game.Root.Path, dll.Path), dll.Metadata!.Sha256, path, metadata.Sha256));
            }
            return new DllReplacementService(CreateTransactions()).ReplaceAll(installation, files, token);
        }, token);
        if (id is not null)
        {
            Notes = [.. Notes, Message("Workspace.TransactionOriginalFilesVerifiedAgainstSHA", id, game.Root.Path)];
            await RefreshGameInventoryAsync(game, installation.Executables[0]);
        }
    }

    private Task ExecuteRestore(BackupRow[] backups) => RunAsync(async token =>
    {
        var message = backups.Length == 1
            ? GetText("Workspace.RestoreThisDLLFromItsOriginalBackup", backups[0].Target)
            : GetText("Workspace.RestoreOriginalDLLsFromTheirBakFiles", backups.Length);
        if (!Confirm(GetText("Workspace.RestoreOriginals"), message)) return;
        var restored = 0;
        var processed = 0;
        foreach (var group in backups.GroupBy(b => b.Game.Root.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var game = group.First().Game;
            try
            {
                var installation = InstallationFor(game);
                Status = Message("Workspace.VerifyingOriginalFiles", game.Root.Title);
                var id = await Task.Run(() =>
                {
                    var files = group.Select(b => (Path.GetRelativePath(installation.Root, b.Dll.Path),
                        b.Dll.Metadata?.Sha256 ?? throw new IOException(GetText("Workspace.CouldNotReadTheActiveDLL")),
                        DllDetectionService.Inspect(b.Dll.Path + ".bak", token).Sha256)).ToArray();
                    return new DllReplacementService(CreateTransactions()).RestoreAll(installation, files, token);
                }, token);
                if (id is not null)
                {
                    Notes = [.. Notes, Message("Workspace.Recovery", id, game.Root.Path)];
                    await RefreshGameInventoryAsync(game, installation.Executables[0]);
                }
                restored += group.Count();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { Notes = [.. Notes, $"{game.Root.Title}: {e.Message}"]; }
            processed += group.Count();
            Percent = 100d * processed / backups.Length;
        }
        Toast = Status = Message("Workspace.OriginalDLLsRestoredOrAlreadyPresent", restored, backups.Length);
        await RefreshRecoveryAsync(CancellationToken.None, reportStatus: false);
    });

    private async Task RefreshGameInventoryAsync(ScanGame game, string executable)
    {
        var scanned = await Task.Run(() => new GameScanner(Path.Combine(storage, "cache"))
            .Scan(new([game.Root with { Executable = executable, Launcher = game.Root.Launcher == "Manuell" ? "Lokal" : game.Root.Launcher }],
                [], [])).Games.FirstOrDefault());
        if (scanned is null) return;
        var selected = SelectedFinding;
        Findings = Findings.Select(g => SamePath(g.Root.Path, scanned.Root.Path) ? scanned : g).ToArray();
        SelectedFinding = selected is not null && SamePath(selected.Root.Path, scanned.Root.Path) ? scanned : selected;
        await RememberInventoriesAsync([scanned]);
    }

    private async Task ChangeOptiAsync(bool remove, CancellationToken token)
    {
        var row = SelectedLibraryGame!;
        var game = row.Inventory!;
        var installation = InstallationFor(game);
        if (!remove && !ConfirmAntiCheat(game)) return;
        if (!remove && SelectedOptiPackage is null) await DownloadOptiAsync(token);
        var package = SelectedOptiPackage;
        var proxy = remove ? null : ResolveProxy(installation, installation.Executables[0],
            optiInstalledProxy ?? OptiProxy, OptiProxies, optiInstalledProxy);
        if (proxy is not null) OptiProxy = proxy;
        if (!remove && proxy is null) return;
        if (remove && !Confirm(GetText("Workspace.RemoveOptiScaler"), row.Title)) return;
        var id = await Task.Run(() =>
        {
            var service = new OptiScalerService(CreateTransactions(), IniProfiles());
            if (!remove)
            {
                GameScanner.RequireModifiable(game.Root.Path, token, game.AntiCheat);
                return service.Install(installation, installation.Executables[0], package!, proxy!, token);
            }
            var existing = service.Installed(installation.Root)?.Id ?? throw new IOException(GetText("Workspace.NoOptiScalerPackageIsAssociatedWithThis"));
            service.Remove(installation, existing, token);
            return existing;
        }, token);
        if (remove) managedOpti.Remove(row.Path);
        else managedOpti.Add(row.Path);
        await RefreshGameInventoryAsync(game, installation.Executables[0]);
        RefreshModCockpit(force: true);
        RefreshInspector();
        Notes = [.. Notes, Message("Workspace.OptiScalerTransaction", id)];
        Toast = Status = remove ? Message("Workspace.OptiScalerProxyRemoved") : Message("Workspace.OptiScalerInstalledOrUpdated");
    }
}
