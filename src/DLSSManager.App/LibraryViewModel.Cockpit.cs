using static DLSSManager.Core.LocalizationService;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    private const string ProjectUrl = "https://github.com/MrSchwafel/DLSS-Manager";
    private const string UnlockerName = "FG & MFG Unlocker";
    private string optiProxy = "version.dll";
    private readonly HashSet<string> driftedGames = new(StringComparer.OrdinalIgnoreCase);
    private LocalizedText drsStatus = Message("Cockpit.CheckingNVIDIADRS");
    private LocalizedText releaseStatus = "";
    private bool modUpdatesAvailable;
    private ModVersion? unlocker;
    private bool unlockerUpdatesPaused;
    private string? optiInstalledProxy;
    private string? modSettingsRoot;

    public bool HasUnlockerProxies => ModProxies.Length > 0;
    public string[] OptiProxies { get; } = ["version.dll", "dxgi.dll", "winmm.dll", "d3d12.dll"];
    public string OptiProxy { get => optiProxy; set => Set(ref optiProxy, value); }
    public string CatalogBadge => (CatalogEntries.Count(e => e.Availability == "Lokal") +
        (modPackageDirectory is null ? 0 : 1)).ToString();
    public bool ModUpdatesAvailable { get => modUpdatesAvailable; private set => Set(ref modUpdatesAvailable, value); }
    public LocalizedText ReleaseStatus { get => releaseStatus; private set => Set(ref releaseStatus, value); }
    public string BackupBadge => Backups.Length.ToString();
    public string LastScanLabel => lastScan is { } scan
        ? GetText("Cockpit.LastScanAt", scan.Date == DateTime.Today ? GetText("Cockpit.Today") : scan.ToString("d", Culture), scan) : "—";
    public string CatalogSummary => GetText("Cockpit.CatalogSRFG", HighestLocal("SR"), HighestLocal("FG"));
    public bool HasUpdateDrift => driftedGames.Count > 0;
    public string LibraryHealth => HasUpdateDrift ? GetText("Cockpit.UpdatesAvailableGames", driftedGames.Count) :
        GetText("Cockpit.GamesModified", allGames.Length, allGames.Count(g => g.Modified));
    public string DrsStatus => drsStatus.Text;
    public string UnlockerStatus => unlocker is { } version ? GetText("Cockpit.FGMFGInstalled", version.Display) + (unlockerUpdatesPaused ? GetText("Cockpit.UpdatesPaused") : "") :
        SelectedLibraryGame?.Inventory?.Dlls.Any(d => d.ModComponent) == true ? GetText("Cockpit.ModFilesDetected") : GetText("Cockpit.NotInstalled");
    public bool UnlockerInstalled => unlocker is not null;
    public bool UnlockerHasFiles => UnlockerInstalled || SelectedLibraryGame?.Inventory?.Dlls.Any(d => d.ModComponent) == true;
    public string UnlockerBadge => UnlockerInstalled ? GetText("Drivers.Installed") : UnlockerHasFiles ? GetText("Cockpit.Detected") : GetText("Cockpit.Missing");
    public string UnlockerInstallLabel => UnlockerHasFiles
        ? GetText("Cockpit.Reinstall") : GetText("Cockpit.Install");

    public ICommand QuickScan { get; private set; } = null!;
    public ICommand ClearSearch { get; private set; } = null!;
    public ICommand CopyProfile { get; private set; } = null!;
    public ICommand OpenBackupFolder { get; private set; } = null!;
    public ICommand ChooseDataFolder { get; private set; } = null!;
    public ICommand ClearShaderCache { get; private set; } = null!;
    public ICommand OpenProject { get; private set; } = null!;
    public ICommand CheckModReleases { get; private set; } = null!;
    public ICommand InstallUnlocker { get; private set; } = null!;
    public ICommand UpdateLibraryUnlocker { get; private set; } = null!;
    public ICommand RemoveUnlocker { get; private set; } = null!;

    private void InitializeCockpitCommands()
    {
        InitializeSystemCommands();
        InitializeIniCommands();
        InitializeAutoUpdateCommands();
        QuickScan = new Command(ScanLibraryAsync, () => !IsBusy);
        ClearSearch = new Command(() => { LibraryQuery = ""; return Task.CompletedTask; }, () => LibraryQuery.Length > 0);
        CopyProfile = new Command(() => RunAsync(CopyProfileAsync), HasGame);
        OpenBackupFolder = new Command(() => RunAsync(_ => OpenShell(Path.Combine(storage, "Backups"))), () => !IsBusy);
        ChooseDataFolder = new Command(() => RunAsync(_ => ChangeDataFolderAsync()), () => !IsBusy && DataFolders.CanChoose);
        ClearShaderCache = new Command(() => RunAsync(ClearShaderCacheAsync), () => !IsBusy);
        OpenProject = new Command(() => RunAsync(_ => OpenShell(ProjectUrl)), () => !IsBusy);
        CheckModReleases = new Command(() => RunAsync(t => CheckModReleasesAsync(t, true)), () => !IsBusy);
        InstallUnlocker = new Command(() => ChangeLibraryUnlocker(false), CanInstallLibraryUnlocker);
        UpdateLibraryUnlocker = new Command(() => ChangeLibraryUnlocker(false, newerOnly: true),
            () => CanInstallLibraryUnlocker() && unlocker?.ReleaseTag is not null && !unlockerUpdatesPaused);
        RemoveUnlocker = new Command(() => ChangeLibraryUnlocker(true), () => CanChangeLibraryUnlocker() && UnlockerInstalled);
    }

    private void DetectUpdateDrift(IEnumerable<ScanGame> scanned)
    {
        foreach (var game in scanned)
        {
            if (!inventories.TryGetValue(game.Root.Path, out var previous)) continue;
            if (previous.Dlls.Any(before => before.HasOriginalBackup && before.Metadata is not null &&
                !game.Dlls.Any(after => SamePath(after.Path, before.Path) && after.Metadata?.Sha256 == before.Metadata.Sha256)))
                driftedGames.Add(game.Root.Path);
        }
        Notify(nameof(HasUpdateDrift));
        Notify(nameof(LibraryHealth));
    }

    private string HighestLocal(string kind) => CatalogEntries.FirstOrDefault(e => e.Kind == kind && e.Availability == "Lokal")?.Version.ToString() ?? "—";

    private void NotifyCatalogSummary()
    {
        Notify(nameof(CatalogBadge));
        Notify(nameof(CatalogSummary));
    }

    private async Task<int> AddLauncherDiscoveriesAsync(ScanGame[] scanned, CancellationToken token)
    {
        var discoveries = scanned.Where(g => IsLauncherSource(g.Root.Launcher) &&
            !LauncherDiscovery.IsKnownLauncherTool(g.Root.Title, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.Root.Path))))
            .DistinctBy(g => g.Root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var fresh = discoveries.Where(g => !allGames.Any(r => SamePath(r.Path, g.Root.Path)))
            .Select(g => new LibraryRow(g.Root.Title, g.Root.Path, settings.DefaultProxy,
                GameExecutableSelection.FromScan(g) ?? "", Id: Guid.NewGuid(), Launcher: g.Root.Launcher)).ToArray();
        var updated = allGames.Where(row => !IsNonGameLauncherEntry(row)).Select(row =>
        {
            if (row.Ignored || row.Executable.Length != 0) return row;
            var game = discoveries.FirstOrDefault(g => SamePath(row.Path, g.Root.Path));
            return game is not null && GameExecutableSelection.FromScan(game) is { } executable
                ? row with { Executable = executable } : row;
        }).ToArray();
        if (fresh.Length > 0 || !updated.SequenceEqual(allGames)) await SaveGamesAsync([.. updated, .. fresh], token);
        return fresh.Length;
    }

    private static bool IsNonGameLauncherEntry(LibraryRow row) => IsXboxSaveEntry(row) ||
        IsLauncherSource(row.Launcher) && LauncherDiscovery.IsKnownLauncherTool(row.Title,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(row.Path)));

    private static bool IsXboxSaveEntry(LibraryRow row) => row.Launcher is "Xbox" or "Xbox / Game Pass" &&
        row.Executable.Length == 0 && row.Title.Equals("GameSave", StringComparison.OrdinalIgnoreCase) &&
        WindowsLauncherDiscovery.IsXboxSaveEntry(row.Path);

    private static bool IsLauncherSource(string launcher) => launcher is "Steam" or "Epic" or "Ubisoft Connect" or "Ubisoft" or "GOG" or "Xbox" or "Xbox / Game Pass";

    private Task ChangeDataFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = GetText("Cockpit.ChooseDataFolder"), InitialDirectory = storage };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true || SamePath(storage, dialog.FolderName))
            return Task.CompletedTask;
        if (!Confirm(GetText("Cockpit.ChangeDataFolder"), GetText("Cockpit.TheFolderWillBeUsedTheNext", dialog.FolderName)))
            return Task.CompletedTask;
        DataFolders.Choose(dialog.FolderName);
        Toast = Status = Message("Cockpit.DataFolderSavedRestartDLSSManager");
        return Task.CompletedTask;
    }

    private async Task CopyProfileAsync(CancellationToken token)
    {
        var row = SelectedLibraryGame!;
        var installedUnlocker = unlocker;
        var installedOptiProxy = optiInstalledProxy;
        uint? preset = null;
        if (row.Executable.Length > 0)
        {
            try { preset = await Task.Run(() => NvidiaProfiles.Read(ProfileExecutable(row)), token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
        }
        var json = JsonSerializer.Serialize(new
        {
            game = row.Title,
            launcher = row.DisplayLauncher,
            unlocker = installedUnlocker is { } version ? new { version = version.Display, proxy = version.Proxy } : null,
            optiScaler = row.ManagedOpti || row.Inventory?.OptiScalerPresent == true ? new { proxy = installedOptiProxy } : null,
            dlls = row.Inventory?.Dlls.Where(d => !d.ModComponent && d.Kind is "SR" or "FG" or "RR" or "XeSS" or "FSR")
                .Select(d => new { file = Path.GetFileName(d.Path), version = d.Version }),
            dlssPreset = preset is null ? null : Presets.FirstOrDefault(p => p.Value == preset)?.Label ?? preset.ToString()
        }, new JsonSerializerOptions { WriteIndented = true });
        Clipboard.SetText(json);
        Toast = Status = Message("Cockpit.CopiedProfileAsJSON");
    }

    private async Task ClearShaderCacheAsync(CancellationToken token)
    {
        var result = await Task.Run(() =>
        {
            long bytes = 0;
            var skipped = 0;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA");
            foreach (var name in new[] { "DXCache", "GLCache" })
            {
                var path = SafePaths.Root(Path.Combine(root, name));
                if (!Directory.Exists(path)) continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        SafePaths.RejectLinks(file.FullName);
                        var size = file.Length;
                        file.Delete();
                        bytes += size;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                }
            }
            return (bytes, skipped);
        }, token);
        Toast = Status = Message("Cockpit.NVIDIAShaderCacheFreedN2MiB", result.bytes / 1048576d) +
            (result.skipped > 0 ? Message("Cockpit.SkippedFiles", result.skipped) : ".");
    }

    private static string? ResolveProxy(Installation game, string executable, string preferred, IEnumerable<string> supported,
        string? owned = null)
    {
        var directory = Path.GetDirectoryName(SafePaths.Child(game.Root, executable))!;
        bool Free(string name) => name.Equals(owned, StringComparison.OrdinalIgnoreCase) ||
            SafePaths.Attributes(SafePaths.Child(directory, name)) is null;
        if (Free(preferred)) return preferred;
        var alternative = supported.FirstOrDefault(Free);
        var message = GetText("Cockpit.IsAlreadyInUseInTheGame", preferred);
        if (alternative is null)
        {
            ConfirmationDialog.Show(Application.Current.MainWindow, message + GetText("Cockpit.NoSupportedProxyNameIsAvailable"), GetText("Cockpit.ProxyConflict"));
            return null;
        }
        return Confirm(GetText("Cockpit.ProxyConflict"), GetText("Cockpit.InstallWith", message, alternative)) ? alternative : null;
    }

    private bool CanChangeLibraryUnlocker() => HasGame() &&
        SelectedLibraryGame?.Inventory is { } game && ExecutableFor(game) is not null;

    private static bool ConfirmAntiCheat(ScanGame game) => game.AntiCheat is not { Length: > 0 } names ||
        Confirm(GetText("Cockpit.AntiCheatDetected"), GetText("Cockpit.ChangingDLLsOrModsMayResultIn", game.Root.Title, string.Join(", ", names)));

    private bool CanInstallLibraryUnlocker() => CanChangeLibraryUnlocker() &&
        SelectedLibraryGame?.Inventory?.Dlls.Any(d => d.Kind == "FG" && !d.ModComponent && CanReplace(d)) == true;

    private Task ChangeLibraryUnlocker(bool remove, bool newerOnly = false)
    {
        var row = SelectedLibraryGame!;
        var game = row.Inventory!;
        var installed = unlocker;
        var target = InstallationFor(game);
        var exe = target.Executables[0];
        return RunAsync(async token =>
        {
            if (!remove && !ConfirmAntiCheat(game)) return;
            var service = ManagedMods(RecommendedFrames(row.Title));
            if (!remove && (newerOnly || modPackageDirectory is null))
            {
                Status = Message("Cockpit.CheckingForAStableUnlockerRelease");
                await FetchUnlockerAsync(token);
            }
            if (newerOnly && (installed?.ReleaseTag is not { } installedTag ||
                !DllVersionService.IsUpgrade(installedTag.TrimStart('v', 'V'), modReleaseTag?.TrimStart('v', 'V'))))
            {
                Toast = Status = Message("Cockpit.NoNewerStableReleaseIsAvailableFor");
                return;
            }
            var preferred = newerOnly && installed is not null
                ? ModProxies.FirstOrDefault(p => p.Equals(installed.Proxy, StringComparison.OrdinalIgnoreCase)) ?? SelectedModProxy!
                : SelectedModProxy!;
            var proxy = remove ? null : ResolveProxy(target, exe, preferred, ModProxies, installed?.Proxy);
            if (!remove && proxy is null) return;
            if (proxy is not null) SelectedModProxy = proxy;
            if (remove && !Confirm(GetText("Cockpit.Remove", UnlockerName), game.Root.Title)) return;
            var id = await Task.Run(() =>
            {
                if (remove) return service.Remove(target, exe, null, token);
                GameScanner.RequireModifiable(target.Root, token, game.AntiCheat);
                var fg = game.Dlls.First(d => d.Kind == "FG" && !d.ModComponent && CanReplace(d));
                var package = ModPackage.Read(modPackageDirectory!, proxy!, token);
                return service.Install(target, exe, Path.GetRelativePath(target.Root, fg.Path), package,
                    modReleaseTag ?? "SM86", modReleaseTag, newerOnly, token);
            }, token);
            await RefreshGameInventoryAsync(game, exe);
            RefreshModCockpit(force: true);
            Toast = Status = id is null ? Message("Cockpit.FilesAreAlreadyUpToDateOr") :
                remove ? Message("Cockpit.UnlockerRemoved") : newerOnly ? Message("Cockpit.UpdatedUnlockerTo", modReleaseTag) : Message("Cockpit.UnlockerInstalled");
        });
    }

    private ModDownloadService UnlockerDownloads() => new(Http, Path.Combine(storage, "Katalog", "Unlocker"));

    private async Task LoadCachedUnlockerAsync(CancellationToken token)
    {
        try
        {
            if (await Task.Run(() => UnlockerDownloads().ReadCached(token), token) is { } release)
            {
                SelectModPackage(release.Directory, release.Proxies, release.Tag);
                NotifyCatalogSummary();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { Notes = [.. Notes, Message("Cockpit.UnlockerCatalog", e.Message)]; }
    }

    private async Task FetchUnlockerAsync(CancellationToken token)
    {
        var release = await UnlockerDownloads().GetLatestAsync(token);
        SelectModPackage(release.Directory, release.Proxies, release.Tag);
        NotifyCatalogSummary();
    }

    private void RefreshModCockpit(bool force = false)
    {
        RefreshIniStatus();
        var game = SelectedLibraryGame?.Inventory;
        var executable = game is null ? null : ExecutableFor(game);
        var directory = executable is null ? null : Path.GetDirectoryName(executable);
        if (!force && directory == modSettingsRoot)
        {
            NotifyUnlockerCard();
            return;
        }
        modSettingsRoot = directory;
        unlocker = null;
        unlockerUpdatesPaused = false;
        optiInstalledProxy = null;
        if (game is not null && executable is not null)
        {
            try
            {
                var target = InstallationFor(game);
                var exe = target.Executables[0];
                var revision = ManagedMods().History(target, exe).FirstOrDefault()?.Mod;
                unlocker = revision?.Version;
                unlockerUpdatesPaused = revision?.UpdatesPaused == true;
                if (InstalledOpti(target, exe) is { } installed)
                    optiInstalledProxy = Path.GetFileName(new OptiScalerService(CreateTransactions()).InstalledProxy(installed));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { Notes = [.. Notes, e.Message]; }
        }
        NotifyUnlockerCard();
        NotifyOptiCard();
    }

    private void NotifyUnlockerCard()
    {
        Notify(nameof(UnlockerInstalled));
        Notify(nameof(UnlockerInstallLabel));
        Notify(nameof(UnlockerStatus));
        Notify(nameof(UnlockerHasFiles));
        Notify(nameof(UnlockerBadge));
    }

    private sealed record ModRelease(string tag_name, ReleaseAsset[] assets);
    private sealed record ReleaseAsset(string name);

    private async Task CheckModReleasesAsync(CancellationToken token, bool reportStatus)
    {
        if (reportStatus) Status = Message("Cockpit.CheckingGitHubReleases");
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var releases = await Task.WhenAll(
                CheckModReleaseAsync("optiscaler/OptiScaler", "OptiScaler", timeout.Token),
                CheckModReleaseAsync("sdli1995/dlssg_for_sm86", "Unlocker", timeout.Token));
            token.ThrowIfCancellationRequested();
            ModUpdatesAvailable = releases.Any(r => r.Available);
            ReleaseStatus = releases[0].Label + " · " + releases[1].Label;
            if (reportStatus) Toast = Status = ReleaseStatus;
        }
    }

    private async Task<(LocalizedText Label, bool Available, string? Tag)> CheckModReleaseAsync(string repository, string label, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("DLSSManager-CSharp");
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<ModRelease>(cancellationToken: token)
                ?? throw new IOException(GetText("Cockpit.ReleaseMissing"));
            var installed = label == "Unlocker" ? modReleaseTag : OptiPackages.FirstOrDefault()?.Source.Split(' ').LastOrDefault();
            token.ThrowIfCancellationRequested();
            var available = release.assets.Length > 0 && DllVersionService.Parse(release.tag_name.TrimStart('v')) is { } latest &&
                (DllVersionService.Parse(installed?.TrimStart('v')) is not { } local || latest > local);
            return ((LocalizedText)$"{label} {release.tag_name}" + (release.assets.Length == 0 ? Message("Cockpit.NoGitHubPackage") : ""), available,
                release.assets.Length > 0 ? release.tag_name : null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or OperationCanceledException)
        { return (Message("Cockpit.CheckFailed", label), false, null); }
    }
}
