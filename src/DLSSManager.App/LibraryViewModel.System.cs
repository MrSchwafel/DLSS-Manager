using static DLSSManager.Core.LocalizationService;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DLSSManager.Core;

namespace DLSSManager.App;

public enum DriverUiState
{
    NoUpdate,
    Available,
    Downloading,
    Downloaded,
    Installing
}

public enum DriverDowngradeUiState
{
    Idle,
    Downloading,
    Installing
}

public sealed record RollbackMenuEntry(
    string Label,
    string? TypeBadge = null,
    bool IsHotfix = false,
    bool IsCached = false,
    string? ReleaseDate = null,
    ICommand? Command = null,
    object? CommandParameter = null,
    bool IsSeparator = false,
    string Glyph = "\uE7A7")
{
    public bool ShowHotfixBadge => TypeBadge is not null && IsHotfix;
    public bool ShowWhqlBadge => TypeBadge is not null && !IsHotfix;
    public bool HasReleaseDate => !string.IsNullOrWhiteSpace(ReleaseDate);
}

public sealed partial class LibraryViewModel
{
    private (string Name, Version Version)? detectedGeForce;
    private LocalizedText driverStatus = Message("Drivers.Checking");
    private string driverColor = "#AEB8C9";
    private LocalizedText appUpdateLabel = "";
    private bool refreshingSystem;
    private bool driverCurrent;
    private DriverUpdateInfo? availableDriver;
    private PreviousDriverOptions previousDrivers = new();
    private DriverUpdateInfo[] allHistoryDrivers = [];
    private HotfixSourceStatus[] lastHotfixSourceStatuses = [];
    private DateTimeOffset? lastHotfixScanUtc;
    private bool lastHotfixFromCache;
    private bool lastHotfixChecked;
    private DriverUiState driverState = DriverUiState.NoUpdate;
    private DriverDowngradeUiState downgradeState = DriverDowngradeUiState.Idle;
    private DriverUpdateInfo? activeDowngradeDriver;
    private int? driverDownloadPercent;
    private double driverDownloadMb;
    private int? downgradeDownloadPercent;
    private double downgradeDownloadMb;
    private long driverCacheBytes;
    private CancellationTokenSource? driverDownloadCts;
    private long downloadStartedTick;
    private bool suppressDownloadToast;
    private DriverSummaryTranslator? driverTranslator;
    private string googleTranslateApiKeyText = "";
    private string customHotfixFeedUrlText = "";
    private string googleSearchApiKeyText = "";
    private string googleSearchCxText = "";

    private NvidiaDriverService DriverService => new(Http, DataFolders.DriverDownloads);
    private HotfixFeedOptions CurrentHotfixFeedOptions => new(
        settings.CustomHotfixFeedUrl,
        settings.GoogleSearchApiKey,
        settings.GoogleSearchCx);
    private DriverSummaryTranslator DriverTranslator => driverTranslator ??= new DriverSummaryTranslator(
        Http,
        DataFolders.DriverTranslationCachePath,
        () => settings.GoogleTranslateApiKey);

    public LocalizedText DriverStatus { get => driverStatus; private set => Set(ref driverStatus, value); }
    public string DriverColor { get => driverColor; private set => Set(ref driverColor, value); }
    public LocalizedText AppUpdateLabel { get => appUpdateLabel; private set => Set(ref appUpdateLabel, value); }

