using static DLSSManager.Core.LocalizationService;
using System.IO;
using System.Runtime.InteropServices;

namespace DLSSManager.App;

internal static class NvidiaProfiles
{
    private const uint Override = 0x10E41E01;
    private const uint Preset = 0x10E41DF3;
    private const int SettingNotFound = -160;
    private const int ExecutableNotFound = -166;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint Query(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionStart(out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionCall(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FindApplication(nint session,
        [MarshalAs(UnmanagedType.LPWStr)] string executable, out nint profile, ref ApplicationInfo application);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(nint session, nint profile, uint id, ref Setting value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(nint session, nint profile, ref Setting value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSetting(nint session, nint profile, uint id);

    // NVIDIA nvapi.h: NVDRS_APPLICATION_V4 and NVDRS_SETTING_V1, 4-byte packing.
    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 20492)]
    private struct ApplicationInfo
    {
        [FieldOffset(0)] public uint Version;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 12320)]
    private struct Setting
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4100)] public uint Id;
        [FieldOffset(4108)] public uint Location;
        [FieldOffset(8220)] public uint CurrentValue;
    }

    public static LocalizedText AccessStatus()
    {
        try { _ = Read("DLSSManager.App.exe"); }
        catch (IOException) { return LocalizedText.Message("NvidiaProfiles.NVIDIADRSUnavailable"); }
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
            ? LocalizedText.Message("NvidiaProfiles.NVIDIADRSReady") : LocalizedText.Message("NvidiaProfiles.NVIDIADRSAdminRequired");
    }

    public static uint? Read(string executable) => WithProfile(executable, (session, profile) =>
    {
        if (ReadSetting(session, profile, Override)?.CurrentValue != 1) return 0u;
        return ReadSetting(session, profile, Preset)?.CurrentValue ?? 0;
    });

    public static void Write(string executable, uint preset)
    {
        if (preset > 15 && preset != 0x00ffffff) throw new ArgumentOutOfRangeException(nameof(preset));
        if (WithProfile(executable, (session, profile) =>
        {
            if (preset == 0)
            {
                Reset(Override);
                Reset(Preset);
            }
            else
            {
                Set(Override, 1);
                Set(Preset, preset);
            }
            Require(Resolve<SessionCall>(0xfcbc7e14)(session));
            return preset;

            void Set(uint id, uint value)
            {
                var setting = EmptySetting();
                setting.Id = id;
                setting.CurrentValue = value;
                Require(Resolve<SetSetting>(0x577dd202)(session, profile, ref setting));
            }

            void Reset(uint id)
            {
                if (ReadSetting(session, profile, id) is not { Location: 0 }) return;
                var status = Resolve<DeleteSetting>(0xe4a26362)(session, profile, id);
                if (status != SettingNotFound) Require(status);
            }
        }) is null)
            throw new IOException(GetText("NvidiaProfiles.NoNVIDIAApplicationProfileWasFoundFor"));
    }

    private static uint? WithProfile(string executable, Func<nint, nint, uint> action)
    {
        try
        {
            Require(Resolve<DriverCall>(0x0150e828)());
            try
            {
                Require(Resolve<SessionStart>(0x0694d52e)(out var session));
                try
                {
                    Require(Resolve<SessionCall>(0x375dbd6b)(session));
                    var application = new ApplicationInfo { Version = 20492 | (4u << 16) };
                    var status = Resolve<FindApplication>(0xeee566b2)(session, executable, out var profile, ref application);
                    if (status == ExecutableNotFound) return null;
                    Require(status);
                    return action(session, profile);
                }
                finally { Resolve<SessionCall>(0xdad9cff8)(session); }
            }
            finally { Resolve<DriverCall>(0xd22bdd7e)(); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException(GetText("NvidiaProfiles.TheNVIDIADriverWithNVAPIIsUnavailable"), e);
        }
    }

    private static Setting? ReadSetting(nint session, nint profile, uint id)
    {
        var setting = EmptySetting();
        var status = Resolve<GetSetting>(0x73bf8338)(session, profile, id, ref setting);
        if (status == SettingNotFound) return null;
        Require(status);
        return setting;
    }

    private static Setting EmptySetting() => new() { Version = 12320 | (1u << 16) };

    private static T Resolve<T>(uint id) where T : Delegate
    {
        var fn = Query(id);
        if (fn == 0) throw new IOException(GetText("NvidiaProfiles.TheNVIDIADriverDoesNotSupportThe"));
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static void Require(int status)
    {
        if (status != 0) throw new IOException(GetText("NvidiaProfiles.NVIDIAProfileAccessFailedNVAPI", status));
    }
}
