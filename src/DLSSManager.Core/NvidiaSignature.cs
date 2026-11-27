using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DLSSManager.Core;

internal static class NvidiaSignature
{
    private static readonly Guid VerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr Handle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallback, SipClient;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, UrlReference;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);

    public static bool IsValid(string path, string kind)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = path };
        var address = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(file, address, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), File = address, UiChoice = 2,
                UnionChoice = 1, StateAction = 1, ProviderFlags = 0x1000
            };
            try
            {
                if (WinVerifyTrust(new(-1), in VerifyAction, ref data) != 0) return false;
                // The certificate loader accepts certificates, not signed PE images.
#pragma warning disable SYSLIB0057
                using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                using var signer = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                var publisher = signer.GetNameInfo(X509NameType.SimpleName, false);
                return kind switch
                {
                    "XeSS" => publisher.Equals("Intel Corporation", StringComparison.OrdinalIgnoreCase),
                    "FSR" => publisher.Equals("Advanced Micro Devices, Inc.", StringComparison.OrdinalIgnoreCase) ||
                        publisher.Equals("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase),
                    _ => publisher.Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase)
                };
            }
            catch (CryptographicException) { return false; }
            finally
            {
                data.StateAction = 2;
                WinVerifyTrust(new(-1), in VerifyAction, ref data);
            }
        }
        finally
        {
            Marshal.DestroyStructure<TrustFile>(address);
            Marshal.FreeHGlobal(address);
        }
    }
}
