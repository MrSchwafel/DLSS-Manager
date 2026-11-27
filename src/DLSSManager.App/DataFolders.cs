using System.IO;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

internal static class DataFolders
{
    private const string RegistryPath = @"Software\DLSS Manager";
    private static bool Portable => Environment.GetCommandLineArgs().Contains("--portable", StringComparer.OrdinalIgnoreCase);
    public static string Active
    {
        get
        {
            if (Portable) return Path.Combine(AppContext.BaseDirectory, "data");
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue("DataPath") is string path && Path.IsPathFullyQualified(path) ? path :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DLSS Manager");
        }
    }

    public static bool CanChoose => !Portable;

    public static string DriverDownloads => Portable
        ? Path.Combine(AppContext.BaseDirectory, "data", "downloads", "drivers")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DLSS-Manager", "downloads", "drivers");

    public static string AppCache => Portable
        ? Path.Combine(AppContext.BaseDirectory, "data", "cache")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DLSS-Manager", "cache");

    public static string HotfixFeedCachePath => Path.Combine(AppCache, "hotfix_feed_cache.json");
    public static string DriverTranslationCachePath => Path.Combine(AppCache, "driver_translations_cache.json");

    public static void Choose(string path)
    {
        Initialize(path);
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("DataPath", path);
    }

    public static string Transactions(string root) => Directory.Exists(Path.Combine(root, "transactions"))
        ? Path.Combine(root, "transactions") : Path.Combine(root, "Backups", "Transaktionen");

    public static void Initialize(string root)
    {
        root = SafePaths.Root(root);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(SafePaths.Child(root, "Katalog"));
        Directory.CreateDirectory(SafePaths.Child(root, "Backups"));
        Directory.CreateDirectory(SafePaths.Root(Transactions(root)));
    }
}
