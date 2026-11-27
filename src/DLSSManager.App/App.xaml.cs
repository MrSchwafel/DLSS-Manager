using System.Windows;

namespace DLSSManager.App;

public partial class App : Application
{
    private SingleInstance? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (WindowsAutostart.TryConfigureFromCommandLine(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        var inTray = StartsInTray(e.Args);
        instance = new SingleInstance(WindowsAutostart.CurrentUserSid());
        if (!instance.IsPrimary)
        {
            Shutdown(inTray || instance.ActivatePrimary() ? 0 : 1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        instance.Listen(window.ShowFromTray);
        window.Start(inTray);
    }

    internal static bool StartsInTray(IEnumerable<string> args) =>
        args.Contains("--minimized", StringComparer.OrdinalIgnoreCase) ||
        args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
