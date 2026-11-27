using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using DLSSManager.Core;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    private static readonly TimeSpan AutoUpdatePollInterval = TimeSpan.FromSeconds(10);
    private DispatcherTimer? autoUpdateTimer;
    private DateTimeOffset nextAutoUpdate;
    private string autoUpdateIntervalText = "60";

    public bool AutoUpdateDlls => settings.AutoUpdateDlls;
    public bool AutoUpdateUnlocker => settings.AutoUpdateUnlocker;
    public bool AutoUpdateOptiScaler => settings.AutoUpdateOptiScaler;
    public string AutoUpdateIntervalText { get => autoUpdateIntervalText; set => Set(ref autoUpdateIntervalText, value); }
    public ICommand ToggleAutoUpdateDlls { get; private set; } = null!;
    public ICommand ToggleAutoUpdateUnlocker { get; private set; } = null!;
    public ICommand ToggleAutoUpdateOptiScaler { get; private set; } = null!;
    public ICommand SaveAutoUpdateInterval { get; private set; } = null!;
    public ICommand RunAutoUpdates { get; private set; } = null!;

    private bool AnyAutoUpdates => AutoUpdateDlls || AutoUpdateUnlocker || AutoUpdateOptiScaler;
    private int AutoUpdateInterval => Math.Clamp(settings.AutoUpdateIntervalMinutes, 1, 1440);

    private void InitializeAutoUpdateCommands()
    {
        ToggleAutoUpdateDlls = Toggle(() => settings with { AutoUpdateDlls = !AutoUpdateDlls });
        ToggleAutoUpdateUnlocker = Toggle(() => settings with { AutoUpdateUnlocker = !AutoUpdateUnlocker });
        ToggleAutoUpdateOptiScaler = Toggle(() => settings with { AutoUpdateOptiScaler = !AutoUpdateOptiScaler });
        SaveAutoUpdateInterval = new Command(() => RunAsync(async token =>
        {
            if (!int.TryParse(AutoUpdateIntervalText, out var minutes) || minutes is < 1 or > 1440)
                throw new ArgumentException("Das Prüfintervall muss zwischen 1 und 1440 Minuten liegen.");
            await SaveSettingsAsync(settings with { AutoUpdateIntervalMinutes = minutes }, token);
            AutoUpdateIntervalText = minutes.ToString();
            nextAutoUpdate = DateTimeOffset.UtcNow.AddMinutes(minutes);
        }), () => !IsBusy);
        RunAutoUpdates = new Command(() => CheckAutomaticUpdatesAsync(reportStatus: true), () => !IsBusy && AnyAutoUpdates);

        ICommand Toggle(Func<ManagerSettings> change) => new Command(() => RunAsync(async token =>
        {
            await SaveSettingsAsync(change(), token);
            nextAutoUpdate = DateTimeOffset.UtcNow;
        }), () => !IsBusy);
    }

    private void LoadAutoUpdateSettings()
    {
        AutoUpdateIntervalText = AutoUpdateInterval.ToString();
        Notify(nameof(AutoUpdateDlls));
        Notify(nameof(AutoUpdateUnlocker));
        Notify(nameof(AutoUpdateOptiScaler));
    }

    public Task UpdateModsOnStartupAsync()
    {
        if (autoUpdateTimer is not null) return Task.CompletedTask;
        nextAutoUpdate = DateTimeOffset.UtcNow;
        autoUpdateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutoUpdatePollInterval };
        autoUpdateTimer.Tick += OnAutoUpdateTick;
        autoUpdateTimer.Start();
        return CheckAutomaticUpdatesAsync();
    }

    public void StopAutoUpdates()
    {
        if (autoUpdateTimer is null) return;
        autoUpdateTimer.Stop();
        autoUpdateTimer.Tick -= OnAutoUpdateTick;
        autoUpdateTimer = null;
    }

    private async void OnAutoUpdateTick(object? sender, EventArgs args)
    {
        if (DateTimeOffset.UtcNow >= nextAutoUpdate) await CheckAutomaticUpdatesAsync();
    }

    private async Task CheckAutomaticUpdatesAsync(bool reportStatus = false)
    {
        if (IsBusy || !AnyAutoUpdates) return;
        nextAutoUpdate = DateTimeOffset.UtcNow.AddMinutes(AutoUpdateInterval);
        await RunAsync(async token =>
        {
            var result = new AutoUpdateResult();
            var rows = allGames.Where(row => !row.Ignored && row.Inventory is not null).ToArray();
            if (AutoUpdateDlls)
                await TryAutoUpdateAsync("DLSS/XeSS/FSR", result, token, () => UpdateNativeDllsAsync(rows, result, token));
            if (AutoUpdateUnlocker)
                await TryAutoUpdateAsync("SM86", result, token, () => UpdateManagedModsAsync(IniMod.Sm86, rows, result, token));
            if (AutoUpdateOptiScaler)
                await TryAutoUpdateAsync("OptiScaler", result, token, () => UpdateManagedModsAsync(IniMod.OptiScaler, rows, result, token));

            if (result.Updated > 0) RefreshModCockpit(force: true);
            if (result.Errors.Count > 0) Notes = [.. Notes, .. result.Errors];
            var summary = result.Updated == 0 ? "Keine Updates angewendet." : $"{result.Updated} Updates angewendet.";
            if (result.Deferred > 0) summary += $" {result.Deferred} wegen laufender Spiele verschoben.";
            if (result.Errors.Count > 0) summary += $" {result.Errors.Count} Hinweise unter „Hinweise und Messwerte“.";
            Status = summary;
            if (reportStatus || result.Updated > 0 || result.Deferred > 0 || result.Errors.Count > 0)
                Toast = summary;
        });
    }

    private async Task UpdateNativeDllsAsync(LibraryRow[] rows, AutoUpdateResult result, CancellationToken token)
    {
        Status = "DLL-Katalog wird auf neue Versionen geprüft …";
        var release = await Task.Run(() => catalog.RefreshAsync(token), token);
        await LoadCatalogAsync(release.Entries, token);
        if (release.Notice is { } notice) result.Errors.Add(notice);
        foreach (var row in rows.DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var game = inventories.GetValueOrDefault(row.Path) ?? row.Inventory!;
            var candidates = UpdateCandidates(game).ToArray();
            if (candidates.Length == 0 || ExecutableFor(game) is null) continue;
            await TryAutoUpdateAsync(row.Title, result, token, async () =>
            {
                var installation = InstallationFor(game);
                var current = await Task.Run(() =>
                {
                    new WindowsInstallationGuard().RequireClosed(installation);
                    return candidates.Where(c => File.Exists(c.Dll.Path))
                        .Select(c => c.Dll with { Metadata = DllDetectionService.Inspect(c.Dll.Path, token) }).ToArray();
                }, token);
                var updates = UpdateCandidates(game with { Dlls = current }).ToArray();
                if (updates.Length == 0) return;
                await ApplyUpdatesAsync(game, updates, token);
                result.Updated += updates.Length;
            });
        }
    }

    private async Task UpdateManagedModsAsync(IniMod mod, LibraryRow[] rows, AutoUpdateResult result, CancellationToken token)
    {
        var sm86 = mod == IniMod.Sm86;
        var label = sm86 ? "SM86" : "OptiScaler";
        Status = $"{label} · Installationen werden geprüft …";
        var opti = new OptiScalerService(CreateTransactions(), IniProfiles());
        var sm86History = ManagedMods();
        var optiPackages = OptiPackages;
        var candidates = rows.SelectMany(row => GameTargets(row).Select(target => (row, target)))
            .DistinctBy(entry => entry.target.Root + "|" + Path.GetDirectoryName(entry.target.Executables[0]),
                StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = await Task.Run(() =>
        {
            var eligible = new List<ManagedUpdate>();
            foreach (var (row, game) in candidates)
            {
                token.ThrowIfCancellationRequested();
                if (row.Inventory!.AntiCheat is { Length: > 0 }) continue;
                try
                {
                    var exe = game.Executables[0];
                    if (sm86)
                    {
                        var revision = sm86History.History(game, exe).FirstOrDefault()?.Mod;
                        if (revision is not { UpdatesPaused: false, Version: { ReleaseTag: { } tag } version }) continue;
                        var fg = row.Inventory.Dlls.FirstOrDefault(d => d.Kind == "FG" && CanReplace(d));
                        if (fg is null) continue;
                        eligible.Add(new(row, game, tag, version.Proxy, Path.GetRelativePath(game.Root, fg.Path)));
                    }
                    else if (opti.Installed(game.Root) is { } journal && opti.InstalledProxyEntry(journal) is { } proxy &&
                             SamePath(Path.GetDirectoryName(SafePaths.Child(game.Root, proxy.Path))!,
                                 Path.GetDirectoryName(SafePaths.Child(game.Root, exe))!))
                    {
                        var cachedTags = optiPackages.Where(p => p.Sha256 == proxy.After)
                            .Select(OptiScalerService.PackageReleaseTag).OfType<string>().Distinct().ToArray();
                        var tag = journal.ComponentReleaseTag ?? (cachedTags.Length == 1 ? cachedTags[0] : null);
                        if (tag is not null) eligible.Add(new(row, game, tag, Path.GetFileName(proxy.Path), null, journal.Id));
                    }
                }
                catch (Exception e) when (IsAutoUpdateError(e)) { result.Errors.Add($"{row.Title} · {label}: {e.Message}"); }
            }
            return eligible;
        }, token);
        if (targets.Count == 0) return;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var release = await CheckModReleaseAsync(sm86 ? "sdli1995/dlssg_for_sm86" : "optiscaler/OptiScaler", label, timeout.Token);
        token.ThrowIfCancellationRequested();
        if (release.Tag is null)
        {
            result.Errors.Add(release.Label);
            return;
        }
        var ready = new List<ManagedUpdate>();
        foreach (var target in targets.Where(t => NewerRelease(t.Tag, release.Tag)))
            await TryAutoUpdateAsync(target.Row.Title, result, token, () =>
            {
                new WindowsInstallationGuard().RequireClosed(target.Game);
                ready.Add(target);
                return Task.CompletedTask;
            });
        if (ready.Count == 0) return;

        Status = $"{label} {release.Tag} · Update wird geladen …";
        var unlockerPackage = sm86 ? await UnlockerDownloads().GetLatestAsync(token) : null;
        var optiPackage = sm86 ? null : await GetOptiReleaseAsync(token);
        var tagToInstall = unlockerPackage?.Tag ?? (optiPackage is null ? null : OptiScalerService.PackageReleaseTag(optiPackage));
        foreach (var target in ready.Where(t => NewerRelease(t.Tag, tagToInstall)))
        {
            token.ThrowIfCancellationRequested();
            await TryAutoUpdateAsync($"{target.Row.Title} · {label}", result, token, async () =>
            {
                var game = target.Game;
                var exe = game.Executables[0];
                var frames = RecommendedFrames(target.Row.Title);
                var changed = await Task.Run(() =>
                {
                    GameScanner.RequireModifiable(game.Root, token);
                    if (unlockerPackage is not null)
                    {
                        var package = ModPackage.Read(unlockerPackage.Directory, target.Proxy, token);
                        return ManagedMods(frames).Install(game, exe, target.FrameGeneration!, package,
                            unlockerPackage.Tag, unlockerPackage.Tag, newerOnly: true, token) is not null;
                    }
                    return opti.Install(game, exe, optiPackage!, target.Proxy, token, target.Journal) != target.Journal;
                }, token);
                if (!changed) return;
                result.Updated++;
                await RefreshGameInventoryAsync(target.Row.Inventory!, exe);
            });
        }
    }

    private static IEnumerable<Installation> GameTargets(LibraryRow row)
    {
        var game = row.Inventory!;
        return new[] { row.Executable, game.Executable }.Concat(game.Alternatives)
            .OfType<string>().Where(exe => Path.IsPathFullyQualified(exe) &&
                exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && SafePaths.Contains(row.Path, exe) && File.Exists(exe))
            .DistinctBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
            .Select(exe => new Installation(row.Path, [Path.GetRelativePath(row.Path, exe)]));
    }

    private static bool NewerRelease(string current, string? candidate) =>
        DllVersionService.IsUpgrade(current.TrimStart('v', 'V'), candidate?.TrimStart('v', 'V'));

    private static async Task TryAutoUpdateAsync(string label, AutoUpdateResult result, CancellationToken token, Func<Task> action)
    {
        try { await action(); }
        catch (GameRunningException) { result.Deferred++; }
        catch (Exception e) when (IsAutoUpdateError(e) || e is OperationCanceledException && !token.IsCancellationRequested)
        { result.Errors.Add($"{label}: {e.Message}"); }
    }

    private static bool IsAutoUpdateError(Exception e) =>
        e is IOException or UnauthorizedAccessException or HttpRequestException or InvalidDataException or
            JsonException or ArgumentException or AggregateException or System.ComponentModel.Win32Exception;

    private sealed record ManagedUpdate(LibraryRow Row, Installation Game, string Tag, string Proxy,
        string? FrameGeneration, Guid? Journal = null);

    private sealed class AutoUpdateResult
    {
        public int Updated { get; set; }
        public int Deferred { get; set; }
        public List<string> Errors { get; } = [];
    }
}
