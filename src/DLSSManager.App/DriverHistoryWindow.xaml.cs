using static DLSSManager.Core.LocalizationService;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public sealed record DriverHistoryRefreshSnapshot(
    DriverUpdateInfo[] Drivers,
    bool HotfixChecked,
    HotfixSourceStatus[] SourceStatuses,
    DateTimeOffset? LastScanUtc,
    bool FromCache);

public partial class DriverHistoryWindow : Window
{
    private sealed class SourceStatusPill
    {
        public SourceStatusPill(HotfixSourceStatus status)
        {
            var sourceName = status.SourceName == "Google Suche" ? GetText("Drivers.GoogleSearch") : status.SourceName;
            var isPositive = status.Status is HotfixSourceFetchStatus.Success or HotfixSourceFetchStatus.CacheHit;
            var isBlocked = status.Status == HotfixSourceFetchStatus.Blocked403_429;
            var isError = status.Status == HotfixSourceFetchStatus.TimeoutOrError;

            Label = status.Status switch
            {
                HotfixSourceFetchStatus.Blocked403_429 => GetText("Drivers.Blocked", sourceName),
                HotfixSourceFetchStatus.TimeoutOrError => GetText("Drivers.Error", sourceName),
                _ => $"{sourceName} ({status.FoundCount})"
            };

            if (isBlocked || isError)
            {
                PillForeground = CreateFrozenBrush(0xFC, 0xD3, 0x4D);
                PillBackground = CreateFrozenBrush(0x1A, 0xF5, 0x9E, 0x0B);
                PillBorder = CreateFrozenBrush(0x3D, 0xF5, 0x9E, 0x0B);
            }
            else if (isPositive && status.FoundCount > 0)
            {
                PillForeground = CreateFrozenBrush(0x81, 0xC7, 0x84);
                PillBackground = CreateFrozenBrush(0x1A, 0x4C, 0xAF, 0x50);
                PillBorder = CreateFrozenBrush(0x38, 0x4C, 0xAF, 0x50);
            }
            else
            {
                PillForeground = CreateFrozenBrush(0x94, 0xA3, 0xB8);
                PillBackground = CreateFrozenBrush(0x0F, 0xFF, 0xFF, 0xFF);
                PillBorder = CreateFrozenBrush(0x1A, 0xFF, 0xFF, 0xFF);
            }

            var statusText = status.Status switch
            {
                HotfixSourceFetchStatus.Success => GetText("Drivers.CheckedLive"),
                HotfixSourceFetchStatus.CacheHit => GetText("Drivers.FromHTTPCache"),
                HotfixSourceFetchStatus.Blocked403_429 => GetText("Drivers.BlockedHTTP"),
                HotfixSourceFetchStatus.TimeoutOrError => GetText("Drivers.TimeoutOrNetworkError"),
                _ => GetText("Drivers.Unknown")
            };
            var localTime = DateTime.SpecifyKind(status.LastCheckedUtc, DateTimeKind.Utc).ToLocalTime();
            ToolTip = GetText("Drivers.ResultsHHMm", sourceName, statusText, status.FoundCount, localTime);
        }

        public string Label { get; }
        public Brush PillForeground { get; }
        public Brush PillBackground { get; }
        public Brush PillBorder { get; }
        public string ToolTip { get; }

