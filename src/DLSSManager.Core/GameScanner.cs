using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSSManager.Core;

public sealed record ScanRoot(string Title, string Path, string Launcher, string? Executable = null, string? KnownAntiCheat = null);
public sealed record ScanOptions(IReadOnlyList<ScanRoot> Roots, IReadOnlyList<string> Exclusions,
    IReadOnlyList<string> BlockedExecutables);
public sealed record ScanProgress(int RootsCompleted, int RootsTotal, int Directories, int Files, string Current);
public sealed record DllFinding(string Path, string Kind, bool ModComponent, long Size, DateTime ModifiedUtc,
    DllMetadata? Metadata, bool HasOriginalBackup, long BackupSize = 0, DateTime? BackupDate = null)
{
    public string? Version => DllVersionService.Parse(Metadata?.FileVersion)?.ToString() ?? Metadata?.FileVersion;
    public string Status => Metadata switch
    {
        null => "Nicht lesbar",
        { IsDll: false } => "Keine PE-DLL",
        _ when Kind == "Proxy" => "Proxy-Kandidat",
        _ when ModComponent => "Unlocker-Kandidat",
        _ => "PE-DLL"
    };
}
public sealed record ScanGame(ScanRoot Root, string? Executable, string[] Alternatives, DllFinding[] Dlls,
    string[]? AntiCheat = null, bool OptiScalerPresent = false);
public sealed record ScanMetrics(double WallMs, double CpuMs, long ManagedBytesDelta, long WorkingSetDelta,
    int HandleDelta, int Directories, int Files, int MetadataReads, int CacheHits);
public sealed record ScanResult(ScanGame[] Games, ScanMetrics Metrics, string[] Warnings);

public static partial class LauncherDiscovery
{
    // Valve KeyValues quoted strings, including the escaped paths used by libraryfolders.vdf.
    [GeneratedRegex("\"((?:\\\\.|[^\"\\\\])*)\"|[{}]|//[^\\r\\n]*")]
    private static partial Regex Tokens();
    private static IEnumerable<(string Key, string Value)> Pairs(string path)
    {
        var tokens = Tokens().Matches(File.ReadAllText(path)).Where(m => !m.Value.StartsWith("//")).ToArray();
        for (var i = 0; i + 1 < tokens.Length; i++)
            if (tokens[i].Groups[1].Success && tokens[i + 1].Groups[1].Success)
            {
                var key = tokens[i].Groups[1].Value;
                var value = tokens[++i].Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
                yield return (key, value);
            }
    }

