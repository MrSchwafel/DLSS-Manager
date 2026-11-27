using static DLSSManager.Core.LocalizationService;
using System.Security.Cryptography;
using System.Text.Json;

namespace DLSSManager.Core;

public static class SafePaths
{
    public static string Root(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectLinks(root);
        return root;
    }

    public static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string Child(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Split('/', '\\').Any(p => p.Length == 0 || p is "." or ".." ||
                p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                p.Contains(':') || IsDevice(p)))
            throw new IOException(GetText("SafePaths.InvalidRelativePath", relative));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!Contains(root, path)) throw new IOException(GetText("SafePaths.ThePathIsOutsideTheRootFolder"));
        RejectLinks(path);
        return path;
    }

    private static bool IsDevice(string name)
    {
        var stem = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
             "0123456789¹²³".Contains(stem[3]));
    }

    public static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Attributes(current) is { } flags && flags.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(GetText("SafePaths.LinksOrReparsePointsAreNotAllowed", current));
    }
}

internal static class DurableFiles
{
    public static string? Hash(string path, CancellationToken token = default)
    {
        SafePaths.RejectLinks(path);
        if (SafePaths.Attributes(path) is null) return null;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Hash(stream, token);
    }

    internal static string Hash(Stream stream, CancellationToken token, HashAlgorithmName? algorithm = null)
    {
        token.ThrowIfCancellationRequested();
        stream.Position = 0;
        if (algorithm is null && !token.CanBeCanceled) return Convert.ToHexString(SHA256.HashData(stream));
        using var hash = IncrementalHash.CreateHash(algorithm ?? HashAlgorithmName.SHA256);
        Pump(stream, (buffer, count) => hash.AppendData(buffer, 0, count), token);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void Copy(string source, string target, CancellationToken token = default)
    {
        SafePaths.RejectLinks(source);
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        Create(target, output => Pump(input, (buffer, count) => output.Write(buffer, 0, count), token));
    }

    private static void Pump(Stream input, Action<byte[], int> consume, CancellationToken token)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                consume(buffer, count);
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static void Json<T>(string path, T value, bool overwrite = false)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Create(temp, stream => JsonSerializer.Serialize(stream, value, Options));
            SafePaths.RejectLinks(path);
            File.Move(temp, path, overwrite);
        }
        finally { File.Delete(temp); }
    }

    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static void Create(string path, Action<FileStream> write)
    {
        SafePaths.RejectLinks(path);
        using var stream = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        write(stream);
        stream.Flush(flushToDisk: true);
    }
}
