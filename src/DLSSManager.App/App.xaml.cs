using System.IO;
using System.Text.Json;
using System.Windows;
using DLSSManager.Core;

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

        try
        {
            var settings = new GameLibraryService(DataFolders.Active).ReadSettings();
            LocalizationService.SetPreference(settings.PreferredLanguage);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            // The workspace loader reports settings errors after the window has opened.
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
