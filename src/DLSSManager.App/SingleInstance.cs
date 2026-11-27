using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DLSSManager.App;

internal sealed class SingleInstance : IDisposable
{
    private static readonly IntPtr MessageOnlyWindow = new(-3);
    private static readonly uint ActivateMessage = RegisterWindowMessage("DLSSManager.CSharp.Activate");
    private readonly string name;
    private readonly Mutex? mutex;
    private HwndSource? receiver;

    public bool IsPrimary { get; }

    public SingleInstance(string userSid)
    {
        name = $"DLSSManager.CSharp.{userSid}";
        try
        {
            mutex = new Mutex(false, @"Local\" + name, out var created);
            IsPrimary = created;
            if (!created) mutex.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated instance can own a mutex the unelevated launcher cannot open.
        }
    }

    public void Listen(Action activate)
    {
        receiver = new HwndSource(new HwndSourceParameters(name)
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0
        });
        receiver.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == ActivateMessage)
            {
                handled = true;
                activate();
            }
            return IntPtr.Zero;
        });
        // Accept only this payload-free activation message across elevation levels.
        ChangeWindowMessageFilterEx(receiver.Handle, ActivateMessage, 1, IntPtr.Zero);
    }

    public bool ActivatePrimary()
    {
        var wait = Stopwatch.StartNew();
        do
        {
            var handle = FindWindowEx(MessageOnlyWindow, IntPtr.Zero, null, name);
            if (handle != IntPtr.Zero)
            {
                GetWindowThreadProcessId(handle, out var processId);
                if (processId != 0) AllowSetForegroundWindow(processId);
                if (PostMessage(handle, ActivateMessage, IntPtr.Zero, IntPtr.Zero)) return true;
            }
            Thread.Sleep(50);
        } while (wait.Elapsed < TimeSpan.FromSeconds(5));
        return false;
    }

    public void Dispose()
    {
        receiver?.Dispose();
        if (IsPrimary) mutex?.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