    public static IEnumerable<ScanRoot> Steam(string steamRoot, Action<string> warning, CancellationToken token)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        try
        {
            var vdf = SafePaths.Child(SafePaths.Root(steamRoot), "steamapps/libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var (key, value) in Pairs(vdf))
                    if ((key == "path" || int.TryParse(key, out _)) && Path.IsPathFullyQualified(value))
                        libraries.Add(value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { warning($"Steam-Bibliotheken: {e.Message}"); }

        foreach (var library in libraries)
        {
            token.ThrowIfCancellationRequested();
            foreach (var manifest in Manifests(library, "steamapps", "appmanifest_*.acf", warning))
            {
                token.ThrowIfCancellationRequested();
                if (Path.GetFileNameWithoutExtension(manifest).Equals("appmanifest_228980", StringComparison.OrdinalIgnoreCase)) continue;
                ScanRoot? result = null;
                try
                {
                    SafePaths.RejectLinks(manifest);
                    var entries = Pairs(manifest).ToLookup(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
                    var folder = entries["installdir"].FirstOrDefault();
                    var title = entries["name"].FirstOrDefault() ?? folder;
                    var type = entries["type"].FirstOrDefault();
                    if (folder is not null && Path.GetFileName(folder) == folder && !IsSteamRuntimeFolder(folder) &&
                        !IsKnownLauncherTool(title, folder) && !NonGameTypes.Contains(type ?? ""))
                        result = new(title!,
                            SafePaths.Child(SafePaths.Root(library), "steamapps/common/" + folder), "Steam",
                            KnownAntiCheat: Path.GetFileNameWithoutExtension(manifest).Equals("appmanifest_730", StringComparison.OrdinalIgnoreCase)
                                ? "VAC" : null);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                { warning($"Steam-Manifest: {e.Message}"); }
                if (result is not null) yield return result;
            }
        }
    }

    private static readonly HashSet<string> NonGameTypes = new(StringComparer.OrdinalIgnoreCase)
        { "tool", "application", "software", "sdk", "dedicatedserver" };

    private static readonly HashSet<string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wallpaper Engine", "wallpaper_engine", "Borderless Gaming", "BorderlessGaming",
        "Lossless Scaling", "LosslessScaling", "OBS Studio", "OBSStudio", "Source Filmmaker",
        "Source SDK", "SteamVR", "Steamworks Common Redistributables", "Steamworks Shared",
        "Proton Experimental", "Steam Linux Runtime"
    };

    public static bool IsKnownLauncherTool(string? title, string folder) =>
        (title is not null && KnownTools.Contains(title.Trim())) || KnownTools.Contains(folder.Trim());

    internal static bool IsSteamRuntimeFolder(string name) => name.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<ScanRoot> Epic(string manifestDirectory, Action<string> warning, CancellationToken token)
    {
        foreach (var path in Manifests(manifestDirectory, null, "*.item", warning))
        {
            token.ThrowIfCancellationRequested();
            ScanRoot? result = null;
            try
            {
                SafePaths.RejectLinks(path);
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                string? Get(string key) => json.RootElement.TryGetProperty(key, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Get("InstallLocation") is { } root && Path.IsPathFullyQualified(root) &&
                    !(json.RootElement.TryGetProperty("bIsIncompleteInstall", out var incomplete) &&
                      incomplete.ValueKind == JsonValueKind.True))
                    result = new(Get("DisplayName") ?? Path.GetFileName(root), root, "Epic", Get("LaunchExecutable"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { warning($"Epic-Manifest: {e.Message}"); }
            if (result is not null) yield return result;
        }
    }

    private static string[] Manifests(string root, string? relative, string pattern, Action<string> warning)
    {
        try
        {
            var dir = SafePaths.Root(root);
            if (relative is not null) dir = SafePaths.Child(dir, relative);
            if (!Directory.Exists(dir)) { warning($"Launcher-Ordner fehlt: {dir}"); return []; }
            return Directory.GetFiles(dir, pattern);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { warning(e.Message); return []; }
    }
}

public sealed class GameScanner(string cacheDirectory)
{
    private sealed record CacheEntry(long Size, DateTime ModifiedUtc, DateTime CreatedUtc, DllMetadata Metadata);

    public ScanResult Scan(ScanOptions options, IProgress<ScanProgress>? progress = null, CancellationToken token = default)
    {
        using var process = Process.GetCurrentProcess();
        var watch = Stopwatch.StartNew();
        var cpu = process.TotalProcessorTime;
        var memory = GC.GetTotalMemory(false);
        var workingSet = process.WorkingSet64;
        var handles = process.HandleCount;
        var warnings = new List<string>();
        var games = new List<ScanGame>();
        var dirs = 0;
        var files = 0;
        var reads = 0;
        var hits = 0;
        var completed = 0;
        var lastProgress = -100L;
        var cacheRoot = SafePaths.Root(cacheDirectory);
        Directory.CreateDirectory(cacheRoot);
        var cachePath = SafePaths.Child(cacheRoot, "scan-cache-v4.json");
        Dictionary<string, CacheEntry> old = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(cachePath))
                old = new(JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(cachePath))!,
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { warnings.Add($"Scan-Cache wird neu aufgebaut: {e.Message}"); }
        var cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        var blocked = options.BlockedExecutables.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exclusions = options.Exclusions.Where(Path.IsPathFullyQualified).Select(Path.GetFullPath).ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in options.Roots)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var root = SafePaths.Root(candidate.Path);
                if (!seen.Add(root) || LauncherDiscovery.IsSteamRuntimeFolder(Path.GetFileName(root)) ||
                    exclusions.Any(path => SafePaths.Contains(path, root))) continue;
                if (!Directory.Exists(root)) { warnings.Add($"Offline oder nicht zugänglich: {root}"); continue; }
                var executable = new List<(string Path, long Size)>();
                var dlls = new List<DllFinding>();
                var antiCheat = new List<(string Folder, string Name)>();
                var optiScaler = new List<string>();
                var pending = new Stack<string>();
                pending.Push(root);
                while (pending.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (exclusions.Any(x => SafePaths.Contains(x, folder))) continue;
                    var modComponent = Path.GetRelativePath(root, folder).Split(Path.DirectorySeparatorChar)
                        .Contains("dlssg_sm86", StringComparer.OrdinalIgnoreCase);
                    dirs++;
                    Report(folder);
                    try
                    {
                        SafePaths.RejectLinks(folder);
                        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                        {
                            token.ThrowIfCancellationRequested();
                            if (AntiCheatName(item.Name) is { } antiCheatName) antiCheat.Add((folder, antiCheatName));
                            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (item is DirectoryInfo)
                            {
                                if (!LauncherDiscovery.IsSteamRuntimeFolder(item.Name)) pending.Push(item.FullName);
                                continue;
                            }
                            files++;
                            if (item is not FileInfo file) continue;
                            if (file.Name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase) ||
                                (DllDetectionService.IsProxy(file.Name) &&
                                    File.Exists(Path.Combine(folder, "OptiScaler.ini")) &&
                                    (System.Diagnostics.FileVersionInfo.GetVersionInfo(file.FullName).ProductName ?? "")
                                        .Contains("OptiScaler", StringComparison.OrdinalIgnoreCase))) optiScaler.Add(folder);
                            if (DllDetectionService.Classify(file.Name, modComponent) is { } kind)
                            {
                                var size = file.Length;
                                var modified = file.LastWriteTimeUtc;
                                var created = file.CreationTimeUtc;
                                DllMetadata? metadata = null;
                                if (old.TryGetValue(file.FullName, out var entry) && entry?.Metadata is not null &&
                                    entry.Size == size && entry.ModifiedUtc == modified && entry.CreatedUtc == created)
                                {
                                    hits++;
                                    metadata = entry.Metadata;
                                }
                                else
                                {
                                    reads++;
                                    try { metadata = DllDetectionService.Inspect(file.FullName, token); }
                                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                                    { warnings.Add($"{file.FullName}: {e.Message}"); }
                                }
                                if (metadata is not null) cache[file.FullName] = new(size, modified, created, metadata);
                                var backup = new FileInfo(file.FullName + ".bak");
                                var hasBackup = !modComponent && DllDetectionService.UpscalerKind(file.Name) is "SR" or "FG" or "RR" or "XeSS" or "FSR" &&
                                    backup.Exists && !backup.Attributes.HasFlag(FileAttributes.ReparsePoint);
                                dlls.Add(new(file.FullName, kind, modComponent, size, modified, metadata, hasBackup,
                                    hasBackup ? backup.Length : 0, hasBackup ? backup.LastWriteTimeUtc : null));
                            }
                            else if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) &&
                                     !blocked.Contains(file.Name))
                                executable.Add((file.FullName, file.Length));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    { warnings.Add($"{folder}: {e.Message}"); }
                }
                string? confirmed = null;
                if (!string.IsNullOrEmpty(candidate.Executable))
                {
                    try
                    {
                        var path = SafePaths.Child(root, candidate.Executable.Replace('\\', '/'));
                        if (executable.Any(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) confirmed = path;
                    }
                    catch (IOException e) { warnings.Add(e.Message); }
                }
                var dllDirs = dlls.Where(d => !d.ModComponent && d.Kind is "SR" or "FG" or "RR").Select(d => Path.GetDirectoryName(d.Path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var alternatives = executable
                    .Where(e => e.Path != confirmed)
                    .OrderByDescending(e => dllDirs.Contains(Path.GetDirectoryName(e.Path)))
                    .ThenByDescending(e => e.Size)
                    .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).Select(e => e.Path).ToArray();
                if (candidate.Launcher == "Laufwerk")
                {
                    // Partition drive scans by the nearest ancestor with an executable.
                    var exeFolders = executable.ToLookup(e => Path.GetDirectoryName(e.Path)!, StringComparer.OrdinalIgnoreCase);
                    var groups = dlls.Where(d => !d.ModComponent && d.Kind is "SR" or "FG" or "RR" or "FSR" or "XeSS")
                        .GroupBy(d => FindGameFolder(d.Path), StringComparer.OrdinalIgnoreCase);
                    foreach (var group in groups)
                    {
                        if (group.Key is not { } gameFolder) continue;
                        var gameDlls = dlls.Where(d => SafePaths.Contains(gameFolder, d.Path)).ToArray();
                        games.Add(new(new(Path.GetFileName(gameFolder), gameFolder, "Lokal"), null,
                            exeFolders[gameFolder].OrderByDescending(e => e.Size).Select(e => e.Path).ToArray(),
                            gameDlls, AntiCheatAt(gameFolder), optiScaler.Any(p => SafePaths.Contains(gameFolder, p))));
                    }
                    string? FindGameFolder(string path)
                    {
                        for (var dir = Path.GetDirectoryName(path); dir is not null && dir != root; dir = Path.GetDirectoryName(dir))
                            if (exeFolders.Contains(dir)) return dir;
                        return null;
                    }
                }
                else if (candidate.Launcher != "Manuell" || dlls.Any(d => d.Kind is "SR" or "FG" or "RR" or "FSR" or "XeSS"))
                {
                    var protection = AntiCheatAt(root);
                    if (candidate.KnownAntiCheat is { } known && !protection.Contains(known)) protection = [.. protection, known];
                    games.Add(new(candidate with { Path = root }, confirmed, alternatives, [.. dlls], protection, optiScaler.Count != 0));
                }
                string[] AntiCheatAt(string path) => antiCheat.Where(item => SafePaths.Contains(path, item.Folder))
                    .Select(item => item.Name).Distinct().ToArray();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add(e.Message); }
            finally { completed++; }
        }
        token.ThrowIfCancellationRequested();
        Report("", force: true);
        foreach (var entry in old)
            cache.TryAdd(entry.Key, entry.Value);
        try { DurableFiles.Json(cachePath, cache, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { warnings.Add($"Scan-Cache konnte nicht gespeichert werden: {e.Message}"); }
        process.Refresh();
        return new([.. games], new(watch.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpu).TotalMilliseconds,
            GC.GetTotalMemory(false) - memory, process.WorkingSet64 - workingSet, process.HandleCount - handles,
            dirs, files, reads, hits), [.. warnings]);

        void Report(string current, bool force = false)
        {
            if (force || watch.ElapsedMilliseconds - lastProgress >= 100)
            {
                lastProgress = watch.ElapsedMilliseconds;
                progress?.Report(new(completed, options.Roots.Count, dirs, files, current));
            }
        }
    }

    public static void RequireModifiable(string path, CancellationToken token, IReadOnlyCollection<string>? acknowledged = null)
    {
        var root = SafePaths.Root(path);
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
        {
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(folder);
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if (AntiCheatName(item.Name) is { } name && acknowledged?.Contains(name) != true)
                    throw new IOException($"{name} erkannt. DLL-Updates und Mod-Installationen sind gesperrt.");
                if (item is DirectoryInfo && !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    folders.Push(item.FullName);
            }
        }
    }

    private static string? AntiCheatName(string name)
    {
        if (name.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)) return "Easy Anti-Cheat";
        if (name.Equals("BattlEye", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("BEService", StringComparison.OrdinalIgnoreCase)) return "BattlEye";
        if (name.Equals("Vanguard", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vgk.sys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("vgc.exe", StringComparison.OrdinalIgnoreCase)) return "Vanguard";
        if (name.Equals("ACE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ACE-BASE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("AntiCheatExpert", StringComparison.OrdinalIgnoreCase)) return "ACE";
        return null;
    }
}
