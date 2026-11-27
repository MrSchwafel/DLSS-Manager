using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace DLSSManager.Core;

public sealed record DllMetadata(string? FileVersion, string? ProductVersion, string Sha256,
    Machine? Machine, bool IsDll, Version? NumericFileVersion, bool OfficialSignature = false);

public static class DllDetectionService
{
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = "SR", ["nvngx_dlssg.dll"] = "FG", ["nvngx_dlssd.dll"] = "RR",
        ["libxess.dll"] = "XeSS", ["amd_fidelityfx_dx12.dll"] = "FSR", ["OptiScaler.dll"] = "OptiScaler"
    };

    private static readonly HashSet<string> Proxies = new(StringComparer.OrdinalIgnoreCase)
    {
        "version.dll", "d3d12.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "winmm.dll", "winhttp.dll", "xinput1_3.dll"
    };

    private static readonly HashSet<string> ModBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "sm86_backend.dll", "fg_kernels.dll", "sr_kernels.dll", "xefg_kernels.dll"
    };

    internal static bool IsProxy(string fileName) => Proxies.Contains(fileName);
    public static string? UpscalerKind(string fileName) => NativeNames.GetValueOrDefault(fileName);

    internal static string? Classify(string name, bool modComponent)
    {
        if (NativeNames.TryGetValue(name, out var kind)) return kind;
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            if (name.StartsWith("libxess", StringComparison.OrdinalIgnoreCase)) return "XeSS";
            if (name.StartsWith("amd_fidelityfx", StringComparison.OrdinalIgnoreCase)) return "FSR";
        }
        if (modComponent && ModBinaries.Contains(name)) return "SM86";
        return Proxies.Contains(name) ? "Proxy" : null;
    }

    public static DllMetadata Inspect(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        SafePaths.RejectLinks(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Machine? machine = null;
        var isDll = false;
        using (var pe = new PEReader(stream, PEStreamOptions.LeaveOpen))
        {
            try
            {
                var headers = pe.PEHeaders;
                if (headers.PEHeader is not null)
                {
                    machine = headers.CoffHeader.Machine;
                    isDll = headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll);
                }
            }
            catch (BadImageFormatException) { }
        }

        var version = machine is not null ? FileVersionInfo.GetVersionInfo(path) : null;
        var hash = DurableFiles.Hash(stream, token);
        var numericVersion = version?.FileVersion is not null
            ? new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart)
            : null;
        var kind = UpscalerKind(Path.GetFileName(path));
        var official = isDll && kind is not null && kind != "OptiScaler" && NvidiaSignature.IsValid(path, kind);
        return new(version?.FileVersion, version?.ProductVersion, hash, machine, isDll, numericVersion, official);
    }
}