    public DriverUiState DriverState
    {
        get => driverState;
        private set
        {
            if (driverState == value) return;
            driverState = value;
            NotifyDriverUi();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public DriverDowngradeUiState DowngradeState
    {
        get => downgradeState;
        private set
        {
            if (downgradeState == value) return;
            downgradeState = value;
            NotifyDriverUi();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasDriverUpdate => availableDriver is not null;
    public bool IsDriverHotfix => availableDriver?.IsHotfix == true;
    public bool IsDriverDownloaded => DriverState == DriverUiState.Downloaded;
    public bool IsDriverDownloading => DriverState == DriverUiState.Downloading;
    public bool IsDriverInstalling => DriverState == DriverUiState.Installing;
    public bool IsDowngradeDownloading => DowngradeState == DriverDowngradeUiState.Downloading;
    public bool IsDowngradeInstalling => DowngradeState == DriverDowngradeUiState.Installing;
    public bool IsAnyDriverBusy => IsDriverDownloading || IsDriverInstalling || IsDowngradeDownloading || IsDowngradeInstalling;
    public bool ShowDriverFolderButton => DriverState is DriverUiState.Downloaded or DriverUiState.Installing;
    public bool ShowDriverReleaseButton => !ShowDriverFolderButton && !string.IsNullOrWhiteSpace(availableDriver?.ReleasePageUrl);

    public DriverUpdateInfo? PreferredPreviousDriver => previousDrivers.PreferredDriver;
    public DriverUpdateInfo? PreviousHotfixDriver => previousDrivers.PreviousHotfixDriver;
    public DriverUpdateInfo? PreviousWhqlDriver => previousDrivers.PreviousWhqlDriver;
    public bool HasPreviousDriver => PreferredPreviousDriver is not null;
    public bool HasPreviousHotfixDriver => PreviousHotfixDriver is not null;
    public bool HasPreviousWhqlDriver => PreviousWhqlDriver is not null;
    public bool IsPreviousHotfixCached => PreviousHotfixDriver?.IsCached == true;
    public bool IsPreviousWhqlCached => PreviousWhqlDriver?.IsCached == true;
    public bool ShowRollbackSplitBar => !HasDriverUpdate && (HasPreviousDriver || IsDowngradeDownloading || IsDowngradeInstalling);
    public bool ShowRollbackIconButton => HasDriverUpdate && (HasPreviousDriver || IsDowngradeDownloading || IsDowngradeInstalling);

    public string RollbackVisualKind => DowngradeState switch
    {
        DriverDowngradeUiState.Downloading => "Downloading",
        DriverDowngradeUiState.Installing => "Installing",
        _ => "Idle"
    };

    public string RollbackButtonGlyph => DowngradeState switch
    {
        DriverDowngradeUiState.Downloading => "\uE711",
        DriverDowngradeUiState.Installing => "\uE895",
        _ => "\uE7A7"
    };

    public string RollbackButtonLabel => DowngradeState switch
    {
        DriverDowngradeUiState.Downloading when activeDowngradeDriver is { } dl => downgradeDownloadPercent is { } p
            ? GetText("Drivers.DownloadingV", dl.Version, p)
            : GetText("Drivers.DownloadingVMB", dl.Version, downgradeDownloadMb),
        DriverDowngradeUiState.Installing when activeDowngradeDriver is { } inst =>
            GetText("Drivers.DowngradingV", inst.Version),
        _ when PreferredPreviousDriver is { } prev => GetText("Drivers.RollBackToV", prev.Version),
        _ => ""
    };

    public string RollbackButtonToolTip => DowngradeState switch
    {
        DriverDowngradeUiState.Downloading => GetText("Drivers.DownloadInProgressClickToCancel"),
        DriverDowngradeUiState.Installing => GetText("Drivers.DowngradingDriverToV", activeDowngradeDriver?.Version),
        _ when PreferredPreviousDriver is { } prev => BuildDriverToolTip(
            GetText("Drivers.RollBackDirectlyToTheLatestPrevious", (prev.IsHotfix ? "Hotfix" : "WHQL"), prev.Version, (prev.IsCached ? GetText("Drivers.Cached3") : "")),
            prev.ReleaseSummary),
        _ => ""
    };

    public string RollbackIconToolTip => IsDowngradeDownloading
        ? GetText("Drivers.DownloadingPreviousDriverClickToCancel")
        : GetText("Workspace.RollBackToThePreviousAvailableDriver");

    public string PreviousHotfixMenuLabel => PreviousHotfixDriver is { } hf
        ? GetText("Drivers.PreviousHotfixV", hf.Version)
        : "";

    public string PreviousWhqlMenuLabel => PreviousWhqlDriver is { } whql
        ? GetText("Drivers.PreviousWHQLDriverV", whql.Version)
        : "";

    public string PreviousHotfixToolTip => PreviousHotfixDriver is { } hf
        ? BuildDriverToolTip(
            $"NVIDIA Hotfix v{hf.Version}{(hf.IsCached ? GetText("Drivers.AlreadyCached") : "")}",
            hf.ReleaseSummary)
        : "";

    public string PreviousWhqlToolTip => PreviousWhqlDriver is { } whql
        ? BuildDriverToolTip(
            GetText("Drivers.NVIDIAWHQLDriverV", whql.Version, (whql.IsCached ? GetText("Drivers.AlreadyCached") : "")),
            whql.ReleaseSummary)
        : "";

    public string DriverVisualKind => DriverState switch
    {
        DriverUiState.Installing => "Installing",
        DriverUiState.Downloading => "Downloading",
        DriverUiState.Downloaded => "Ready",
        _ => IsDriverHotfix ? "Hotfix" : "WHQL"
    };

    public string DriverButtonGlyph => DriverState switch
    {
        DriverUiState.Downloading => "\uE711",
        DriverUiState.Installing => "\uE895",
        DriverUiState.Downloaded => "\uE768",
        _ => "\uE896"
    };

    public string DriverButtonLabel => availableDriver is not { } update ? "" : DriverState switch
    {
        DriverUiState.Downloading => driverDownloadPercent is { } p
            ? GetText("Drivers.Downloading", p)
            : GetText("Drivers.DownloadingMB", driverDownloadMb),
        DriverUiState.Installing => GetText("Drivers.InstallingV", update.Version),
        DriverUiState.Downloaded => SilentDriverInstall
            ? GetText("Drivers.ExpressSetupV", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version)
            : GetText("Drivers.InstallV", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version),
        DriverUiState.Available => GetText("Drivers.DownloadV", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version),
        _ => ""
    };

    public string DriverButtonToolTip => availableDriver is not { } update ? DriverStatus.Text : DriverState switch
    {
        DriverUiState.Downloading => GetText("Drivers.DownloadInProgressClickToCancel"),
        DriverUiState.Installing => GetText("Drivers.InstallingNVIDIADriverVInTheBackground", update.Version),
        DriverUiState.Downloaded when SilentDriverInstall =>
            GetText("Drivers.StartSilentNVIDIAExpressInstallationInThe"),
        DriverUiState.Downloaded =>
            BuildDriverToolTip(GetText("Drivers.InstallTheDownloadedNVIDIADriverVInteractively", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version), update.ReleaseSummary),
        DriverUiState.Available =>
            BuildDriverToolTip(
                GetText("Drivers.DownloadNVIDIADriverVDirectlyInThe", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version),
                update.ReleaseSummary),
        _ => DriverStatus.Text
    };

    public string DriverReleasePageToolTip => IsDriverHotfix
        ? GetText("Drivers.OpenHotfixReleaseNotesAndDownloadsIn")
        : GetText("Drivers.OpenWHQLDriverDetailsAndDownloadsIn");

    public bool SilentDriverInstall => settings.SilentDriverInstall;
    public bool DeleteDriverInstallerAfterInstall => settings.DeleteDriverInstallerAfterInstall;
    public bool TranslateDriverSummaries => CanTranslateDriverSummaries && settings.TranslateDriverSummaries;
    private readonly SemaphoreSlim settingsWriteGate = new(1, 1);
    private DriverTooltipStyle? pendingDriverTooltipStyle;
    private int driverTooltipStyleChange;
    private bool driverTooltipSettingsReady;

    internal Task DriverTooltipStyleSave { get; private set; } = Task.CompletedTask;
    public bool CanChooseDriverTooltipStyle => driverTooltipSettingsReady;
    public sealed record DriverTooltipOption(DriverTooltipStyle Code, string LabelKey);

    public DriverTooltipOption[] DriverTooltipChoices { get; } =
    [
        new(DriverTooltipStyle.Compact, "Drivers.CompactSummary"),
        new(DriverTooltipStyle.Detailed, "Drivers.DetailedReleaseNotesWithBadges")
    ];

    public DriverTooltipStyle DriverPreviewStyle
    {
        get => pendingDriverTooltipStyle ??
            (string.Equals(settings.DriverTooltipStyle, nameof(DriverTooltipStyle.Detailed), StringComparison.OrdinalIgnoreCase)
                ? DriverTooltipStyle.Detailed : DriverTooltipStyle.Compact);
        set
        {
            if (!CanChooseDriverTooltipStyle || DriverPreviewStyle == value)
                return;

            pendingDriverTooltipStyle = value;
            Notify(nameof(DriverPreviewStyle));
            DriverTooltipStyleSave = SaveDriverTooltipStyleAsync(++driverTooltipStyleChange);
        }
    }

    public string ClientLanguageCode => Language;
    public bool CanTranslateDriverSummaries => ClientLanguageCode == German;
    public string TranslateDriverSummariesLabel =>
        GetText("Settings.TranslateDriverNotes");
    public string GoogleTranslateApiKeyText
    {
        get => googleTranslateApiKeyText;
        set => Set(ref googleTranslateApiKeyText, value);
    }
    public string CustomHotfixFeedUrlText
    {
        get => customHotfixFeedUrlText;
        set => Set(ref customHotfixFeedUrlText, value);
    }
    public string GoogleSearchApiKeyText
    {
        get => googleSearchApiKeyText;
        set => Set(ref googleSearchApiKeyText, value);
    }
    public string GoogleSearchCxText
    {
        get => googleSearchCxText;
        set => Set(ref googleSearchCxText, value);
    }
    public string ClearDriverCacheLabel => GetText("Drivers.ClearDriverCacheMB", driverCacheBytes / 1048576d);

    public ICommand RefreshSystem { get; private set; } = null!;
    public ICommand OpenDriverPage { get; private set; } = null!;
    public ICommand PrimaryDriverAction { get; private set; } = null!;
    public ICommand OpenDriverReleasePage { get; private set; } = null!;
    public ICommand OpenDriverCacheFolder { get; private set; } = null!;
    public ICommand InstallDriverInteractive { get; private set; } = null!;
    public ICommand PrimaryRollbackAction { get; private set; } = null!;
    public ICommand RollbackToDriver { get; private set; } = null!;
    public ICommand RollbackToHotfix { get; private set; } = null!;
    public ICommand RollbackToWhql { get; private set; } = null!;
    public ICommand OpenDriverHistoryOverview { get; private set; } = null!;
    public ICommand OpenHotfixPortal { get; private set; } = null!;
    public ICommand OpenPreviousDriverArchive { get; private set; } = null!;
    public ICommand ToggleSilentDriverInstall { get; private set; } = null!;
    public ICommand ToggleDeleteDriverInstallerAfterInstall { get; private set; } = null!;
    public ICommand ToggleTranslateDriverSummaries { get; private set; } = null!;
    public ICommand SaveGoogleTranslateApiKey { get; private set; } = null!;
    public ICommand SaveHotfixSourceSettings { get; private set; } = null!;
    public ICommand ClearDriverCache { get; private set; } = null!;

    public RollbackMenuEntry[] RollbackMenuItems
    {
        get
        {
            var items = new List<RollbackMenuEntry>();
            var menuDrivers = previousDrivers.Drivers.Take(5).ToList();
            if (PreviousHotfixDriver is { } prevHotfix &&
                !menuDrivers.Any(d => d.IsHotfix && d.ParsedVersion == prevHotfix.ParsedVersion))
            {
                if (menuDrivers.Count >= 5)
                    menuDrivers[^1] = prevHotfix;
                else
                    menuDrivers.Add(prevHotfix);
            }

            foreach (var d in menuDrivers)
            {
                var typeLabel = d.IsHotfix ? "Hotfix" : "WHQL";
                items.Add(new RollbackMenuEntry(
                    Label: $"v{d.Version}",
                    TypeBadge: typeLabel,
                    IsHotfix: d.IsHotfix,
                    IsCached: d.IsCached,
                    ReleaseDate: d.ReleaseDate,
                    Command: RollbackToDriver,
                    CommandParameter: d));
            }

            if (items.Count > 0)
                items.Add(new RollbackMenuEntry("", IsSeparator: true));

            items.Add(new RollbackMenuEntry(
                Label: GetText("Drivers.DriverOverview"),
                Command: OpenDriverHistoryOverview,
                Glyph: "\uE8FD"));
            items.Add(new RollbackMenuEntry(
                Label: GetText("Drivers.OpenHotfixPage2"),
                Command: OpenHotfixPortal,
                Glyph: "\uE8A7"));
            items.Add(new RollbackMenuEntry(
                Label: GetText("Drivers.OpenDriverArchive"),
                Command: OpenPreviousDriverArchive,
                Glyph: "\uE8A7"));
            return [.. items];
        }
    }

    private void InitializeSystemCommands()
    {
        RefreshSystem = new Command(() => RefreshSystemAsync(forceHotfixRefresh: true),
            () => !refreshingSystem && !IsAnyDriverBusy);
        OpenDriverPage = new Command(
            () => RunAsync(_ => driverCurrent
                ? OpenNvidiaAsync()
                : OpenShell(availableDriver?.ReleasePageUrl ?? NvidiaDriverLookup.DriverPage)),
            () => !IsBusy);
        PrimaryDriverAction = new Command(
            ExecutePrimaryDriverActionAsync,
            () => availableDriver is not null && !IsDowngradeDownloading && !IsDowngradeInstalling &&
                  DriverState is DriverUiState.Available or DriverUiState.Downloading or DriverUiState.Downloaded);
        OpenDriverReleasePage = new Command(
            () => availableDriver?.ReleasePageUrl is { Length: > 0 } pageUrl
                ? OpenShell(pageUrl)
                : Task.CompletedTask,
            () => !string.IsNullOrWhiteSpace(availableDriver?.ReleasePageUrl));
        OpenDriverCacheFolder = new Command(async () =>
        {
            var folder = DataFolders.DriverDownloads;
            await Task.Run(() => Directory.CreateDirectory(folder));
            await OpenShell(folder);
        }, () => true);
        InstallDriverInteractive = new Command(
            () => InstallDriverAsync(forceInteractive: true),
            () => DriverState == DriverUiState.Downloaded && availableDriver is not null && !IsDowngradeDownloading && !IsDowngradeInstalling);
        PrimaryRollbackAction = new Command(
            ExecutePrimaryRollbackActionAsync,
            () => IsDowngradeDownloading || (PreferredPreviousDriver is not null && !IsAnyDriverBusy));
        RollbackToDriver = new Command(
            p => p is DriverUpdateInfo d ? StartDriverDowngradeFlowAsync(d) : Task.CompletedTask,
            p => p is DriverUpdateInfo && !IsAnyDriverBusy);
        RollbackToHotfix = new Command(
            () => PreviousHotfixDriver is { } hf ? StartDriverDowngradeFlowAsync(hf) : Task.CompletedTask,
            () => PreviousHotfixDriver is not null && !IsAnyDriverBusy);
        RollbackToWhql = new Command(
            () => PreviousWhqlDriver is { } whql ? StartDriverDowngradeFlowAsync(whql) : Task.CompletedTask,
            () => PreviousWhqlDriver is not null && !IsAnyDriverBusy);
        OpenDriverHistoryOverview = new Command(
            OpenDriverHistoryOverviewAsync,
            () => !IsAnyDriverBusy);
        OpenHotfixPortal = new Command(
            () => OpenShell(NvidiaDriverLookup.HotfixListUrl),
            () => true);
        OpenPreviousDriverArchive = new Command(
            () => OpenShell(PreferredPreviousDriver?.ReleasePageUrl
                ?? PreviousWhqlDriver?.ReleasePageUrl
                ?? PreviousHotfixDriver?.ReleasePageUrl
                ?? NvidiaDriverLookup.DriverPage),
            () => true);
        ToggleSilentDriverInstall = new Command(
            () => RunAsync(t => SaveSettingsAsync(settings with { SilentDriverInstall = !SilentDriverInstall }, t)),
            () => !IsBusy);
        ToggleDeleteDriverInstallerAfterInstall = new Command(
            () => RunAsync(t => SaveSettingsAsync(settings with { DeleteDriverInstallerAfterInstall = !DeleteDriverInstallerAfterInstall }, t)),
            () => !IsBusy);
        ToggleTranslateDriverSummaries = new Command(
            () => RunAsync(t => SaveSettingsAsync(settings with { TranslateDriverSummaries = !TranslateDriverSummaries }, t)),
            () => !IsBusy && CanTranslateDriverSummaries);
        SaveGoogleTranslateApiKey = new Command(
            () => RunAsync(t => SaveSettingsAsync(settings with
            {
                GoogleTranslateApiKey = string.IsNullOrWhiteSpace(GoogleTranslateApiKeyText)
                    ? null
                    : GoogleTranslateApiKeyText.Trim()
            }, t)),
            () => !IsBusy);
        SaveHotfixSourceSettings = new Command(
            () => RunAsync(t => SaveSettingsAsync(settings with
            {
                CustomHotfixFeedUrl = string.IsNullOrWhiteSpace(CustomHotfixFeedUrlText) ? null : CustomHotfixFeedUrlText.Trim(),
                GoogleSearchApiKey = string.IsNullOrWhiteSpace(GoogleSearchApiKeyText) ? null : GoogleSearchApiKeyText.Trim(),
                GoogleSearchCx = string.IsNullOrWhiteSpace(GoogleSearchCxText) ? null : GoogleSearchCxText.Trim()
            }, t)),
            () => !IsBusy);
        ClearDriverCache = new Command(
            ClearDriverCacheAsync,
            () => driverCacheBytes > 0 && !IsAnyDriverBusy);
    }

    private void NotifyDriverUi()
    {
        Notify(nameof(DriverState));
        Notify(nameof(DowngradeState));
        Notify(nameof(HasDriverUpdate));
        Notify(nameof(IsDriverHotfix));
        Notify(nameof(IsDriverDownloaded));
        Notify(nameof(IsDriverDownloading));
        Notify(nameof(IsDriverInstalling));
        Notify(nameof(IsDowngradeDownloading));
        Notify(nameof(IsDowngradeInstalling));
        Notify(nameof(IsAnyDriverBusy));
        Notify(nameof(ShowDriverFolderButton));
        Notify(nameof(ShowDriverReleaseButton));
        Notify(nameof(DriverVisualKind));
        Notify(nameof(DriverButtonGlyph));
        Notify(nameof(DriverButtonLabel));
        Notify(nameof(DriverButtonToolTip));
        Notify(nameof(DriverReleasePageToolTip));
        Notify(nameof(PreferredPreviousDriver));
        Notify(nameof(PreviousHotfixDriver));
        Notify(nameof(PreviousWhqlDriver));
        Notify(nameof(HasPreviousDriver));
        Notify(nameof(HasPreviousHotfixDriver));
        Notify(nameof(HasPreviousWhqlDriver));
        Notify(nameof(IsPreviousHotfixCached));
        Notify(nameof(IsPreviousWhqlCached));
        Notify(nameof(ShowRollbackSplitBar));
        Notify(nameof(ShowRollbackIconButton));
        Notify(nameof(RollbackVisualKind));
        Notify(nameof(RollbackButtonGlyph));
        Notify(nameof(RollbackButtonLabel));
        Notify(nameof(RollbackButtonToolTip));
        Notify(nameof(RollbackIconToolTip));
        Notify(nameof(PreviousHotfixMenuLabel));
        Notify(nameof(PreviousWhqlMenuLabel));
        Notify(nameof(PreviousHotfixToolTip));
        Notify(nameof(PreviousWhqlToolTip));
        Notify(nameof(RollbackMenuItems));
        Notify(nameof(ClearDriverCacheLabel));
    }

    private async Task SaveDriverTooltipStyleAsync(int change)
    {
        try
        {
            await WriteSettingsAsync(current => current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notes = [.. Notes, Message("Drivers.CouldNotSaveDriverPreviewSettings", e.Message)];
        }
        finally
        {
            if (change == driverTooltipStyleChange)
            {
                pendingDriverTooltipStyle = null;
                Notify(nameof(DriverPreviewStyle));
            }
        }
    }

    private async Task WriteSettingsAsync(
        Func<ManagerSettings, ManagerSettings> update, CancellationToken token = default, string? preferredLanguage = null)
    {
        await settingsWriteGate.WaitAsync(token);
        try
        {
            var updated = update(settings) with
            {
                DriverTooltipStyle = DriverPreviewStyle.ToString(),
                PreferredLanguage = preferredLanguage ?? settings.PreferredLanguage
            };
            await Task.Run(() => library.SaveSettings(updated), token);
            settings = updated;
        }
        finally
        {
            settingsWriteGate.Release();
        }
    }

    private Task PersistSettingsSilentlyAsync(ManagerSettings updated, CancellationToken token = default) =>
        WriteSettingsAsync(_ => updated, token);

    private async Task ReconcilePendingDowngradeOnCheckAsync(CancellationToken token)
    {
        if (settings.PendingDowngrade is not { } pending ||
            !Version.TryParse(pending.TargetVersion, out var targetVer) ||
            detectedGeForce?.Version != targetVer)
            return;

        if (pending.RetainInCacheAfterInstall)
            return;

        if (settings.DeleteDriverInstallerAfterInstall)
        {
            var driverType = pending.InstallerFileName.Contains("-Hotfix-", StringComparison.OrdinalIgnoreCase)
                ? NvidiaDriverType.Hotfix
                : NvidiaDriverType.WHQL;
            var targetInfo = new DriverUpdateInfo(driverType, pending.TargetVersion, targetVer);
            await Task.Run(() => DriverService.CleanupAfterDowngrade(targetInfo, targetVer, autoDeleteEnabled: true), token);
            await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = null }, token);
        }
        else
        {
            await PersistSettingsSilentlyAsync(
                settings with { PendingDowngrade = pending with { RetainInCacheAfterInstall = true } },
                token);
        }
    }

    private async Task RefreshDriverCacheSizeAsync()
    {
        var bytes = await Task.Run(() => DriverService.GetCacheSizeBytes());
        driverCacheBytes = bytes;
        Notify(nameof(ClearDriverCacheLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    public Task RefreshSystemAsync() => RefreshSystemAsync(forceHotfixRefresh: false);

    public async Task RefreshSystemAsync(bool forceHotfixRefresh)
    {
        if (refreshingSystem) return;
        refreshingSystem = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await Task.WhenAll(RefreshDriverAsync(forceHotfixRefresh), RefreshManagerReleaseAsync(), CheckModReleasesAsync(CancellationToken.None, false));
        }
        finally
        {
            refreshingSystem = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task RefreshDriverAsync(bool forceHotfixRefresh = false)
    {
        if (IsAnyDriverBusy)
            return;

        availableDriver = null;
        previousDrivers = new();
        allHistoryDrivers = [];
        lastHotfixSourceStatuses = [];
        lastHotfixScanUtc = null;
        lastHotfixFromCache = false;
        driverDownloadPercent = null;
        driverDownloadMb = 0;
        DriverState = DriverUiState.NoUpdate;
        driverCurrent = false;
        DriverColor = "#AEB8C9";
        DriverStatus = Message("Drivers.Checking");
        NotifyDriverUi();

        if (detectedGeForce is not { } gpu)
        {
            DriverStatus = Message("Drivers.GeForceDriverStatusUnknown");
            return;
        }

        await ReconcilePendingDowngradeOnCheckAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        DriverHistoryInfo history;
        try
        {
            history = await NvidiaDriverLookup.HistoryAsync(
                Http,
                gpu.Name,
                gpu.Version,
                timeout.Token,
                hotfixCachePath: DataFolders.HotfixFeedCachePath,
                forceHotfixRefresh: forceHotfixRefresh,
                hotfixFeedOptions: CurrentHotfixFeedOptions);
        }
        catch (OperationCanceledException)
        {
            history = new DriverHistoryInfo(null, null, new PreviousDriverOptions(), false);
        }

        var selected = history.AvailableUpdate(gpu.Version);
        var whql = history.LatestWhqlDriver;

        try
        {
            await Task.Run(() => DriverService.CleanupOutdatedInstallers(
                gpu.Version,
                selected?.ParsedVersion ?? whql?.ParsedVersion,
                settings.DeleteDriverInstallerAfterInstall,
                settings.PendingDowngrade?.InstallerFileName));
            await RefreshDriverCacheSizeAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notes = [.. Notes, Message("Drivers.DriverCacheCleanup", e.Message)];
        }

        previousDrivers = await Task.Run(() => DriverService.EnrichPreviousDrivers(history.PreviousDrivers, gpu.Version));
        allHistoryDrivers = await Task.Run(() => DriverService.EnrichDriverList(history.AllDrivers ?? history.PreviousDrivers.Drivers));
        lastHotfixChecked = history.HotfixChecked;
        lastHotfixSourceStatuses = history.HotfixSourceStatuses ?? [];
        lastHotfixScanUtc = history.HotfixLastScanUtc;
        lastHotfixFromCache = history.HotfixFromCache;

        if (selected is not null)
        {
            availableDriver = selected;
            var cached = await Task.Run(() => DriverService.GetCachedInstaller(selected));
            DriverStatus = Message("Drivers.AvailableV", (selected.IsHotfix ? "Hotfix" : "WHQL"), selected.Version);
            DriverColor = "#F7C56C";
            DriverState = cached is not null ? DriverUiState.Downloaded : DriverUiState.Available;
            NotifyDriverUi();
            return;
        }

        if (whql is not null)
        {
            driverCurrent = true;
            DriverStatus = history.HotfixChecked ? Message("Drivers.DriverUpToDate") : Message("Drivers.WHQLUpToDateHotfixUnchecked");
            DriverColor = "#A6D962";
        }
        else
        {
            DriverStatus = Message("Drivers.WHQLStatusUnknown");
        }
        NotifyDriverUi();
    }

    private async Task ExecutePrimaryDriverActionAsync()
    {
        if (DriverState == DriverUiState.Downloading)
        {
            if (Environment.TickCount64 - downloadStartedTick < 400)
                return;
            driverDownloadCts?.Cancel();
            return;
        }

        if (DriverState == DriverUiState.Downloaded)
        {
            await InstallDriverAsync(forceInteractive: false);
            return;
        }

        if (DriverState != DriverUiState.Available || availableDriver is not { } update)
            return;

        if (settings.PendingDowngrade is not null)
            await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = null });

        var existingCache = await Task.Run(() => DriverService.GetCachedInstaller(update));
        if (existingCache is not null)
        {
            DriverState = DriverUiState.Downloaded;
            await RefreshDriverCacheSizeAsync();
            SetTimedToast(Message("Drivers.NVIDIAVAlreadyCachedReadyToInstall", (update.IsHotfix ? "Hotfix" : "WHQL"), update.Version), "Success", 5);
            return;
        }

        var isNotebook = NvidiaDriverLookup.IsNotebookGpu(detectedGeForce?.Name);
        if (string.IsNullOrWhiteSpace(update.DirectInstallerUrl))
        {
            try
            {
                using var resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                update = await NvidiaDriverLookup.ResolveDirectInstallerUrlAsync(Http, update, isNotebook, resolveCts.Token);
                availableDriver = update;
                NotifyDriverUi();
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                Notes = [.. Notes, Message("Drivers.DriverLinkLookup", e.Message)];
            }

            if (string.IsNullOrWhiteSpace(update.DirectInstallerUrl))
            {
                if (!string.IsNullOrWhiteSpace(update.ReleasePageUrl))
                {
                    SetTimedToast(Message("Drivers.DirectLinkUnavailableOpeningTheDownloadPage", (update.IsHotfix ? "Hotfix" : "WHQL")), "Warning", 6);
                    await OpenShell(update.ReleasePageUrl);
                }
                else
                {
                    SetTimedToast(Message("Drivers.NoDownloadURLIsAvailableForThis"), "Warning", 6);
                }
                return;
            }
        }

        var kindLabel = update.IsHotfix ? "Hotfix" : "WHQL";
        using var cts = new CancellationTokenSource();
        driverDownloadCts = cts;
        downloadStartedTick = Environment.TickCount64;
        suppressDownloadToast = false;
        driverDownloadPercent = 0;
        driverDownloadMb = 0;
        DriverState = DriverUiState.Downloading;
        SetPinnedToast(Message("Drivers.DownloadingNVIDIAV", kindLabel, update.Version), "Active");

        var progress = new Progress<DriverDownloadProgress>(p =>
        {
            if (driverDownloadCts != cts || cts.IsCancellationRequested)
                return;

            driverDownloadPercent = p.Percent;
            driverDownloadMb = p.BytesReceived / 1048576d;
            Notify(nameof(DriverButtonLabel));

            if (suppressDownloadToast) return;

            var detail = p.TotalBytes is { } total
                ? $"{p.Percent}% ({p.BytesReceived / 1048576d:0} / {total / 1048576d:0} MB)"
                : $"{p.BytesReceived / 1048576d:0} MB";
            SetPinnedToast(Message("Drivers.DownloadingNVIDIAV2", kindLabel, update.Version, detail), "Active");
        });

        try
        {
            await DriverService.DownloadAsync(update, isNotebook, progress, cts.Token);
            DriverState = DriverUiState.Downloaded;
            await RefreshDriverCacheSizeAsync();
            Status = Message("Drivers.NVIDIAVDownloadedAndVerified", kindLabel, update.Version);
            SetTimedToast(Message("Drivers.NVIDIAVReadyToInstall", kindLabel, update.Version), "Success", 6);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            DriverState = DriverUiState.Available;
            await RefreshDriverCacheSizeAsync();
            SetTimedToast(Message("Drivers.DriverDownloadCancelled"), "Warning", 4);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        {
            DriverState = DriverUiState.Available;
            await RefreshDriverCacheSizeAsync();
            Notes = [.. Notes, Message("Drivers.DriverDownloadFailed", e.Message)];
            if (!string.IsNullOrWhiteSpace(update.ReleasePageUrl))
            {
                SetTimedToast(Message("Drivers.DirectDownloadFailedOpeningTheDownloadPage", e.Message), "Warning", 8);
                await OpenShell(update.ReleasePageUrl);
            }
            else
            {
                SetTimedToast(Message("Drivers.DriverDownloadFailed", e.Message), "Warning", 8);
            }
        }
        finally
        {
            if (ReferenceEquals(driverDownloadCts, cts))
                driverDownloadCts = null;
            driverDownloadPercent = null;
            driverDownloadMb = 0;
            NotifyDriverUi();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task InstallDriverAsync(bool forceInteractive)
    {
        if (availableDriver is not { } update) return;

        if (settings.PendingDowngrade is not null)
            await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = null });

        var installerPath = await Task.Run(() => DriverService.GetCachedInstaller(update));
        if (installerPath is null)
        {
            DriverState = DriverUiState.Available;
            await RefreshDriverCacheSizeAsync();
            SetTimedToast(Message("Drivers.TheCachedDriverInstallerIsMissingOr"), "Warning", 6);
            return;
        }

        var silent = SilentDriverInstall && !forceInteractive;
        if (!silent)
        {
            try
            {
                var startInfo = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath)!
                };
                var process = Process.Start(startInfo);
                SetTimedToast(Message("Drivers.NVIDIADriverInstallerStarted"), "Success", 5);
                if (process is not null)
                    _ = ObserveInteractiveInstallAsync(process, update);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                SetTimedToast(Message("Drivers.DriverInstallationCancelledAdministratorPrivilegesRequi"), "Warning", 6);
            }
            catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
            {
                SetTimedToast(Message("Drivers.CouldNotStartInstaller", e.Message), "Warning", 8);
            }
            return;
        }

        DriverState = DriverUiState.Installing;
        SetPinnedToast(Message("Drivers.InstallingNVIDIADriverVInTheBackground2", update.Version), "Active");

        Process? silentProcess = null;
        try
        {
            var startInfo = new ProcessStartInfo(installerPath)
            {
                Arguments = NvidiaDriverService.BuildInstallerArguments(silentInstall: true, cleanInstall: false),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath)!
            };
            silentProcess = Process.Start(startInfo) ?? throw new IOException(GetText("Drivers.CouldNotStartTheInstallerProcess"));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            DriverState = DriverUiState.Downloaded;
            SetTimedToast(Message("Drivers.ExpressSetupCancelledAdministratorPrivilegesRequired"), "Warning", 6);
            return;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            DriverState = DriverUiState.Downloaded;
            SetTimedToast(Message("Drivers.CouldNotStartExpressSetup", e.Message), "Warning", 8,
                GetText("Drivers.StartInteractively"), () => InstallDriverAsync(forceInteractive: true));
            return;
        }

        int exitCode;
        using (silentProcess)
        {
            await silentProcess.WaitForExitAsync();
            exitCode = silentProcess.ExitCode;
        }

        await RefreshDetectedGpuAsync(CancellationToken.None);

        if (exitCode == 0)
        {
            var cleaned = await Task.Run(() => DriverService.CleanupAfterInstall(
                update, detectedGeForce?.Version, settings.DeleteDriverInstallerAfterInstall));
            await RefreshDriverCacheSizeAsync();
            DriverState = DriverUiState.NoUpdate;
            await RefreshDriverAsync();
            Status = Message("Drivers.NVIDIADriverVInstalled", update.Version);
            SetTimedToast(
                cleaned ? Message("Drivers.DriverInstalledCacheCleaned") : Message("Drivers.NVIDIADriverVInstalled2", update.Version),
                "Success",
                6);
        }
        else if (exitCode == 1)
        {
            var cleaned = await Task.Run(() => DriverService.CleanupAfterInstall(
                update, detectedGeForce?.Version, settings.DeleteDriverInstallerAfterInstall));
            await RefreshDriverCacheSizeAsync();
            availableDriver = null;
            DriverState = DriverUiState.NoUpdate;
            driverCurrent = true;
            DriverColor = "#A6D962";
            DriverStatus = Message("Drivers.VInstalledRestartRecommended", update.Version);
            NotifyDriverUi();
            Status = Message("Drivers.DriverInstalledWindowsRestartRecommended");
            SetTimedToast(
                cleaned
                    ? Message("Drivers.DriverInstalledWindowsRestartRecommendedCacheCleaned")
                    : Message("Drivers.DriverInstalledWindowsRestartRecommended2"),
                "Success",
                8);
        }
        else
        {
            DriverState = DriverUiState.Downloaded;
            var codeText = exitCode < 0 ? $"{exitCode} (0x{unchecked((uint)exitCode):X8})" : exitCode.ToString();
            Notes = [.. Notes, Message("Drivers.NVIDIAExpressSetupSNorebootExitedWith", codeText)];
            SetTimedToast(
                Message("Drivers.ExpressSetupFailedExitCode", codeText),
                "Warning",
                20,
                GetText("Drivers.StartInteractively"),
                () => InstallDriverAsync(forceInteractive: true));
        }
    }

    private async Task ObserveInteractiveInstallAsync(Process process, DriverUpdateInfo update)
    {
        try
        {
            using (process)
            {
                await process.WaitForExitAsync();
            }

            await RefreshDetectedGpuAsync(CancellationToken.None);
            if (detectedGeForce?.Version >= update.ParsedVersion)
            {
                var cleaned = await Task.Run(() => DriverService.CleanupAfterInstall(
                    update, detectedGeForce?.Version, settings.DeleteDriverInstallerAfterInstall));
                await RefreshDriverCacheSizeAsync();
                await RefreshDriverAsync();
                SetTimedToast(
                    cleaned ? Message("Drivers.DriverInstalledCacheCleaned") : Message("Drivers.NVIDIADriverVInstalled2", update.Version),
                    "Success",
                    6);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Notes = [.. Notes, Message("Drivers.DriverStatusCheckAfterSetup", e.Message)];
        }
    }

    private async Task ExecutePrimaryRollbackActionAsync()
    {
        if (IsDowngradeDownloading)
        {
            if (Environment.TickCount64 - downloadStartedTick < 400)
                return;
            driverDownloadCts?.Cancel();
            return;
        }

        if (PreferredPreviousDriver is { } preferred)
            await StartDriverDowngradeFlowAsync(preferred);
    }

    private async Task OpenDriverHistoryOverviewAsync()
    {
        if (IsAnyDriverBusy) return;

        var gpuName = detectedGeForce?.Name ?? "NVIDIA GeForce";
        var installedVersion = detectedGeForce is { } gpu
            ? NvidiaDriverLookup.Display(gpu.Version)
            : GetText("Drivers.Unknown");
        var dialogDrivers = allHistoryDrivers.Length > 0
            ? allHistoryDrivers
            : previousDrivers.Drivers;

        var overview = new DriverHistoryWindow(
            gpuName,
            installedVersion,
            dialogDrivers,
            lastHotfixChecked,
            DataFolders.DriverDownloads,
            TranslateDriverSummaries,
            ClientLanguageCode,
            CanTranslateDriverSummaries ? DriverTranslator : null,
            async enabled =>
            {
                await PersistSettingsSilentlyAsync(settings with { TranslateDriverSummaries = enabled });
                Notify(nameof(TranslateDriverSummaries));
            },
            installedParsedVersion: detectedGeForce?.Version,
            sourceStatuses: lastHotfixSourceStatuses,
            lastScanUtc: lastHotfixScanUtc,
            fromCache: lastHotfixFromCache,
            onRefreshHotfixSources: RefreshDriverHistoryForDialogAsync)
        {
            Owner = Application.Current.MainWindow,
            DataContext = this
        };

        if (overview.ShowDialog() != true)
            return;

        if (overview.SelectedDriver is { } chosen)
        {
            if (detectedGeForce is { } current && chosen.ParsedVersion > current.Version)
            {
                availableDriver = chosen;
                var cached = await Task.Run(() => DriverService.GetCachedInstaller(chosen));
                DriverStatus = Message("Drivers.AvailableV", (chosen.IsHotfix ? "Hotfix" : "WHQL"), chosen.Version);
                DriverColor = "#F7C56C";
                DriverState = cached is not null ? DriverUiState.Downloaded : DriverUiState.Available;
                NotifyDriverUi();
                await ExecutePrimaryDriverActionAsync();
            }
            else
            {
                await StartDriverDowngradeFlowAsync(chosen);
            }
            return;
        }

        if (overview.SelectedLocalInstallerPath is { } localPath)
        {
            try
            {
                await Task.Run(() => DriverService.VerifyInstaller(localPath));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                SetTimedToast(Message("Drivers.InvalidDriverInstaller", e.Message), "Warning", 7);
                return;
            }

            var target = NvidiaDriverService.ParseLocalInstallerInfo(localPath);
            await PromptAndExecuteDowngradeAsync(target, explicitInstallerPath: localPath);
        }
    }

    private async Task<DriverHistoryRefreshSnapshot> RefreshDriverHistoryForDialogAsync()
    {
        if (detectedGeForce is not { } gpu)
        {
            return new DriverHistoryRefreshSnapshot(
                allHistoryDrivers.Length > 0 ? allHistoryDrivers : previousDrivers.Drivers,
                lastHotfixChecked,
                lastHotfixSourceStatuses,
                lastHotfixScanUtc,
                lastHotfixFromCache);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        DriverHistoryInfo history;
        try
        {
            history = await NvidiaDriverLookup.HistoryAsync(
                Http,
                gpu.Name,
                gpu.Version,
                timeout.Token,
                hotfixCachePath: DataFolders.HotfixFeedCachePath,
                forceHotfixRefresh: true,
                bypassHotfixCooldown: true,
                hotfixFeedOptions: CurrentHotfixFeedOptions);
        }
        catch (OperationCanceledException)
        {
            return new DriverHistoryRefreshSnapshot(
                allHistoryDrivers.Length > 0 ? allHistoryDrivers : previousDrivers.Drivers,
                lastHotfixChecked,
                lastHotfixSourceStatuses,
                lastHotfixScanUtc,
                lastHotfixFromCache);
        }

        previousDrivers = await Task.Run(() => DriverService.EnrichPreviousDrivers(history.PreviousDrivers, gpu.Version));
        allHistoryDrivers = await Task.Run(() => DriverService.EnrichDriverList(history.AllDrivers ?? history.PreviousDrivers.Drivers));
        lastHotfixChecked = history.HotfixChecked;
        lastHotfixSourceStatuses = history.HotfixSourceStatuses ?? [];
        lastHotfixScanUtc = history.HotfixLastScanUtc;
        lastHotfixFromCache = history.HotfixFromCache;

        if (!IsAnyDriverBusy && DriverState is DriverUiState.NoUpdate or DriverUiState.Available)
        {
            var selected = history.AvailableUpdate(gpu.Version);
            if (selected is not null)
            {
                availableDriver = selected;
                var cached = await Task.Run(() => DriverService.GetCachedInstaller(selected));
                DriverStatus = Message("Drivers.AvailableV", (selected.IsHotfix ? "Hotfix" : "WHQL"), selected.Version);
                DriverColor = "#F7C56C";
                DriverState = cached is not null ? DriverUiState.Downloaded : DriverUiState.Available;
                driverCurrent = false;
            }
            else if (history.LatestWhqlDriver is not null)
            {
                availableDriver = null;
                driverCurrent = true;
                DriverStatus = history.HotfixChecked ? Message("Drivers.DriverUpToDate") : Message("Drivers.WHQLUpToDateHotfixUnchecked");
                DriverColor = "#A6D962";
                DriverState = DriverUiState.NoUpdate;
            }
            NotifyDriverUi();
        }

        return new DriverHistoryRefreshSnapshot(
            allHistoryDrivers,
            lastHotfixChecked,
            lastHotfixSourceStatuses,
            lastHotfixScanUtc,
            lastHotfixFromCache);
    }

    private async Task StartDriverDowngradeFlowAsync(DriverUpdateInfo target)
    {
        if (IsAnyDriverBusy) return;

        var pending = new PendingDriverDowngrade(
            NvidiaDriverService.GetInstallerFileName(target),
            target.Version,
            RetainInCacheAfterInstall: false);
        await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = pending });

        var installerPath = await Task.Run(() => DriverService.GetCachedInstaller(target));
        if (installerPath is null)
        {
            var isNotebook = NvidiaDriverLookup.IsNotebookGpu(detectedGeForce?.Name);
            if (string.IsNullOrWhiteSpace(target.DirectInstallerUrl))
            {
                try
                {
                    using var resolveCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    target = await NvidiaDriverLookup.ResolveDirectInstallerUrlAsync(Http, target, isNotebook, resolveCts.Token);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
                {
                    Notes = [.. Notes, Message("Drivers.PreviousDriverLinkLookup", e.Message)];
                }

                if (string.IsNullOrWhiteSpace(target.DirectInstallerUrl))
                {
                    if (!string.IsNullOrWhiteSpace(target.ReleasePageUrl))
                    {
                        SetTimedToast(Message("Drivers.DirectLinkUnavailableOpeningTheDriverArchive"), "Warning", 6);
                        await OpenShell(target.ReleasePageUrl);
                    }
                    else
                    {
                        SetTimedToast(Message("Drivers.NoDownloadURLIsAvailableForThis2"), "Warning", 6);
                    }
                    return;
                }
            }

            using var cts = new CancellationTokenSource();
            driverDownloadCts = cts;
            downloadStartedTick = Environment.TickCount64;
            suppressDownloadToast = false;
            downgradeDownloadPercent = 0;
            downgradeDownloadMb = 0;
            activeDowngradeDriver = target;
            DowngradeState = DriverDowngradeUiState.Downloading;
            SetPinnedToast(Message("Drivers.DownloadingPreviousDriverV", target.Version), "Active");

            var progress = new Progress<DriverDownloadProgress>(p =>
            {
                if (driverDownloadCts != cts || cts.IsCancellationRequested)
                    return;

                downgradeDownloadPercent = p.Percent;
                downgradeDownloadMb = p.BytesReceived / 1048576d;
                Notify(nameof(RollbackButtonLabel));

                if (suppressDownloadToast) return;

                var progressText = p.Percent is { } pct
                    ? $"{pct}%"
                    : $"{p.BytesReceived / 1048576d:0} MB";
                SetPinnedToast(Message("Drivers.DownloadingPreviousDriverV2", target.Version, progressText), "Active");
            });

            var downloadedOk = false;
            try
            {
                installerPath = await DriverService.DownloadAsync(target, isNotebook, progress, cts.Token);
                downloadedOk = true;
                if (detectedGeForce is { } gpu)
                    previousDrivers = await Task.Run(() => DriverService.EnrichPreviousDrivers(previousDrivers, gpu.Version));
                await RefreshDriverCacheSizeAsync();
                Status = Message("Drivers.PreviousDriverVDownloadedAndVerified", target.Version);
                ClearToast();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                await RefreshDriverCacheSizeAsync();
                SetTimedToast(Message("Drivers.PreviousDriverDownloadCancelled"), "Warning", 4);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or OperationCanceledException)
            {
                await RefreshDriverCacheSizeAsync();
                Notes = [.. Notes, Message("Drivers.PreviousDriverDownloadFailed", e.Message)];
                if (!string.IsNullOrWhiteSpace(target.ReleasePageUrl))
                {
                    SetTimedToast(Message("Drivers.DirectDownloadFailedOpeningTheDownloadPage", e.Message), "Warning", 8);
                    await OpenShell(target.ReleasePageUrl);
                }
                else
                {
                    SetTimedToast(Message("Drivers.DownloadFailed", e.Message), "Warning", 8);
                }
            }
            finally
            {
                if (ReferenceEquals(driverDownloadCts, cts))
                    driverDownloadCts = null;
                downgradeDownloadPercent = null;
                downgradeDownloadMb = 0;
                activeDowngradeDriver = null;
                DowngradeState = DriverDowngradeUiState.Idle;
                CommandManager.InvalidateRequerySuggested();
            }

            if (!downloadedOk || installerPath is null)
                return;
        }

        installerPath = await Task.Run(() => DriverService.GetCachedInstaller(target));
        if (installerPath is null)
        {
            await RefreshDriverCacheSizeAsync();
            SetTimedToast(Message("Drivers.TheCachedInstallerForThePreviousDriver"), "Warning", 6);
            return;
        }

        await PromptAndExecuteDowngradeAsync(target, explicitInstallerPath: null);
    }

    private async Task PromptAndExecuteDowngradeAsync(DriverUpdateInfo target, string? explicitInstallerPath)
    {
        var dduPath = await Task.Run(() => NvidiaDriverService.FindDduExecutable(settings.DduExecutablePath, storage));
        var currentVersionDisplay = detectedGeForce is { } detected
            ? NvidiaDriverLookup.Display(detected.Version)
            : GetText("Drivers.Unknown");

        var dialog = new DriverDowngradeWindow(
            currentVersionDisplay,
            target,
            SilentDriverInstall,
            dduPath,
            DataFolders.DriverDownloads)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() != true)
            return;

        await ExecuteDowngradeInstallAsync(target, dialog.CleanInstall, forceInteractive: false, explicitInstallerPath);
    }

    private async Task ExecuteDowngradeInstallAsync(
        DriverUpdateInfo target,
        bool cleanInstall,
        bool forceInteractive,
        string? explicitInstallerPath = null)
    {
        var isExternalLocalFile = !string.IsNullOrWhiteSpace(explicitInstallerPath);
        var installerPath = isExternalLocalFile
            ? explicitInstallerPath
            : await Task.Run(() => DriverService.GetCachedInstaller(target));
        if (installerPath is null || !File.Exists(installerPath))
        {
            await RefreshDriverCacheSizeAsync();
            SetTimedToast(Message("Drivers.ThePreviousDriverInstallerIsMissingFrom"), "Warning", 6);
            return;
        }

        var silent = SilentDriverInstall && !forceInteractive;
        if (!silent)
        {
            try
            {
                var startInfo = new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installerPath)!
                };
                var process = Process.Start(startInfo);
                SetTimedToast(
                    cleanInstall
                        ? Message("Drivers.NVIDIAInstallerStartedChooseCustomAdvancedAnd")
                        : Message("Drivers.NVIDIAInstallerStartedForDowngradeToV", target.Version),
                    "Success",
                    cleanInstall ? 9 : 6);
                if (process is not null)
                    _ = ObserveInteractiveDowngradeAsync(process, target, isExternalLocalFile);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                SetTimedToast(Message("Drivers.DriverDowngradeCancelledAdministratorPrivilegesRequired"), "Warning", 6);
            }
            catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
            {
                SetTimedToast(Message("Drivers.CouldNotStartInstaller", e.Message), "Warning", 8);
            }
            return;
        }

        activeDowngradeDriver = target;
        DowngradeState = DriverDowngradeUiState.Installing;
        SetPinnedToast(
            cleanInstall
                ? Message("Drivers.RunningACleanInstallToDowngradeTo", target.Version)
                : Message("Drivers.DowngradingDriverToV2", target.Version),
            "Active");

        Process? silentProcess = null;
        try
        {
            var startInfo = new ProcessStartInfo(installerPath)
            {
                Arguments = NvidiaDriverService.BuildInstallerArguments(silentInstall: true, cleanInstall),
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath)!
            };
            silentProcess = Process.Start(startInfo) ?? throw new IOException(GetText("Drivers.CouldNotStartTheInstallerProcess"));
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            activeDowngradeDriver = null;
            DowngradeState = DriverDowngradeUiState.Idle;
            SetTimedToast(Message("Drivers.DriverDowngradeCancelledAdministratorPrivilegesRequired"), "Warning", 6);
            return;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            activeDowngradeDriver = null;
            DowngradeState = DriverDowngradeUiState.Idle;
            SetTimedToast(Message("Drivers.CouldNotStartDowngrade", e.Message), "Warning", 8,
                GetText("Drivers.StartInteractively"), () => ExecuteDowngradeInstallAsync(target, cleanInstall, forceInteractive: true, explicitInstallerPath));
            return;
        }

        int exitCode;
        using (silentProcess)
        {
            await silentProcess.WaitForExitAsync();
            exitCode = silentProcess.ExitCode;
        }

        activeDowngradeDriver = null;
        DowngradeState = DriverDowngradeUiState.Idle;
        await RefreshDetectedGpuAsync(CancellationToken.None);

        if (exitCode == 0)
        {
            if (!isExternalLocalFile)
                await FinalizeCompletedDowngradeAsync(target);
            await RefreshDriverAsync();
            Status = Message("Drivers.DowngradeToNVIDIAVComplete", target.Version);
            SetTimedToast(Message("Drivers.DowngradeToVComplete", target.Version), "Success", 7);
        }
        else if (exitCode == 1)
        {
            if (!isExternalLocalFile)
                await FinalizeCompletedDowngradeAsync(target);
            DriverColor = "#F7C56C";
            DriverStatus = Message("Drivers.VInstalledRestartRequired", target.Version);
            NotifyDriverUi();
            Status = Message("Drivers.DowngradeToVCompleteWindowsRestartRequired", target.Version);
            SetTimedToast(Message("Drivers.DowngradeCompleteWindowsRestartRequired"), "Success", 9);
        }
        else
        {
            var codeText = exitCode < 0 ? $"{exitCode} (0x{unchecked((uint)exitCode):X8})" : exitCode.ToString();
            var args = NvidiaDriverService.BuildInstallerArguments(silentInstall: true, cleanInstall);
            Notes = [.. Notes, Message("Drivers.NVIDIADowngradeExitedWithCode", args, codeText)];
            SetTimedToast(
                Message("Drivers.DowngradeFailedExitCode", codeText),
                "Warning",
                20,
                GetText("Drivers.StartInteractively"),
                () => ExecuteDowngradeInstallAsync(target, cleanInstall, forceInteractive: true, explicitInstallerPath));
        }
    }

