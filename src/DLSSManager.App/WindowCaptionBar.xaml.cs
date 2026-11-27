using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace DLSSManager.App;

public partial class WindowCaptionBar : UserControl
{
    private const int DwmBorderColor = 34;
    private const int DarkBorderColorRef = 0x00362C26;

    public WindowCaptionBar() => InitializeComponent();

    public static readonly DependencyProperty UseCustomCaptionProperty = DependencyProperty.RegisterAttached(
        "UseCustomCaption", typeof(bool), typeof(WindowCaptionBar), new PropertyMetadata(false, OnUseCustomCaptionChanged));

    public static bool GetUseCustomCaption(Window window) => (bool)window.GetValue(UseCustomCaptionProperty);

    public static void SetUseCustomCaption(Window window, bool value) => window.SetValue(UseCustomCaptionProperty, value);

    private static void OnUseCustomCaptionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var window = (Window)target;
        if ((bool)args.NewValue)
        {
            window.SourceInitialized += InitializeWindowFrame;
            window.StateChanged += RestoreIfMaximized;
        }
        else
        {
            window.SourceInitialized -= InitializeWindowFrame;
            window.StateChanged -= RestoreIfMaximized;
        }
    }

    private static void InitializeWindowFrame(object? sender, EventArgs args)
    {
        var handle = new WindowInteropHelper((Window)sender!).Handle;
        HwndSource.FromHwnd(handle)!.AddHook(BlockMaximize);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            DwmSetWindowAttribute(handle, DwmBorderColor, DarkBorderColorRef, sizeof(int));
    }

    private static void RestoreIfMaximized(object? sender, EventArgs args)
    {
        if (sender is Window { WindowState: WindowState.Maximized } window)
            window.WindowState = WindowState.Normal;
    }

    private static IntPtr BlockMaximize(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmNcLeftButtonDoubleClick = 0x00A3;
        const int wmSysCommand = 0x0112;
        const int hitCaption = 2;
        const int sysCommandMask = 0xFFF0;
        const int scMaximize = 0xF030;

        var parameter = wParam.ToInt64();
        handled = (message == wmNcLeftButtonDoubleClick && parameter == hitCaption) ||
                  (message == wmSysCommand && (parameter & sysCommandMask) == scMaximize);
        return IntPtr.Zero;
    }

    private void MinimizeWindow(object sender, RoutedEventArgs args)
    {
        if (Window.GetWindow(this) is { } window)
            SystemCommands.MinimizeWindow(window);
    }

    private void CloseWindow(object sender, RoutedEventArgs args) => Window.GetWindow(this)?.Close();

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, in int value, int size);
}
