using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSManager.Core;

public sealed record IniSetting(string Section, string Key, string Value)
{
    public bool Matches(string section, string key) => StringComparer.OrdinalIgnoreCase.Equals(Section, section) && StringComparer.OrdinalIgnoreCase.Equals(Key, key);
    public string ExpertLine => $"{Section}|{Key}={Value}";
}

public static partial class ModIniService
{
    [GeneratedRegex(@"^(?<prefix>[^=]+?\s*=\s*)(?<value>.*?)(?<suffix>\s+[;#].*|\s*)$")]
    private static partial Regex Assignment();

    public static IniSetting[] ParseExpert(string text)
    {
        var result = new List<IniSetting>();
        var lineNumber = 0;
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            var bar = line.IndexOf('|');
            var equals = line.IndexOf('=', Math.Max(0, bar + 1));
            if (bar < 1 || equals <= bar + 1)
                throw new ArgumentException($"Expertenzeile {lineNumber}. Erwartet wird Abschnitt|Schlüssel=Wert.");
            var setting = new IniSetting(line[..bar].Trim(), line[(bar + 1)..equals].Trim(), line[(equals + 1)..].Trim());
            Validate(setting);
            result.Add(setting);
        }
        return result.ToArray();
    }

    public static byte[] Read(Installation game, string executable, string fileName = "dlssg_sm86.ini") => ReadFile(Target(game, executable, fileName).Path);

    public static byte[] ReadFile(string path)
    {
        const int maxBytes = 2 * 1024 * 1024;
        SafePaths.RejectLinks(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new IOException("Die Mod-INI überschreitet das Größenlimit von 2 MiB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static byte[] ReadVerified(string path, string hash)
    {
        var original = ReadFile(path);
        if (Convert.ToHexString(SHA256.HashData(original)) != hash)
            throw new IOException("Die Mod-INI aus dem Paket wurde verändert.");
        return original;
    }

    public static IniSetting[] ReadValues(byte[] original) => ReadEntries(original).Select(e => e.Setting).ToArray();

    internal static (IniSetting Setting, string Description)[] ReadEntries(byte[] original)
    {
        var result = new List<(IniSetting, string)>();
        var comments = new List<string>();
        string? section = null;
        foreach (var line in Decode(original).Text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                var comment = trimmed[1..].Trim();
                if (comment.Length == 0 || comment.All(c => c == '-')) comments.Clear();
                else comments.Add(comment);
                continue;
            }
            if (Section(line) is { } name) section = name;
            else if (section is not null && Key(line) is { } key)
                result.Add((new(section, key, Assignment().Match(line).Groups["value"].Value.Trim()),
                    string.Join(Environment.NewLine, comments)));
            comments.Clear();
        }
        return result.ToArray();
    }

    [GeneratedRegex(@"[^\r\n]*(?:\r\n|\r|\n|$)")]
    private static partial Regex Lines();

    public static byte[] Apply(byte[] original, IEnumerable<IniSetting> settings)
    {
        var (text, encoding, bomLength) = Decode(original);
        var lines = Lines().Matches(text).Where(m => m.Length > 0).Select(m => m.Value).ToList();
        var newline = lines.Select(Ending).FirstOrDefault(e => e.Length != 0) ?? "\r\n";
        var trailing = lines.Count != 0 && Ending(lines[^1]).Length > 0;
        foreach (var setting in settings)
        {
            Validate(setting);
            var insertion = -1;
            var inSection = false;
            var found = false;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var body = line.TrimEnd('\r', '\n');
                if (Section(body) is { } section)
                {
                    inSection = Same(section, setting.Section);
                    if (inSection) insertion = i + 1;
                }
                if (!inSection) continue;
                var key = Key(body);
                if (key is not null) insertion = i + 1;
                if (!Same(key, setting.Key)) continue;
                var match = Assignment().Match(body);
                lines[i] = match.Groups["prefix"].Value + setting.Value + match.Groups["suffix"].Value + Ending(line);
                found = true;
            }
            if (found) continue;
            if (insertion < 0)
            {
                insertion = lines.Count;
                Insert(insertion++, $"[{setting.Section}]");
            }
            Insert(insertion, $"{setting.Key}={setting.Value}");
        }
        var changed = encoding.GetBytes(string.Concat(lines));
        return [.. original.AsSpan(0, bomLength), .. changed];

        void Insert(int index, string line)
        {
            if (index == lines.Count && !trailing)
            {
                if (index > 0 && Ending(lines[index - 1]).Length == 0) lines[index - 1] += newline;
                lines.Add(line);
            }
            else lines.Insert(index, line + newline);
        }
    }

    internal static byte[] NormalizeSm86(byte[] original)
    {
        var (text, encoding, bomLength) = Decode(original);
        var output = new StringBuilder(text.Length);
        string? section = null;
        foreach (Match match in Lines().Matches(text))
        {
            var body = match.Value.TrimEnd('\r', '\n');
            if (Section(body) is { } heading) section = heading;
            else if (section is not null && Key(body) is { } key && ModIniProfiles.IsRemovedSm86Key(section, key))
                continue;
            output.Append(match.Value);
        }
        var content = output.Length == text.Length ? original :
            [.. original.AsSpan(0, bomLength), .. encoding.GetBytes(output.ToString())];
        var replacements = ReadValues(content).Where(s => Same(s.Value, "auto"))
            .Select(s => s.Matches("DLSSG", "Preset") ? s with { Value = "A" } :
                s.Matches("Runtime", "Mode") ? s with { Value = "Bundled" } : null).OfType<IniSetting>().ToArray();
        return replacements.Length == 0 ? content : Apply(content, replacements);
    }

    public static Guid? Save(FileTransactions transactions, Installation game, string executable,
        byte[] original, IEnumerable<IniSetting> settings, string workDirectory, CancellationToken token = default, string fileName = "dlssg_sm86.ini", bool create = false)
    {
        var (root, path) = Target(game, executable, fileName);
        return Write(transactions, game, root, path, create ? null : original, Apply(original, settings), workDirectory, token);
    }

    public static Guid? Restore(FileTransactions transactions, Installation game, string executable, IniMod mod,
        byte[] original, ModIniProfiles profiles, string workDirectory, CancellationToken token)
    {
        var (root, path) = Target(game, executable, ModIniProfiles.FileName(mod));
        var current = File.Exists(path) ? ReadFile(path) : null;
        return Write(transactions, game, root, path, current, profiles.Merge(mod, root, original), workDirectory, token);
    }

    private static Guid? Write(FileTransactions transactions, Installation game, string root, string path,
        byte[]? original, byte[] content, string workDirectory, CancellationToken token)
    {
        if (original is not null && original.AsSpan().SequenceEqual(content)) return null;
        var before = original is null ? null : Convert.ToHexString(SHA256.HashData(original));
        token.ThrowIfCancellationRequested();
        using var staged = new IniPayload(SafePaths.Root(workDirectory), content);
        return transactions.Apply(game, [new(Path.GetRelativePath(root, path), before, staged.Path, staged.Hash)], token);
    }

    private static (string Root, string Path) Target(Installation game, string executable, string fileName)
    {
        var root = SafePaths.Root(game.Root);
        var exe = SafePaths.Child(root, executable);
        if (!game.Executables.Any(p => Same(SafePaths.Child(root, p), exe)))
            throw new IOException("Die Ziel-EXE gehört nicht zur bestätigten Installation.");
        return (root, SafePaths.Child(Path.GetDirectoryName(exe)!, fileName));
    }

    private static (string Text, Encoding Encoding, int BomLength) Decode(byte[] input)
    {
        Encoding[] candidates = [new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true),
            new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true)];
        foreach (var encoding in candidates)
        {
            var bom = encoding.GetPreamble();
            if (input.AsSpan().StartsWith(bom)) return (encoding.GetString(input.AsSpan(bom.Length)), encoding, bom.Length);
        }
        try { return (candidates[2].GetString(input), candidates[2], 0); }
        catch (DecoderFallbackException)
        {
            // Preserve the original legacy byte values and reject unmappable new characters.
            var encoding = Encoding.GetEncoding("iso-8859-1", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (encoding.GetString(input), encoding, 0);
        }
    }

    private static string Ending(string line) => line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" :
        line.EndsWith('\n') ? "\n" : line.EndsWith('\r') ? "\r" : "";

    private static string? Section(string line)
    {
        var text = line.Trim();
        var end = text.IndexOf(']');
        return text.StartsWith('[') && end > 1 ? text[1..end].Trim() : null;
    }

    private static string? Key(string line)
    {
        var text = line.TrimStart();
        var index = text.IndexOf('=');
        return index > 0 && text[0] is not (';' or '#' or '[') ? text[..index].TrimEnd() : null;
    }

    private static bool Same(string? left, string right) => StringComparer.OrdinalIgnoreCase.Equals(left, right);

    internal static void Validate(IniSetting setting)
    {
        if (setting is null || setting.Value is null || string.IsNullOrWhiteSpace(setting.Section) || string.IsNullOrWhiteSpace(setting.Key) ||
            setting.Section.Any(c => char.IsControl(c) || c is '[' or ']' or '|') ||
            setting.Key.Any(c => char.IsControl(c) || c is '=' or '[' or ']' or '|') ||
            setting.Key.TrimStart()[0] is ';' or '#' || setting.Value.Any(c => c is '\n' or '\r' or '\0'))
            throw new ArgumentException("Ungültiger INI-Abschnitt, Schlüssel oder Wert.");
    }
}
