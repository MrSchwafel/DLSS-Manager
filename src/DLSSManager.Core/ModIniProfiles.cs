using System.Security.Cryptography;
using System.Text.Json;

namespace DLSSManager.Core;

public enum IniMod { Sm86, OptiScaler }

public sealed record IniProfile(IniSetting[] Values, IniSetting[] Expert)
{
    public static IniProfile Empty { get; } = new([], []);
    public bool HasOverrides => Values.Length != 0 || Expert.Length != 0;
}

public sealed class ModIniProfiles(string storage, int? recommendedFrames = null)
{
    public static string FileName(IniMod mod) => mod == IniMod.Sm86 ? "dlssg_sm86.ini" : "OptiScaler.ini";

    public static byte[] Defaults(IniMod mod)
    {
        using var stream = typeof(ModIniProfiles).Assembly.GetManifestResourceStream(
            "DLSSManager.Core.IniDefaults." + FileName(mod))!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    public IniProfile Read(IniMod mod, string? gameRoot = null)
    {
        var path = ProfilePath(mod, gameRoot);
        if (!File.Exists(path)) return IniProfile.Empty;
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var profile = JsonSerializer.Deserialize<IniProfile>(input);
        if (profile is not { Values: not null, Expert: not null })
            throw new IOException("Ungültiges INI-Profil.");
        Validate(mod, profile.Values.Concat(profile.Expert));
        return profile;
    }

    public void Save(IniMod mod, string? gameRoot, IniProfile profile)
    {
        Validate(mod, profile.Values.Concat(profile.Expert));
        var path = ProfilePath(mod, gameRoot);
        if (!profile.HasOverrides)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        DurableFiles.Json(path, profile, overwrite: true);
    }

    public byte[] Merge(IniMod mod, string gameRoot, byte[] original)
    {
        var global = Read(mod);
        var local = Read(mod, gameRoot);
        return Merge(mod, original, global, local, recommendedFrames);
    }

    public static byte[] Merge(IniMod mod, byte[] original, IniProfile global, IniProfile local, int? recommendedFrames = null)
    {
        var settings = global.Values.Concat(global.Expert).Concat(local.Values).Concat(local.Expert).ToArray();
        Validate(mod, settings);
        if (mod == IniMod.Sm86) original = ModIniService.NormalizeSm86(original);
        var merged = ModIniService.Apply(original, settings);
        if (mod == IniMod.Sm86 && recommendedFrames is >= 1 and <= 5 &&
            !settings.Any(v => v.Matches("FrameGeneration", "MaxGeneratedFrames")))
            merged = ModIniService.Apply(merged, [new("FrameGeneration", "MaxGeneratedFrames",
                recommendedFrames.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        if (mod == IniMod.Sm86)
        {
            var values = ModIniService.ReadValues(merged);
            var engine = values.LastOrDefault(v => v.Matches("FrameGeneration", "Engine"))?.Value;
            var frames = values.LastOrDefault(v => v.Matches("FrameGeneration", "MaxGeneratedFrames"))?.Value;
            if (string.Equals(engine, "XeSS", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(frames, out var count) && count > 3)
                merged = ModIniService.Apply(merged, [new("FrameGeneration", "MaxGeneratedFrames", "3")]);
        }
        return merged;
    }

    private static void Validate(IniMod mod, IEnumerable<IniSetting> settings)
    {
        foreach (var setting in settings)
        {
            ModIniService.Validate(setting);
            if (mod == IniMod.Sm86 &&
                (IsRemovedSm86Key(setting.Section, setting.Key) ||
                 (setting.Matches("DLSSG", "Preset") || setting.Matches("Runtime", "Mode")) &&
                 setting.Value.Equals("auto", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Veraltete SM86-Einstellung: {setting.ExpertLine}");
        }
    }

    internal static bool IsRemovedSm86Key(string section, string key) => (section.ToUpperInvariant(), key.ToUpperInvariant()) is
        ("FRAMEGENERATION", "OPTIMIZED") or ("COMPATIBILITY", "OPTIMIZEDKERNELS") or ("COMPATIBILITY", "PRESET") or ("XESSFG", "MODEL");

    private string ProfilePath(IniMod mod, string? gameRoot)
    {
        var scope = gameRoot is null ? "global" : Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(SafePaths.Root(gameRoot).ToUpperInvariant())));
        return SafePaths.Child(SafePaths.Root(storage), Path.Combine(scope, mod + ".json"));
    }

    internal IniPayload Stage(byte[] content) => new(SafePaths.Child(SafePaths.Root(storage), "work"), content);

    internal string RememberOptiBase(byte[] original)
    {
        var hash = Convert.ToHexString(SHA256.HashData(original));
        var directory = SafePaths.Child(SafePaths.Root(storage), "OptiScaler");
        Directory.CreateDirectory(directory);
        var path = SafePaths.Child(directory, hash + ".ini");
        if (File.Exists(path)) return hash;
        using var staged = Stage(original);
        try { File.Move(staged.Path, path); }
        catch (IOException) when (File.Exists(path)) { }
        return hash;
    }

    public byte[] ReadOptiBase(string hash)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new IOException("Ungültige OptiScaler-INI-Referenz.");
        var path = SafePaths.Child(SafePaths.Root(storage), Path.Combine("OptiScaler", hash + ".ini"));
        var original = ModIniService.ReadFile(path);
        if (Convert.ToHexString(SHA256.HashData(original)) != hash) throw new IOException("OptiScaler-INI-Basis wurde verändert.");
        return original;
    }
}

internal sealed class IniPayload : IDisposable
{
    public string Path { get; }
    public string Hash { get; }

    public IniPayload(string directory, byte[] content)
    {
        Directory.CreateDirectory(directory);
        Path = SafePaths.Child(directory, Guid.NewGuid().ToString("N") + ".ini");
        Hash = Convert.ToHexString(SHA256.HashData(content));
        try { File.WriteAllBytes(Path, content); }
        catch { File.Delete(Path); throw; }
    }

    public void Dispose() => File.Delete(Path);
}
