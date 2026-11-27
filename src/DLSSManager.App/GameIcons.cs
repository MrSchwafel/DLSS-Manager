using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using DLSSManager.Core;

namespace DLSSManager.App;

internal static class GameIcons
{
    private static readonly ConcurrentDictionary<(string Path, DateTime Modified), ImageSource> Images = new();

    public static ImageSource? Resolve(string? custom, string? executable, ScanGame? game)
    {
        var icon = Read(custom) ?? Read(executable) ?? Read(game?.Executable);
        if (icon is not null || game is null) return icon;
        foreach (var path in game.Alternatives.Where(IsGameCandidate).Take(8))
            if (Read(path) is { } candidate) return candidate;
        return null;
    }

    private static bool IsGameCandidate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return !name.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("setup", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("crash", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("protected_game", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("anticheat", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase);
    }

    private static ImageSource? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        var key = (path, File.GetLastWriteTimeUtc(path));
        if (Images.TryGetValue(key, out var cached)) return cached;
        var image = LibraryRow.ReadIcon(path);
        return image is null ? null : Images.GetOrAdd(key, image);
    }
}

public sealed class ScanGameIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ScanGame game ? GameIcons.Resolve(null, null, game) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
