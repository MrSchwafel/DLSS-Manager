using System.Text.RegularExpressions;

namespace DLSSManager.Core;

public static partial class GameExecutableSelection
{
    private static readonly Dictionary<string, string> PreferredExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sniper Elite: Resistance"] = "SniperResistance_dx12.exe",
        ["The Last of Us Part II Remastered"] = "tlou-ii.exe",
        ["Assassins Creed III Remastered"] = "ACIII.exe",
        ["Assassins Creed 1"] = "AssassinsCreed_Game.exe"
    };

    public static string? FromScan(ScanGame game)
    {
        if (game.Executable is { Length: > 0 } confirmed) return confirmed;
        var candidates = game.Alternatives.Where(IsCandidate).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var titleKey = game.Root.Title.Replace("™", "").Replace("®", "").Replace("'", "").Replace("’", "");
        if (PreferredExecutables.TryGetValue(titleKey, out var preferred))
        {
            var preferredPaths = candidates.Where(path => Path.GetFileName(path).Equals(preferred, StringComparison.OrdinalIgnoreCase)).ToArray();
            var rootMatch = preferredPaths.FirstOrDefault(path => string.Equals(Path.GetDirectoryName(path), game.Root.Path, StringComparison.OrdinalIgnoreCase));
            if (rootMatch is not null) return rootMatch;
            if (preferredPaths.Length == 1) return preferredPaths[0];
        }

        var names = candidates.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        candidates = candidates.Where(path =>
        {
            var name = Path.GetFileNameWithoutExtension(path);
            return !(name.StartsWith("Play", StringComparison.OrdinalIgnoreCase) && names.Contains(name[4..])) &&
                !names.Contains(name + "Game") &&
                !(name.EndsWith("_plus", StringComparison.OrdinalIgnoreCase) && names.Contains(name[..^5]));
        }).ToArray();
        if (candidates.Length == 1) return candidates[0];

        // Unreal packages put the runtime under Binaries/Win64 and bootstrap executables elsewhere.
        var runtimes = candidates.Where(IsWin64Runtime).Take(2).ToArray();
        if (runtimes.Length == 1 && candidates.Where(path => !IsWin64Runtime(path)).All(path =>
                Path.GetFileNameWithoutExtension(path).StartsWith("Play", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals(Path.GetFileName(runtimes[0]), StringComparison.OrdinalIgnoreCase)))
            return runtimes[0];

        var title = game.Root.Title.Split(':', 2)[0];
        var words = TitleWord().Matches(title).Select(match => match.Value).ToArray();
        if (words.Length == 0) return null;
        var fullName = string.Concat(words);
        var initials = string.Concat(words.Select(word =>
            word.All(char.IsAsciiDigit) || word is "II" or "III" or "IV" or "VI" or "VII" or "VIII" or "IX"
                ? word : word[..1]));
        var matches = candidates.Where(path =>
        {
            var name = string.Concat(TitleWord().Matches(Path.GetFileNameWithoutExtension(path)).Select(match => match.Value));
            return name.Equals(fullName, StringComparison.OrdinalIgnoreCase) ||
                (initials.Length >= 3 && name.Equals(initials, StringComparison.OrdinalIgnoreCase));
        }).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool IsWin64Runtime(string path)
    {
        var parts = path.Split('\\', '/');
        return parts.Length >= 3 && parts[^3].Equals("Binaries", StringComparison.OrdinalIgnoreCase) &&
            parts[^2].Equals("Win64", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCandidate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var folders = path.Split('\\', '/');
        return !name.EndsWith("_helper", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith("-helper", StringComparison.OrdinalIgnoreCase) &&
            !folders.SkipLast(1).Any(folder => SupportFolder().IsMatch(folder)) &&
            !SupportExecutable().IsMatch(name);
    }

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex TitleWord();

    [GeneratedRegex(@"^(?:Redistributables|_CommonRedist|DevTools|configtool|Installer_?Resources|Support|Detection|Register)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportFolder();

    [GeneratedRegex(@"setup|redist|unins|crash|launcher|anticheat|protected_game|encoder|benchmark|config(?:uration)?tool|^crs-|^send-presence$|^dedicatedserver$|^uplaybrowser$|^ffmpeg$|^7za$|^gdf(?:_rem|install)?$|^gefirewall(?:_rem)?$|^visconfig$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SupportExecutable();
}
