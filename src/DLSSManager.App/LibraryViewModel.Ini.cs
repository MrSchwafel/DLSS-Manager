using static DLSSManager.Core.LocalizationService;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DLSSManager.Core;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    public ICommand EditGlobalIni { get; private set; } = null!;
    public ICommand EditUnlockerIni { get; private set; } = null!;
    public ICommand EditOptiIni { get; private set; } = null!;
    public ICommand ApplyUnlockerIni { get; private set; } = null!;
    public ICommand ApplyOptiIni { get; private set; } = null!;
    public string UnlockerProfileStatus => unlockerProfileStatus.Text;
    public string OptiProfileStatus => optiProfileStatus.Text;
    private LocalizedText unlockerProfileStatus = Message("Ini.ModDefault");
    private LocalizedText optiProfileStatus = Message("Ini.ModDefault");

    private ModIniProfiles IniProfiles(int? recommendedFrames = null) =>
        new(Path.Combine(storage, "Mod-INI"), recommendedFrames);

    private void InitializeIniCommands()
    {
        EditGlobalIni = new Command(() => RunAsync(t => OpenIniEditorAsync(IniMod.Sm86, null, null, null, t)), () => !IsBusy);
        EditUnlockerIni = new Command(() => EditLibraryIni(IniMod.Sm86), HasGame);
        EditOptiIni = new Command(() => EditLibraryIni(IniMod.OptiScaler), HasGame);
        ApplyUnlockerIni = new Command(() => ApplyLibraryIni(IniMod.Sm86), () =>
            CanChangeLibraryUnlocker() && (UnlockerInstalled || SelectedLibraryGame?.Inventory?.Dlls.Any(d => d.ModComponent && d.Kind == "FG") == true));
        ApplyOptiIni = new Command(() => ApplyLibraryIni(IniMod.OptiScaler), () =>
            CanChangeLibraryUnlocker() && (optiInstalledProxy is not null || SelectedLibraryGame?.Inventory?.OptiScalerPresent == true));
    }

    private void RefreshIniStatus()
    {
        try
        {
            var row = SelectedLibraryGame;
            var profiles = IniProfiles();
            unlockerProfileStatus = Label(IniMod.Sm86);
            optiProfileStatus = Label(IniMod.OptiScaler);

            LocalizedText Label(IniMod mod) =>
                row is not null && profiles.Read(mod, row.Path).HasOverrides ? Message("Ini.CustomGameProfile") :
                profiles.Read(mod).HasOverrides ? Message("Ini.GlobalDefault") :
                mod == IniMod.Sm86 && RecommendedFrames(row?.Title) is int frames ? Message("Ini.DLSSdbRecommendationActiveX", frames + 1) :
                Message("Ini.ModDefault");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            unlockerProfileStatus = optiProfileStatus = Message("Ini.CouldNotReadINIProfile");
            Notes = [.. Notes, e.Message];
        }
        Notify(nameof(UnlockerProfileStatus));
        Notify(nameof(OptiProfileStatus));
    }

    private int? RecommendedFrames(string? title) => title is null ? null : rtx?.Find(title)?.RecommendedFrames;

    private async Task<int> ApplyDatabaseFramesAsync(CancellationToken token)
    {
        var targets = allGames.Where(row => !row.Ignored && RecommendedFrames(row.Title) is not null)
            .Select(row => (Row: row, Target: IniTarget(row), Frames: RecommendedFrames(row.Title)!.Value))
            .Where(entry => entry.Target is not null).ToArray();
        var profiles = IniProfiles();
        var global = profiles.Read(IniMod.Sm86);
        if (OverridesFrames(global)) return 0;
        var service = ManagedMods();
        var transactions = CreateTransactions();
        var result = await Task.Run(() =>
        {
            var changed = 0;
            var warnings = new List<string>();
            foreach (var (row, target, frames) in targets)
            {
                token.ThrowIfCancellationRequested();
                var (game, exe, scan) = target!.Value;
                if (scan.AntiCheat is { Length: > 0 }) continue;
                try
                {
                    if (service.History(game, exe).FirstOrDefault()?.Mod?.Version is null ||
                        OverridesFrames(profiles.Read(IniMod.Sm86, game.Root))) continue;
                    var current = ModIniService.Read(game, exe);
                    var values = ModIniService.ReadValues(current);
                    var engine = values.LastOrDefault(v => v.Matches("FrameGeneration", "Engine"))?.Value;
                    var limit = string.Equals(engine, "XeSS", StringComparison.OrdinalIgnoreCase) ? Math.Min(3, frames) : frames;
                    var value = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (values.LastOrDefault(v => v.Matches("FrameGeneration", "MaxGeneratedFrames"))?.Value == value)
                        continue;
                    GameScanner.RequireModifiable(game.Root, token);
                    if (ModIniService.Save(transactions, game, exe, current,
                        [new("FrameGeneration", "MaxGeneratedFrames", value)], Path.Combine(storage, "ini-work"), token) is not null)
                        changed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or AggregateException or JsonException or ArgumentException)
                { warnings.Add($"{row.Title} · DLSSdb-MFG: {e.Message}"); }
            }
            return (changed, warnings);
        }, token);
        if (result.warnings.Count > 0) Notes = [.. Notes, .. result.warnings.Select(message => (LocalizedText)message)];
        return result.changed;

        static bool OverridesFrames(IniProfile profile) => profile.Values.Concat(profile.Expert)
            .Any(value => value.Matches("FrameGeneration", "MaxGeneratedFrames"));
    }

    private Task EditLibraryIni(IniMod mod)
    {
        var row = SelectedLibraryGame!;
        var target = IniTarget(row);
        return RunAsync(t => OpenIniEditorAsync(mod, row.Title, row.Path, target, t));
    }

    private (Installation Game, string Exe, ScanGame Scan)? IniTarget(LibraryRow row)
    {
        if (row.Inventory is not { } scan || ExecutableFor(scan) is null) return null;
        var game = InstallationFor(scan);
        return (game, game.Executables[0], scan);
    }

    private Task EditModIniAsync(CancellationToken token)
    {
        var (game, exe) = SelectedModTarget();
        var finding = SelectedFinding!;
        var title = allGames.FirstOrDefault(row => SamePath(row.Path, game.Root))?.Title ?? finding.Root.Title;
        return OpenIniEditorAsync(IniMod.Sm86, title, game.Root, (game, exe, finding), token);
    }

    private async Task OpenIniEditorAsync(IniMod initialTab, string? title, string? gameRoot,
        (Installation Game, string Exe, ScanGame Scan)? target, CancellationToken token)
    {
        var frames = RecommendedFrames(title);
        var profiles = IniProfiles(frames);
        var mods = Enum.GetValues<IniMod>();
        var originals = mods.ToDictionary(mod => mod, mod => ReadIniBase(mod, target, profiles));
        var global = mods.ToDictionary(mod => mod, mod => profiles.Read(mod));
        var local = mods.ToDictionary(mod => mod, mod => gameRoot is null ? IniProfile.Empty : profiles.Read(mod, gameRoot));
        var iniPaths = new Dictionary<IniMod, string>();
        if (target is { } destination)
        {
            var directory = Path.GetDirectoryName(SafePaths.Child(destination.Game.Root, destination.Exe))!;
            foreach (var mod in mods)
                iniPaths.Add(mod, SafePaths.Child(directory, ModIniProfiles.FileName(mod)));
        }
        var editor = new ModIniWindow(title, initialTab, originals, global, local, frames, iniPaths) { Owner = Application.Current.MainWindow };
        if (editor.ShowDialog() != true) return;
        if (editor.Changes.Count == 0)
        {
            Toast = Status = Message("Ini.NoINIChanges");
            return;
        }

        await Task.Run(() =>
        {
            foreach (var (mod, profile) in editor.Changes)
                profiles.Save(mod, gameRoot, profile);
        }, token);
        RefreshIniStatus();
        Toast = Status = gameRoot is null ? Message("Ini.GlobalINIDefaultsSaved") : Message("Ini.GameINIProfileSaved");

        if (target is not { } selected) return;
        var installed = editor.Changes.Keys.Where(mod => IsIniModInstalled(mod, selected)).ToArray();
        if (installed.Length == 0 || !ConfirmAntiCheat(selected.Scan)) return;
        var errors = new List<string>();
        foreach (var mod in installed)
        {
            try { await ApplyIniAsync(mod, selected, originals[mod], profiles, token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or AggregateException or ArgumentException)
            { errors.Add($"{ModIniProfiles.FileName(mod)} · {e.Message}"); }
        }
        Toast = Status = errors.Count == 0 ? Message("Ini.GameINIProfilesSavedAndApplied") :
            Message("Ini.GameINIProfilesSavedCouldNotApply") + string.Join(" · ", errors);
    }

    private Task ApplyLibraryIni(IniMod mod)
    {
        var row = SelectedLibraryGame!;
        return RunAsync(async token =>
        {
            if (IniTarget(row) is not { } target || !IsIniModInstalled(mod, target)) return;
            if (!ConfirmAntiCheat(target.Scan)) return;
            var profiles = IniProfiles(RecommendedFrames(row.Title));
            await ApplyIniAsync(mod, target, ReadIniBase(mod, target, profiles), profiles, token);
            Toast = Status = Message("Ini.Restored", ModIniProfiles.FileName(mod));
        });
    }

    private Task ApplyIniAsync(IniMod mod, (Installation Game, string Exe, ScanGame Scan) target, byte[] original,
        ModIniProfiles profiles, CancellationToken token) => Task.Run(() =>
    {
        GameScanner.RequireModifiable(target.Game.Root, token, target.Scan.AntiCheat);
        return ModIniService.Restore(CreateTransactions(), target.Game, target.Exe, mod, original, profiles,
            Path.Combine(storage, "ini-work"), token);
    }, token);

    private byte[] ReadIniBase(IniMod mod, (Installation Game, string Exe, ScanGame Scan)? target, ModIniProfiles profiles)
    {
        if (mod == IniMod.Sm86)
        {
            if (target is { } game && ManagedMods().InstalledIniBase(game.Game, game.Exe) is { } installed)
                return installed;
            if (modPackageDirectory is { } directory)
            {
                var path = SafePaths.Child(directory, ModIniProfiles.FileName(mod));
                if (File.Exists(path)) return ModIniService.ReadFile(path);
            }
        }
        else
        {
            if (target is { } game && InstalledOpti(game.Game, game.Exe) is { IniBase: { } hash })
                return profiles.ReadOptiBase(hash);
            if ((SelectedOptiPackage ?? OptiPackages.FirstOrDefault())?.LocalPath is { } package)
            {
                var path = SafePaths.Child(SafePaths.Root(package + ".files"), ModIniProfiles.FileName(mod));
                if (File.Exists(path)) return ModIniService.ReadFile(path);
            }
        }
        return ModIniProfiles.Defaults(mod);
    }

    private bool IsIniModInstalled(IniMod mod, (Installation Game, string Exe, ScanGame Scan) target)
    {
        var directory = Path.GetDirectoryName(SafePaths.Child(target.Game.Root, target.Exe))!;
        if (mod == IniMod.Sm86)
            return ManagedMods().History(target.Game, target.Exe).FirstOrDefault()?.Mod?.Version is not null ||
                (Directory.Exists(SafePaths.Child(directory, "dlssg_sm86")) &&
                 OptiProxies.Concat(["dbghelp.dll", "dinput8.dll"]).Any(proxy => File.Exists(SafePaths.Child(directory, proxy))));
        // Scan inventory may report OptiScaler for a different executable within the same game.
        return InstalledOpti(target.Game, target.Exe) is not null ||
            target.Scan.Dlls.Any(d => d.Kind.Equals("OptiScaler", StringComparison.OrdinalIgnoreCase) &&
                SamePath(Path.GetDirectoryName(d.Path)!, directory)) ||
            (target.Scan.OptiScalerPresent && File.Exists(SafePaths.Child(directory, "OptiScaler.ini")) &&
             OptiProxies.Any(proxy => File.Exists(SafePaths.Child(directory, proxy))));
    }

    private TransactionJournal? InstalledOpti(Installation game, string exe)
    {
        var service = new OptiScalerService(CreateTransactions());
        var journal = service.Installed(game.Root);
        return journal is not null && service.InstalledProxy(journal) is { } proxy &&
            SamePath(Path.GetDirectoryName(SafePaths.Child(game.Root, proxy))!,
                Path.GetDirectoryName(SafePaths.Child(game.Root, exe))!) ? journal : null;
    }
}
