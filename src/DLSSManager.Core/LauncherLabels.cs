namespace DLSSManager.Core;

public static class LauncherLabels
{
    public static string FromPath(string path)
    {
        var parts = path.Split('\\', '/').ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (parts.Contains("SteamLibrary") || parts.Contains("steamapps")) return "Steam";
        if (parts.Contains("Epic Games")) return "Epic";
        if (parts.Contains("Ubisoft") || parts.Contains("Ubisoft Game Launcher")) return "Ubisoft";
        if (parts.Contains("GOG Galaxy")) return "GOG";
        if (parts.Contains("XboxGames")) return "Xbox";
        return "Lokal";
    }
}
