using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DLSSManager.Core;

public static class WindowsLauncherDiscovery
{
    [SupportedOSPlatform("windows")]
    public static IEnumerable<ScanRoot> Installed(Action<string> warning, CancellationToken token)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var (keyPath, launcher, pathKey, titleKey, exeKey) in new[]
            {
                (@"SOFTWARE\GOG.com\Games", "GOG", "path", "gameName", "exe"),
                (@"SOFTWARE\Ubisoft\Launcher\Installs", "Ubisoft Connect", "InstallDir", "DisplayName", "Executable")
            })
            {
                using var parent = machine.OpenSubKey(keyPath);
                foreach (var id in parent?.GetSubKeyNames() ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    using var game = parent?.OpenSubKey(id);
                    if (game?.GetValue(pathKey) is not string path || !Path.IsPathFullyQualified(path)) continue;
                    var executable = game.GetValue(exeKey) as string;
                    if (executable is not null && Path.IsPathFullyQualified(executable))
                        executable = Path.GetRelativePath(path, executable);
                    yield return new(game.GetValue(titleKey) as string ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
                        path, launcher, executable);
                }
            }
        }

        foreach (var drive in GameDrives())
        {
            token.ThrowIfCancellationRequested();
            var marker = Path.Combine(drive, ".GamingRoot");
            var xbox = Path.Combine(drive, "XboxGames");
            try
            {
                if (File.Exists(marker) && new FileInfo(marker).Length is >= 10 and <= 4096)
                {
                    var bytes = File.ReadAllBytes(marker);
                    if (bytes.AsSpan(0, 4).SequenceEqual("RGBX"u8))
                    {
                        var relative = System.Text.Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8).Trim('\0', '\\');
                        if (relative.Length > 0) xbox = SafePaths.Child(drive, relative);
                    }
                }
            }
            catch (IOException e) { warning(e.Message); }
            foreach (var game in XboxGames(xbox, warning, token)) yield return game;
        }
    }

    public static bool IsXboxSaveEntry(string path) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(path)).Equals("GameSave", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<ScanRoot> XboxGames(string library, Action<string> warning, CancellationToken token)
    {
        foreach (var game in Directories(library, warning))
        {
            token.ThrowIfCancellationRequested();
            if (IsXboxSaveEntry(game)) continue;
            var content = Path.Combine(game, "Content");
            var root = Directory.Exists(content) ? content : game;
            var executable = XboxExecutable(root, warning);
            if (executable is not null) yield return new(Path.GetFileName(game), root, "Xbox / Game Pass", executable);
        }
    }

    public static string[] GameDrives() => DriveInfo.GetDrives()
        .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
        .Select(d => d.RootDirectory.FullName).ToArray();

    public static string[] Directories(string path, Action<string> warning)
    {
        try
        {
            if (!Directory.Exists(path)) return [];
            SafePaths.RejectLinks(path);
            return Directory.GetDirectories(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warning(e.Message);
            return [];
        }
    }

    private static string? XboxExecutable(string root, Action<string> warning)
    {
        try
        {
            var config = SafePaths.Child(SafePaths.Root(root), "MicrosoftGame.config");
            if (!File.Exists(config)) return null;
            return XDocument.Load(config).Descendants()
                .FirstOrDefault(x => x.Name.LocalName == "Executable")?.Attribute("Name")?.Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            warning(e.Message);
            return null;
        }
    }
}
