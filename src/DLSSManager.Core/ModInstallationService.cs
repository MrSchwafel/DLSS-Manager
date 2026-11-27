using static DLSSManager.Core.LocalizationService;
using System.Reflection.PortableExecutable;

namespace DLSSManager.Core;

public sealed record ModPackageFile(string RelativePath, string Source, string Sha256);
public sealed record ModPackage(string Proxy, IReadOnlyList<ModPackageFile> Files)
{
    public static string[] ReadProxies(string source)
    {
        var root = SafePaths.Root(source);
        var alternatives = SafePaths.Child(root, "alternatives");
        var files = Directory.Exists(alternatives) ? Directory.GetFiles(alternatives, "*.dll") : [];
        var defaultProxy = SafePaths.Child(root, "version.dll");
        return files.Append(defaultProxy)
            .Where(p => (SafePaths.Attributes(p) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
            .Select(Path.GetFileName).OfType<string>().Where(DllDetectionService.IsProxy)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ModPackage Read(string source, string proxy, CancellationToken token = default, bool backupLayout = false)
    {
        if (!DllDetectionService.IsProxy(proxy)) throw new IOException(GetText("ModInstallationService.UnknownSM86Proxy"));
        var root = SafePaths.Root(source);
        var mod = SafePaths.Child(root, "dlssg_sm86");
        var files = new List<ModPackageFile>();
        Add(proxy, SafePaths.Child(root, backupLayout || proxy.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
            ? proxy : "alternatives/" + proxy));
        Add("dlssg_sm86.ini", SafePaths.Child(root, "dlssg_sm86.ini"));

        var pending = new Stack<string>();
        pending.Push(mod);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            SafePaths.RejectLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = SafePaths.Attributes(path) ?? throw new IOException(GetText("ModInstallationService.AnSM86PackageFileIsMissing"));
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException(GetText("ModInstallationService.TheSM86PackageContainsALink"));
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(path);
                else if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    Add(Path.GetRelativePath(root, path), path);
            }
        }

        var paths = files.Select(f => f.RelativePath.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!paths.IsSupersetOf(["dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/kernels/fg_kernels.dll",
                "dlssg_sm86/runtime/nvngx_dlssg.dll"]))
            throw new IOException(GetText("ModInstallationService.TheSM86PackageIsMissingRequiredFG"));
        return new(proxy, files);

        void Add(string relative, string path)
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = DllDetectionService.Inspect(path, token);
                if (!metadata.IsDll || metadata.Machine != Machine.Amd64)
                    throw new IOException(GetText("ModInstallationService.SM86PackageFileIsNotAnX64", relative));
                files.Add(new(relative, path, metadata.Sha256));
            }
            else
                files.Add(new(relative, path, DurableFiles.Hash(path, token) ??
                    throw new IOException(GetText("ModInstallationService.SM86PackageFileMissing", relative))));
        }
    }
}

public sealed class ModInstallationService(FileTransactions transactions, ModIniProfiles? profiles = null)
{
    public Guid? Install(Installation game, string executable, string nativeFrameGenerationDll,
        ModPackage package, ModPackage? previous = null, CancellationToken token = default, ModRevision? revision = null,
        bool recordUnchanged = true)
    {
        var root = SafePaths.Root(game.Root);
        var frameGeneration = SafePaths.Child(root, nativeFrameGenerationDll);
        if (!Path.GetFileName(frameGeneration).Equals("nvngx_dlssg.dll", StringComparison.OrdinalIgnoreCase) ||
            nativeFrameGenerationDll.Split('/', '\\').Contains("dlssg_sm86", StringComparer.OrdinalIgnoreCase) ||
            DllDetectionService.Inspect(frameGeneration, token) is not { IsDll: true, Machine: Machine.Amd64 })
            throw new IOException(GetText("ModInstallationService.ANativeX64FrameGenerationDLLIs"));

        return Apply(game, executable, package, previous, token, revision, recordUnchanged);
    }

    public Guid? Uninstall(Installation game, string executable, ModPackage installedPackage,
        CancellationToken token = default, ModRevision? revision = null) =>
        Apply(game, executable, null, installedPackage, token, revision);

    private Guid? Apply(Installation game, string executable, ModPackage? desired, ModPackage? previous,
        CancellationToken token, ModRevision? revision, bool recordUnchanged = true)
    {
        var root = SafePaths.Root(game.Root);
        var exe = SafePaths.Child(root, executable);
        if (!game.Executables.Any(p => StringComparer.OrdinalIgnoreCase.Equals(SafePaths.Child(root, p), exe)))
            throw new IOException(GetText("Ini.TheTargetExecutableIsOutsideTheVerified"));
        var target = Path.GetDirectoryName(exe)!;
        var oldFiles = Index(previous);
        var newFiles = Index(desired);
        var plan = new List<FileChange>();

        const string iniName = "dlssg_sm86.ini";
        var sourceIni = newFiles.GetValueOrDefault(iniName);
        using var merged = profiles is not null && sourceIni is not null
            ? profiles.Stage(profiles.Merge(IniMod.Sm86, root, ModIniService.ReadVerified(sourceIni.Source, sourceIni.Sha256)))
            : null;
        if (merged is not null) newFiles[iniName] = new(iniName, merged.Path, merged.Hash);

        foreach (var key in oldFiles.Keys.Union(newFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var full = SafePaths.Child(target, key);
            var current = DurableFiles.Hash(full, token);
            var next = newFiles.GetValueOrDefault(key);
            var isIni = key.Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase);
            var ownsIni = isIni && (merged is not null || (profiles is not null && revision?.Predecessor is not null));
            if (!ownsIni && current is not null && current != oldFiles.GetValueOrDefault(key)?.Sha256)
            {
                if (isIni || (next is null && desired is null)) continue;
                throw new IOException(GetText("ModInstallationService.AnExternalOrModifiedFileIsBlocking", key));
            }
            if (current == next?.Sha256) continue;
            // Keep an existing INI on update so custom values and comments survive.
            if (isIni && !ownsIni && current is not null && next is not null) continue;
            plan.Add(new(Path.GetRelativePath(root, full), current, next?.Source, next?.Sha256));
        }

        return plan.Count == 0 && (revision is null || !recordUnchanged) ? null : transactions.Apply(game, plan, token, createTargetDirectories: true, mod: revision);
    }

    private static Dictionary<string, ModPackageFile> Index(ModPackage? package)
    {
        var result = new Dictionary<string, ModPackageFile>(StringComparer.OrdinalIgnoreCase);
        if (package is null) return result;
        if (!DllDetectionService.IsProxy(package.Proxy)) throw new IOException(GetText("ModInstallationService.UnknownSM86Proxy"));
        foreach (var file in package.Files)
        {
            var relative = file.RelativePath.Replace('\\', '/');
            if (!(relative.StartsWith("dlssg_sm86/", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("dlssg_sm86.ini", StringComparison.OrdinalIgnoreCase) ||
                relative.Equals(package.Proxy, StringComparison.OrdinalIgnoreCase)) ||
                file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                !result.TryAdd(relative, file with { Sha256 = file.Sha256.ToUpperInvariant() }))
                throw new IOException(GetText("ModInstallationService.InvalidSM86PackagePathOrHash", relative));
        }
        return result;
    }
}
