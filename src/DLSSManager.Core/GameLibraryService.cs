using System.Text.Json;

namespace DLSSManager.Core;

public sealed record GameLibraryEntry(Guid Id, string Title, string Path, string Executable, string Proxy,
    string? Icon = null, bool Ignored = false, string Launcher = "Lokal", string Arguments = "", bool Overlay = false);

public sealed record DllBatchTarget(string Kind, Version Version, string CatalogHash);
public sealed record PendingDriverDowngrade(
    string InstallerFileName,
    string TargetVersion,
    bool RetainInCacheAfterInstall = false);
public enum DriverTooltipStyle { Compact, Detailed }

public sealed record ManagerSettings(string[] ScanFolders, string DefaultProxy = "", DllBatchTarget[]? BatchTargets = null, DateTime? LastScan = null,
    bool ScanLaunchersOnStartup = true, bool ExcludeAntiCheatGames = true, bool AutoUpdateDlls = false, bool AutoUpdateUnlocker = false, bool AutoUpdateOptiScaler = false,
    int AutoUpdateIntervalMinutes = 60,
    bool SilentDriverInstall = false,
    bool DeleteDriverInstallerAfterInstall = true,
    PendingDriverDowngrade? PendingDowngrade = null,
    string? DduExecutablePath = null,
    bool TranslateDriverSummaries = false,
    string? GoogleTranslateApiKey = null,
    string? PreferredLanguage = null,
    string? CustomHotfixFeedUrl = HotfixFeedScanner.DefaultHotfixFeedUrl,
    string? GoogleSearchApiKey = null,
    string? GoogleSearchCx = null,
    string DriverTooltipStyle = nameof(global::DLSSManager.Core.DriverTooltipStyle.Compact));

public sealed class GameLibraryService(string storage)
{
    public ManagerSettings ReadSettings()
    {
        var path = SafePaths.Child(SafePaths.Root(storage), "settings-v1.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(path))
            ?? throw new IOException("Einstellungen konnten nicht gelesen werden.") : new([]);
    }

    public void SaveSettings(ManagerSettings settings)
    {
        var root = SafePaths.Root(storage);
        Directory.CreateDirectory(root);
        DurableFiles.Json(SafePaths.Child(root, "settings-v1.json"), settings, overwrite: true);
    }

    public ScanGame[] ReadInventories()
    {
        var path = SafePaths.Child(SafePaths.Root(storage), "inventories-v1.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ScanGame[]>(File.ReadAllText(path)) ?? [] : [];
    }

    public void SaveInventories(ScanGame[] games)
    {
        var root = SafePaths.Root(storage);
        Directory.CreateDirectory(root);
        DurableFiles.Json(SafePaths.Child(root, "inventories-v1.json"), games, overwrite: true);
    }

    public GameLibraryEntry[]? Read()
    {
        var path = SafePaths.Child(SafePaths.Root(storage), "library-v1.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<GameLibraryEntry[]>(File.ReadAllText(path))
            ?? throw new IOException("Spielebibliothek konnte nicht gelesen werden.") : null;
    }

    public void Save(GameLibraryEntry[] entries)
    {
        var root = SafePaths.Root(storage);
        Directory.CreateDirectory(root);
        DurableFiles.Json(SafePaths.Child(root, "library-v1.json"), entries, overwrite: true);
    }
}