        private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    private sealed class DriverRow : INotifyPropertyChanged
    {
        private string? translatedSummary;
        private bool useTranslated;
        private DriverHoverSummary? hoverSummary;

        private DriverRow(string dividerTitle)
        {
            IsDivider = true;
            VersionLabel = dividerTitle;
            ActionLabel = "";
        }

        public DriverRow(
            DriverUpdateInfo driver,
            string versionLabel,
            bool isHotfix,
            bool isWhql,
            bool isCached,
            string? releaseDateLabel,
            bool hasReleaseDate,
            string? originalSummary,
            string? releasePageUrl,
            bool hasReleasePage,
            bool useTranslated,
            Version? installedVersion,
            bool isLatestHotfixUpdate = false,
            bool isPreviousHotfix = false)
        {
            Driver = driver;
            VersionLabel = versionLabel;
            IsHotfix = isHotfix;
            IsWhql = isWhql;
            IsCached = isCached;
            IsLatestHotfixUpdate = isLatestHotfixUpdate;
            IsPreviousHotfix = isPreviousHotfix;
            ReleaseDateLabel = releaseDateLabel;
            HasReleaseDate = hasReleaseDate;
            OriginalSummary = originalSummary;
            ReleasePageUrl = releasePageUrl;
            HasReleasePage = hasReleasePage;
            this.useTranslated = useTranslated;

            if (installedVersion is not null && driver.ParsedVersion > installedVersion)
            {
                IsUpdateAction = true;
                ActionLabel = "Update";
                CanSelectAction = true;
            }
            else if (installedVersion is not null && driver.ParsedVersion == installedVersion)
            {
                IsInstalledAction = true;
                ActionLabel = GetText("Drivers.Installed");
                CanSelectAction = false;
            }
            else
            {
                ActionLabel = "Downgrade";
                CanSelectAction = true;
            }
        }

        public static DriverRow Divider(string title) => new(title);

        public bool IsDivider { get; }
        public bool IsDriverRow => !IsDivider;
        public DriverUpdateInfo? Driver { get; }
        public string VersionLabel { get; }
        public bool IsHotfix { get; }
        public bool IsWhql { get; }
        public bool IsCached { get; }
        public bool IsLatestHotfixUpdate { get; }
        public bool IsPreviousHotfix { get; }
        public string? ReleaseDateLabel { get; }
        public bool HasReleaseDate { get; }
        public string? OriginalSummary { get; }
        public string? ReleasePageUrl { get; }
        public bool HasReleasePage { get; }
        public bool IsUpdateAction { get; }
        public bool IsInstalledAction { get; }
        public bool CanSelectAction { get; }
        public string ActionLabel { get; }

        public string? TranslatedSummary
        {
            get => translatedSummary;
            set
            {
                if (string.Equals(translatedSummary, value, StringComparison.Ordinal)) return;
                translatedSummary = value;
                NotifySummaryChanged();
            }
        }

        public bool UseTranslated
        {
            get => useTranslated;
            set
            {
                if (useTranslated == value) return;
                useTranslated = value;
                NotifySummaryChanged();
            }
        }

        private string? ActiveSummary =>
            useTranslated && !string.IsNullOrWhiteSpace(translatedSummary)
                ? translatedSummary
                : OriginalSummary;

        public bool HasSummary => !IsDivider && !string.IsNullOrWhiteSpace(ActiveSummary);

        public DriverHoverSummary HoverSummary => hoverSummary ??=
            DriverHoverSummary.Create(ActiveSummary, OriginalSummary, IsHotfix);

        public string? SummaryPreview
        {
            get
            {
                if (IsDivider || string.IsNullOrWhiteSpace(ActiveSummary))
                    return null;

                var lines = ActiveSummary
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Take(3);
                return string.Join(" · ", lines);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifySummaryChanged()
        {
            hoverSummary = null;
            OnPropertyChanged(nameof(SummaryPreview));
            OnPropertyChanged(nameof(HoverSummary));
            OnPropertyChanged(nameof(HasSummary));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private DriverRow[] allRows = [];
    private readonly string driverCacheFolder;
    private readonly string clientLanguage;
    private readonly DriverSummaryTranslator? translator;
    private readonly Func<bool, Task>? onTranslateChanged;
    private readonly Version? installedParsedVersion;
    private readonly Func<Task<DriverHistoryRefreshSnapshot>>? onRefreshHotfixSources;
    private HotfixSourceStatus[] sourceStatuses;
    private DateTimeOffset? lastScanUtc;
    private bool fromCache;
    private bool hotfixChecked;
    private bool isRefreshingSources;
    private bool translateEnabled;
    private CancellationTokenSource? translationCts;
    private string activeFilter = "All";
    private readonly DispatcherTimer summaryHoverTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(400)
    };
    private readonly DispatcherTimer summaryCloseTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200)
    };
    private FrameworkElement? pendingSummaryRow;
    private FrameworkElement? summaryRow;
    private Point summaryAnchor;

    public DriverUpdateInfo? SelectedDriver { get; private set; }
    public string? SelectedLocalInstallerPath { get; private set; }

    public DriverHistoryWindow(
        string gpuName,
        string installedVersion,
        DriverUpdateInfo[] drivers,
        bool hotfixChecked,
        string driverCacheFolder,
        bool translateEnabled = false,
        string? clientLanguage = null,
        DriverSummaryTranslator? translator = null,
        Func<bool, Task>? onTranslateChanged = null,
        Version? installedParsedVersion = null,
        HotfixSourceStatus[]? sourceStatuses = null,
        DateTimeOffset? lastScanUtc = null,
        bool fromCache = false,
        Func<Task<DriverHistoryRefreshSnapshot>>? onRefreshHotfixSources = null)
    {
        InitializeComponent();
        summaryHoverTimer.Tick += OpenSummaryHover;
        summaryCloseTimer.Tick += CloseSummaryAfterGrace;
        Deactivated += (_, _) => CloseSummaryHover();
        LocationChanged += (_, _) => CloseSummaryHover();
        this.driverCacheFolder = driverCacheFolder;
        this.clientLanguage = NormalizePreference(clientLanguage) == Auto ? LocalizationService.Language : NormalizePreference(clientLanguage);
        this.translator = translator;
        this.onTranslateChanged = onTranslateChanged;
        this.installedParsedVersion = installedParsedVersion ?? (Version.TryParse(installedVersion, out var parsed) ? parsed : null);
        this.sourceStatuses = sourceStatuses ?? [];
        this.lastScanUtc = lastScanUtc;
        this.fromCache = fromCache;
        this.hotfixChecked = hotfixChecked;
        this.onRefreshHotfixSources = onRefreshHotfixSources;

        var canTranslate = translator is not null && this.clientLanguage == German;
        this.translateEnabled = canTranslate && translateEnabled;

        SubtitleText.Text = GetText("Drivers.CurrentlyInstalledGeForceV", gpuName, installedVersion);
        RefreshSourcesButton.Visibility = onRefreshHotfixSources is not null ? Visibility.Visible : Visibility.Collapsed;

        if (canTranslate)
        {
            TranslateToggleButton.IsChecked = this.translateEnabled;
            TranslateToggleButton.Visibility = Visibility.Visible;
        }

        BuildRows(drivers);
        UpdateSourceStatusBar();

        Closed += (_, _) =>
        {
            CloseSummaryHover();
            translationCts?.Cancel();
            translationCts?.Dispose();
            translationCts = null;
        };

        ApplyFilter();

        if (this.translateEnabled)
            StartTranslation();
    }

    private void BuildRows(DriverUpdateInfo[] drivers)
    {
        var previousTranslations = allRows
            .Where(r => r.Driver is not null && !string.IsNullOrWhiteSpace(r.TranslatedSummary))
            .GroupBy(r => (r.Driver!.DriverType, r.Driver.Version))
            .ToDictionary(g => g.Key, g => g.First().TranslatedSummary);

        var latestHotfixUpdateVersion = installedParsedVersion is not null
            ? drivers.Where(d => d.IsHotfix && d.ParsedVersion is not null && d.ParsedVersion > installedParsedVersion)
                .OrderByDescending(d => d.ParsedVersion)
                .FirstOrDefault()?.ParsedVersion
            : null;
        var previousHotfixVersion = installedParsedVersion is not null
            ? drivers.Where(d => d.IsHotfix && d.ParsedVersion is not null && d.ParsedVersion < installedParsedVersion)
                .OrderByDescending(d => d.ParsedVersion)
                .FirstOrDefault()?.ParsedVersion
            : null;

        allRows = drivers
            .Where(d => d.ParsedVersion is not null)
            .OrderByDescending(d => d.ParsedVersion)
            .ThenByDescending(d => d.IsHotfix)
            .Select(d =>
            {
                var isLatestHf = d.IsHotfix && latestHotfixUpdateVersion is not null && d.ParsedVersion == latestHotfixUpdateVersion;
                var isPrevHf = d.IsHotfix && previousHotfixVersion is not null && d.ParsedVersion == previousHotfixVersion;
                var row = new DriverRow(
                    driver: d,
                    versionLabel: $"v{d.Version}",
                    isHotfix: d.IsHotfix,
                    isWhql: !d.IsHotfix,
                    isCached: d.IsCached,
                    releaseDateLabel: d.ReleaseDate,
                    hasReleaseDate: !string.IsNullOrWhiteSpace(d.ReleaseDate),
                    originalSummary: d.ReleaseSummary,
                    releasePageUrl: d.ReleasePageUrl,
                    hasReleasePage: !string.IsNullOrWhiteSpace(d.ReleasePageUrl),
                    useTranslated: translateEnabled,
                    installedVersion: installedParsedVersion,
                    isLatestHotfixUpdate: isLatestHf,
                    isPreviousHotfix: isPrevHf);

                if (previousTranslations.TryGetValue((d.DriverType, d.Version), out var translated))
                    row.TranslatedSummary = translated;

                return row;
            })
            .ToArray();
    }

    private void UpdateSourceStatusBar()
    {
        var showStatusBar = activeFilter is "All" or "Hotfix" &&
                            (sourceStatuses.Length > 0 || isRefreshingSources);
        HotfixSourcesBar.Visibility = showStatusBar ? Visibility.Visible : Visibility.Collapsed;
        HotfixNoticeBanner.Visibility = hotfixChecked ? Visibility.Collapsed : Visibility.Visible;

        SourceStatusList.ItemsSource = sourceStatuses.Select(s => new SourceStatusPill(s)).ToArray();

        if (isRefreshingSources)
        {
            SourceTimestampText.Text = GetText("Drivers.Scanning");
        }
        else if (fromCache && lastScanUtc is { } cachedAt)
        {
            var age = DateTimeOffset.UtcNow - cachedAt;
            if (age < TimeSpan.FromMinutes(1))
                SourceTimestampText.Text = GetText("Drivers.FromCacheJustNow");
            else if (age < TimeSpan.FromHours(1))
                SourceTimestampText.Text = GetText("Drivers.FromCacheMinAgo", (int)Math.Max(1, Math.Floor(age.TotalMinutes)));
            else
                SourceTimestampText.Text = GetText("Drivers.FromCache", cachedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        else if (lastScanUtc is { } scanAt)
        {
            SourceTimestampText.Text = GetText("Drivers.AsOf", scanAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        else
        {
            SourceTimestampText.Text = "";
        }

        RefreshSourcesButton.IsEnabled = !isRefreshingSources && onRefreshHotfixSources is not null;
    }

    private async void RefreshHotfixSources(object sender, RoutedEventArgs e)
    {
        if (isRefreshingSources || onRefreshHotfixSources is null)
            return;

        isRefreshingSources = true;
        UpdateSourceStatusBar();
        try
        {
            var snapshot = await onRefreshHotfixSources();
            if (!IsLoaded) return;

            hotfixChecked = snapshot.HotfixChecked;
            sourceStatuses = snapshot.SourceStatuses;
            lastScanUtc = snapshot.LastScanUtc;
            fromCache = snapshot.FromCache;
            BuildRows(snapshot.Drivers);
            ApplyFilter();

            if (translateEnabled)
                StartTranslation();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
        }
        finally
        {
            isRefreshingSources = false;
            UpdateSourceStatusBar();
        }
    }

    private async void ToggleTranslation(object sender, RoutedEventArgs e)
    {
        var enabled = TranslateToggleButton.IsChecked == true;
        translateEnabled = enabled;

        foreach (var row in allRows)
            row.UseTranslated = enabled;

        if (enabled)
        {
            StartTranslation();
        }
        else
        {
            translationCts?.Cancel();
            translationCts?.Dispose();
            translationCts = null;
        }

        if (onTranslateChanged is not null)
        {
            try
            {
                await onTranslateChanged(enabled);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void StartTranslation()
    {
        if (translator is null || !ClientLanguageProvider.CanTranslateFromEnglish(clientLanguage))
            return;

        translationCts?.Cancel();
        translationCts?.Dispose();
        var cts = new CancellationTokenSource();
        translationCts = cts;
        _ = TranslateRowsAsync(cts.Token);
    }

    private async Task TranslateRowsAsync(CancellationToken token)
    {
        if (translator is null) return;

        foreach (var row in allRows)
        {
            if (token.IsCancellationRequested)
                return;

            if (row.Driver is null || string.IsNullOrWhiteSpace(row.OriginalSummary) || !string.IsNullOrWhiteSpace(row.TranslatedSummary))
                continue;

            try
            {
                var translated = await translator.TranslateAsync(
                    row.Driver.Version,
                    row.OriginalSummary,
                    clientLanguage,
                    token);

                if (token.IsCancellationRequested)
                    return;

                if (!string.IsNullOrWhiteSpace(translated) &&
                    !string.Equals(translated, row.OriginalSummary, StringComparison.Ordinal))
                {
                    row.TranslatedSummary = translated;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        }
    }

    private void SelectFilter(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        activeFilter = ReferenceEquals(button, FilterWhqlButton) ? "WHQL"
            : ReferenceEquals(button, FilterHotfixButton) ? "Hotfix"
            : ReferenceEquals(button, FilterCachedButton) ? "Cached"
            : "All";

        FilterAllButton.Tag = activeFilter == "All" ? "Active" : null;
        FilterWhqlButton.Tag = activeFilter == "WHQL" ? "Active" : null;
        FilterHotfixButton.Tag = activeFilter == "Hotfix" ? "Active" : null;
        FilterCachedButton.Tag = activeFilter == "Cached" ? "Active" : null;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        CloseSummaryHover();
        var filtered = activeFilter switch
        {
            "WHQL" => allRows.Where(r => r.IsWhql).ToArray(),
            "Hotfix" => allRows.Where(r => r.IsHotfix).ToArray(),
            "Cached" => allRows.Where(r => r.IsCached).ToArray(),
            _ => allRows
        };

        var installedGen = installedParsedVersion is { } ver && ver.Major >= 100
            ? ver.Major / 100
            : (int?)null;

        if (activeFilter == "Hotfix" &&
            installedGen is { } gen &&
            filtered.Length > 0 &&
            !filtered.Any(r => r.Driver?.ParsedVersion is { } dv && dv.Major / 100 >= gen))
        {
            GenerationHintText.Text = GetText("Drivers.NoNewerHotfixHasBeenReleasedFor", gen);
            GenerationHintBanner.Visibility = Visibility.Visible;
        }
        else
        {
            GenerationHintBanner.Visibility = Visibility.Collapsed;
        }

        var displayList = new List<DriverRow>(filtered.Length + 1);
        if (installedGen is { } currentGen)
        {
            var currentOrNewer = filtered.Where(r => r.Driver?.ParsedVersion is { } dv && dv.Major / 100 >= currentGen).ToArray();
            var olderGen = filtered.Where(r => r.Driver?.ParsedVersion is { } dv && dv.Major / 100 < currentGen).ToArray();
            displayList.AddRange(currentOrNewer);
            if (olderGen.Length > 0)
            {
                displayList.Add(DriverRow.Divider(GetText("Drivers.EarlierDriverGenerations")));
                displayList.AddRange(olderGen);
            }
        }
        else
        {
            displayList.AddRange(filtered);
        }

        DriverList.ItemsSource = displayList;
        CountText.Text = filtered.Length == 1 ? GetText("Drivers.Version") : GetText("Drivers.Versions", filtered.Length);
        EmptyStateText.Text = activeFilter switch
        {
            "Hotfix" => GetText("Drivers.NoHotfixVersionsWereFoundOnlineOr"),
            "Cached" => GetText("Drivers.NoOlderDriverInstallersAreCachedLocally"),
            _ => GetText("Drivers.NoDriverVersionsMatchThisFilter")
        };
        EmptyStateText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSourceStatusBar();
    }

    private void StartSummaryHover(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DriverRow { IsDriverRow: true } } element)
            return;

        CancelPendingSummary();
        if (ReferenceEquals(element, summaryRow))
        {
            summaryCloseTimer.Stop();
            return;
        }

        pendingSummaryRow = element;
        summaryHoverTimer.Start();
    }

    private void EndSummaryHover(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, pendingSummaryRow))
            CancelPendingSummary();

        if (ReferenceEquals(sender, summaryRow))
            summaryCloseTimer.Start();
    }

    private void UnloadSummaryRow(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, pendingSummaryRow))
            CancelPendingSummary();

        if (ReferenceEquals(sender, summaryRow))
            HideSummaryCard();
    }

    private void EnterSummaryCard(object sender, MouseEventArgs e)
    {
        summaryCloseTimer.Stop();
        CancelPendingSummary();
    }

    private void LeaveSummaryCard(object sender, MouseEventArgs e)
    {
        if (summaryRow is not null)
            summaryCloseTimer.Start();
    }

    private void OpenSummaryHover(object? sender, EventArgs e)
    {
        var element = pendingSummaryRow;
        CancelPendingSummary();
        if (element is not { IsMouseOver: true, DataContext: DriverRow row } || !IsActive)
            return;

        summaryCloseTimer.Stop();
        summaryRow = element;
        summaryAnchor = Mouse.GetPosition(SummaryViewport);
        SummaryOverlay.DataContext = row;
        DetailedScroll.ScrollToTop();
        PlaceSummaryHover();
    }

    private void CancelPendingSummary()
    {
        summaryHoverTimer.Stop();
        pendingSummaryRow = null;
    }

    private void CloseSummaryAfterGrace(object? sender, EventArgs e)
    {
        summaryCloseTimer.Stop();
        if (summaryRow is not { IsMouseOver: true } && !SummaryOverlay.IsMouseOver)
            HideSummaryCard();
    }

    private void HideSummaryCard()
    {
        summaryCloseTimer.Stop();
        summaryRow = null;
        SummaryOverlay.Visibility = Visibility.Hidden;
        SummaryOverlay.DataContext = null;
    }

    private void CloseSummaryHover()
    {
        CancelPendingSummary();
        HideSummaryCard();
    }

    private void UpdateSummaryPlacement(object sender, SizeChangedEventArgs e)
    {
        if (SummaryOverlay.DataContext is DriverRow)
            PlaceSummaryHover();
    }

    private void PlaceSummaryHover()
    {
        const double edgeInset = 8;
        const double gap = 6;
        var anchorOnScreen = SummaryViewport.PointToScreen(summaryAnchor);
        var workArea = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)anchorOnScreen.X, (int)anchorOnScreen.Y)).WorkingArea;
        var available = new Rect(
            SummaryViewport.PointFromScreen(new Point(workArea.Left, workArea.Top)),
            SummaryViewport.PointFromScreen(new Point(workArea.Right, workArea.Bottom)));
        available.Intersect(new Rect(SummaryViewport.RenderSize));

        if (available.IsEmpty || available.Width <= 2 * edgeInset || available.Height <= 2 * edgeInset)
        {
            SummaryOverlay.Visibility = Visibility.Hidden;
            return;
        }

        available.Inflate(-edgeInset, -edgeInset);
        var isDetailed = DetailedCard.Visibility == Visibility.Visible;
        var cardWidth = isDetailed ? DetailedCard.MaxWidth : SummaryCard.MaxWidth;
        // Keep the card beside the pointer so it can be reached without crossing another row.
        var anchorX = Math.Clamp(summaryAnchor.X, available.Left, available.Right);
        var spaceLeft = anchorX - available.Left - gap;
        var spaceRight = available.Right - anchorX - gap;
        var openRight = spaceRight >= cardWidth || spaceRight >= spaceLeft;
        SummaryOverlay.MaxWidth = Math.Clamp(openRight ? spaceRight : spaceLeft, 0, cardWidth);
        SummaryOverlay.Width = isDetailed ? SummaryOverlay.MaxWidth : double.NaN;
        SummaryOverlay.MaxHeight = isDetailed ? available.Height : double.PositiveInfinity;
        SummaryOverlay.Measure(new Size(SummaryOverlay.MaxWidth, SummaryOverlay.MaxHeight));
        var size = SummaryOverlay.DesiredSize;

        if (size.Width <= 0 || size.Height <= 0 || size.Height > available.Height)
        {
            SummaryOverlay.Visibility = Visibility.Hidden;
            return;
        }

        var left = openRight ? anchorX + gap : anchorX - gap - size.Width;
        Canvas.SetLeft(SummaryOverlay, Math.Clamp(left, available.Left, Math.Max(available.Left, available.Right - size.Width)));
        Canvas.SetTop(SummaryOverlay, Math.Clamp(summaryAnchor.Y - 18, available.Top, Math.Max(available.Top, available.Bottom - size.Height)));
        SummaryOverlay.Visibility = Visibility.Visible;
    }

    private void SelectDriverForDowngrade(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DriverUpdateInfo driver })
        {
            SelectedDriver = driver;
            DialogResult = true;
        }
    }

    private void PickLocalInstaller(object sender, RoutedEventArgs e)
    {
        var initialDir = Directory.Exists(driverCacheFolder)
            ? driverCacheFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var picker = new OpenFileDialog
        {
            Title = GetText("Drivers.SelectALocalNVIDIADriverInstallerExe"),
            InitialDirectory = initialDir,
            Filter = GetText("Drivers.NVIDIADriverInstallerExeExe")
        };
        if (picker.ShowDialog(this) == true)
        {
            SelectedLocalInstallerPath = picker.FileName;
            DialogResult = true;
        }
    }

    private void OpenHotfixPortal(object sender, RoutedEventArgs e) =>
        TryOpenShell(NvidiaDriverLookup.HotfixListUrl);

    private void OpenDriverDetails(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
            TryOpenShell(url);
    }

    private void OpenCacheFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(driverCacheFolder);
            TryOpenShell(driverCacheFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryOpenShell(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
        }
    }
}
