using System.Globalization;
using System.Text.Json;

namespace DLSSManager.Core;

public static class LocalizationService
{
    public const string Auto = "auto";
    public const string German = "de";
    public const string English = "en";

    private static readonly Dictionary<string, (CultureInfo Culture, IReadOnlyDictionary<string, string> Strings)> Catalogs = new()
    {
        [English] = (CultureInfo.GetCultureInfo("en-US"), Load(English)),
        [German] = (CultureInfo.GetCultureInfo("de-DE"), Load(German))
    };
    public static string SystemLanguage { get; } = ResolveLanguage(null, CultureInfo.CurrentUICulture);
    private static string language = SystemLanguage;
    public static string Language => Volatile.Read(ref language);
    public static CultureInfo Culture => Catalogs[Language].Culture;
    public static event EventHandler? LanguageChanged;

    public static string NormalizePreference(string? preference)
    {
        var code = preference?.Trim().Split('-', 2)[0].ToLowerInvariant();
        return code is not null && Catalogs.ContainsKey(code) ? code : Auto;
    }

    public static string ResolveLanguage(string? preference, CultureInfo systemCulture)
    {
        var selected = NormalizePreference(preference);
        if (selected != Auto) return selected;
        var system = systemCulture.TwoLetterISOLanguageName;
        return Catalogs.ContainsKey(system) ? system : English;
    }

    public static void SetPreference(string? preference)
    {
        var normalized = NormalizePreference(preference);
        var next = normalized == Auto ? SystemLanguage : normalized;
        if (Interlocked.Exchange(ref language, next) != next)
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string GetText(string key, params object?[] arguments)
    {
        var active = Language;
        var (culture, strings) = Catalogs[active];
        return Format(strings, key, culture, arguments) ??
            (active == English ? null : Format(Catalogs[English].Strings, key, culture, arguments)) ?? key;
    }

    private static string? Format(IReadOnlyDictionary<string, string> catalog, string key, CultureInfo culture, object?[] arguments)
    {
        if (!catalog.TryGetValue(key, out var format) || string.IsNullOrWhiteSpace(format)) return null;
        try { return arguments.Length == 0 ? format : string.Format(culture, format, arguments); }
        catch (FormatException) { return null; }
    }

    private static IReadOnlyDictionary<string, string> Load(string code)
    {
        using var stream = typeof(LocalizationService).Assembly
            .GetManifestResourceStream($"DLSSManager.Core.Localization.Strings.{code}.json");
        return stream is null ? new Dictionary<string, string>() :
            JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
