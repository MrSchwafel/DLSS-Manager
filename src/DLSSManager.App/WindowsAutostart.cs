using static DLSSManager.Core.LocalizationService;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace DLSSManager.App;

internal static class WindowsAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "DLSSManager.CSharp";
    private const string ConfigureSwitch = "--configure-windows-autostart";
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int LogonTrigger = 9;
    private const int ExecAction = 0;
    private const int InteractiveToken = 3;
    private const int Highest = 1;
    private const int CreateOrUpdate = 6;

    internal readonly record struct State(bool Enabled, bool Minimized, bool Legacy = false);

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    public static State Read()
    {
        dynamic scheduler = Connect();
        var task = FindTask(scheduler.GetFolder("\\"), TaskName(CurrentUserSid()));
        if (task is not null && (bool)task.Enabled)
            return new(true, App.StartsInTray(TaskArguments(task)));

        using var legacy = Registry.CurrentUser.OpenSubKey(RunKey);
        return legacy?.GetValue(RunName) is string ? new(true, true, true) : new(false, true);
    }

    public static async Task SetAsync(State state)
    {
        var path = ExecutablePath();
        var sid = CurrentUserSid();
        var args = Environment.GetCommandLineArgs()[1..];
        var portable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(() => Set(state, path, sid, portable));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or COMException { HResult: AccessDenied })
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.ArgumentList.Add(ConfigureSwitch);
            start.ArgumentList.Add(sid);
            start.ArgumentList.Add(state.Enabled ? (state.Minimized ? "tray" : "window") : "off");
            using var helper = Process.Start(start) ?? throw new InvalidOperationException(GetText("WindowsAutostart.UACLaunchFailed"));
            await helper.WaitForExitAsync();
            if (helper.ExitCode == 2)
                throw new InvalidOperationException(GetText("WindowsAutostart.StartupMustBeConfiguredUnderTheSame"));
            if (helper.ExitCode != 0)
                throw new InvalidOperationException(GetText("WindowsAutostart.WindowsTaskSchedulerCouldNotSaveThe"));
        }
    }

    public static bool TryConfigureFromCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length < 3 || args[^3] != ConfigureSwitch) return false;
        if (args[^2] != CurrentUserSid())
        {
            exitCode = 2;
            return true;
        }

        try
        {
            var state = args[^1] switch
            {
                "tray" => new State(true, true),
                "window" => new State(true, false),
                "off" => new State(false, true),
                _ => throw new InvalidOperationException(GetText("WindowsAutostart.InvalidStartupSetting"))
            };
            Set(state, ExecutablePath(), args[^2], args[..^3].Contains("--portable", StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e) when (IsSettingsError(e))
        {
            exitCode = 1;
        }
        return true;
    }

    public static bool IsSettingsError(Exception e) => e is COMException or Win32Exception or
        UnauthorizedAccessException or SecurityException or IOException or InvalidOperationException;

    private static dynamic Connect()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        scheduler.Connect();
        return scheduler;
    }

    private static string ExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.Equals(Path.GetFileName(path), "DLSSManager.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(GetText("WindowsAutostart.StartupRequiresThePublishedExecutable"));
        return path!;
    }

    private static string TaskName(string sid) => $"DLSS Manager ({sid})";

    private static dynamic? FindTask(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (Exception e) when (e is FileNotFoundException or COMException { HResult: FileNotFound }) { return null; }
    }

    private static string[] TaskArguments(dynamic task)
    {
        string arguments = task.Definition.Actions.Item(1).Arguments ?? "";
        return arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Set(State state, string path, string sid, bool portable)
    {
        using var legacy = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        var name = TaskName(sid);
        var existing = FindTask(folder, name);
        if (state.Enabled)
        {
            if (existing is not null && (bool)existing.Enabled)
                portable = ((string[])TaskArguments(existing)).Contains("--portable", StringComparer.OrdinalIgnoreCase);
            else if (legacy?.GetValue(RunName) is string command)
                portable = command.TrimEnd().EndsWith(" --portable", StringComparison.OrdinalIgnoreCase);
            dynamic task = BuildTask(scheduler, path, sid, state.Minimized, portable);
            folder.RegisterTaskDefinition(name, task, CreateOrUpdate, sid, null, InteractiveToken, null);
        }
        else if (existing is not null)
        {
            folder.DeleteTask(name, 0);
        }

        // Keep the old Run entry until Task Scheduler confirms the change.
        legacy?.DeleteValue(RunName, throwOnMissingValue: false);
    }

    private static dynamic BuildTask(dynamic scheduler, string path, string sid, bool minimized, bool portable)
    {
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = GetText("WindowsAutostart.StartsDLSSManagerAtWindowsSignIn");
        task.Principal.UserId = sid;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = Highest;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";

        dynamic trigger = task.Triggers.Create(LogonTrigger);
        trigger.UserId = sid;
        dynamic action = task.Actions.Create(ExecAction);
        action.Path = path;
        action.WorkingDirectory = Path.GetDirectoryName(path);
        action.Arguments = (minimized, portable) switch
        {
            (true, true) => "--minimized --portable",
            (true, false) => "--minimized",
            (false, true) => "--portable",
            _ => ""
        };
        return task;
    }
}