    private async Task FinalizeCompletedDowngradeAsync(DriverUpdateInfo target)
    {
        if (detectedGeForce?.Version == target.ParsedVersion)
        {
            await Task.Run(() => DriverService.CleanupAfterDowngrade(target, detectedGeForce?.Version, settings.DeleteDriverInstallerAfterInstall));
            if (settings.DeleteDriverInstallerAfterInstall)
            {
                await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = null });
            }
            else if (settings.PendingDowngrade is { } pending && !pending.RetainInCacheAfterInstall)
            {
                await PersistSettingsSilentlyAsync(
                    settings with { PendingDowngrade = pending with { RetainInCacheAfterInstall = true } });
            }
        }
        await RefreshDriverCacheSizeAsync();
    }

    private async Task ObserveInteractiveDowngradeAsync(Process process, DriverUpdateInfo target, bool isExternalLocalFile)
    {
        try
        {
            using (process)
            {
                await process.WaitForExitAsync();
            }

            await RefreshDetectedGpuAsync(CancellationToken.None);
            if (detectedGeForce?.Version == target.ParsedVersion)
            {
                if (!isExternalLocalFile)
                    await FinalizeCompletedDowngradeAsync(target);
                await RefreshDriverAsync();
                SetTimedToast(Message("Drivers.DowngradeToVComplete", target.Version), "Success", 7);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            Notes = [.. Notes, Message("Drivers.DriverStatusCheckAfterDowngrade", e.Message)];
        }
    }

    private async Task ClearDriverCacheAsync()
    {
        if (IsAnyDriverBusy) return;
        var (freed, deleted) = await Task.Run(() => DriverService.ClearCache());
        if (settings.PendingDowngrade is not null)
            await PersistSettingsSilentlyAsync(settings with { PendingDowngrade = null });
        if (DriverState == DriverUiState.Downloaded)
            DriverState = availableDriver is not null ? DriverUiState.Available : DriverUiState.NoUpdate;
        if (detectedGeForce is { } gpu)
            previousDrivers = await Task.Run(() => DriverService.EnrichPreviousDrivers(previousDrivers, gpu.Version));
        await RefreshDriverCacheSizeAsync();
        NotifyDriverUi();
        Status = deleted == 0
            ? Message("Drivers.TheDriverCacheIsAlreadyEmpty")
            : Message("Drivers.DriverCacheClearedFreedMB", freed / 1048576d);
        SetTimedToast(
            deleted == 0 ? Message("Drivers.TheDriverCacheIsAlreadyEmpty") : Message("Drivers.DriverCacheClearedMBFreed", freed / 1048576d),
            "Success",
            5);
    }

    private static string BuildDriverToolTip(string headline, string? summary) =>
        string.IsNullOrWhiteSpace(summary) ? headline : $"{headline}\n\n{summary}";

    private async Task RefreshManagerReleaseAsync()
    {
        try
        {
            var release = await UpdateService.CheckManagerAsync(Http, AppVersion, ReleaseChannel.Stable);
            AppUpdateLabel = release is null ? "" : Message("Drivers.AppUpdateAvailable", release.Tag);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException)
        { AppUpdateLabel = Message("Drivers.AppUpdateCheckFailed"); }
    }
}
