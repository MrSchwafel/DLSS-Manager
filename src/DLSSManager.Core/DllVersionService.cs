using System.Globalization;

namespace DLSSManager.Core;

public static class DllVersionService
{
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Replace(',', '.').Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length > 4) return null;
        Span<int> numbers = stackalloc int[4];
        numbers.Clear();
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return null;
        return new(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static bool IsUpgrade(string? installed, string? candidate) =>
        Parse(installed) is { } current && Parse(candidate) is { } available && available > current;
}
