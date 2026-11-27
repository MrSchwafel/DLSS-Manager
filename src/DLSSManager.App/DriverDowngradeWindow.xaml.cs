using static DLSSManager.Core.LocalizationService;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DLSSManager.Core;

namespace DLSSManager.App;

public partial class DriverDowngradeWindow : Window
{
    private readonly bool silentInstallEnabled;
    private readonly string? dduPath;
    private readonly string driverCacheFolder;

    public bool CleanInstall => CleanInstallCheckBox.IsChecked == true;

    public DriverDowngradeWindow(
        string currentVersion,
        DriverUpdateInfo targetDriver,
        bool silentInstallEnabled,
        string? dduPath,
        string driverCacheFolder)
    {
        InitializeComponent();
        this.silentInstallEnabled = silentInstallEnabled;
        this.dduPath = dduPath;
        this.driverCacheFolder = driverCacheFolder;

        var typeLabel = targetDriver.IsHotfix ? "Hotfix" : "WHQL";
        Title = GetText("Drivers.DowngradeNVIDIADriverToV", targetDriver.Version);
        TitleHeading.Text = GetText("Drivers.DowngradeNVIDIADriverToV", targetDriver.Version);
        VersionComparisonText.Text = GetText("Drivers.CurrentVTargetV", currentVersion, targetDriver.Version, typeLabel);
        PrimaryDduButton.Content = dduPath is not null ? GetText("Drivers.RunDDU") : GetText("Drivers.DownloadDDUWagnardsoft");

        UpdateModeHint(this, new RoutedEventArgs());
    }

    private void UpdateModeHint(object sender, RoutedEventArgs e)
    {
        if (SetupModeHint is null || CleanInstallCheckBox is null)
            return;

        var clean = CleanInstallCheckBox.IsChecked == true;
        if (silentInstallEnabled)
        {
            SetupModeHint.Text = clean
                ? GetText("Drivers.SilentExpressModeRunsWithSClean")
                : GetText("Drivers.SilentExpressModeRunsWithSNoreboot");
            SetupModeHint.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
        }
        else
        {
            SetupModeHint.Text = clean
                ? GetText("Drivers.InteractiveSetupInTheNVIDIAInstallerChoose")
                : GetText("Drivers.InteractiveSetupOpensTheStandardNVIDIAInstaller");
            SetupModeHint.Foreground = new SolidColorBrush(
                clean ? Color.FromRgb(0xFC, 0xD3, 0x4D) : Color.FromRgb(0x94, 0xA3, 0xB8));
        }
    }

    private void HandlePrimaryDduAction(object sender, RoutedEventArgs e)
    {
        HideActionError();
        try
        {
            if (dduPath is not null)
            {
                using var _ = Process.Start(new ProcessStartInfo(dduPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(dduPath)!
                });
            }
            else
            {
                using var _ = Process.Start(new ProcessStartInfo(NvidiaDriverService.WagnardsoftDduUrl)
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowActionError(GetText("Drivers.DDULaunchCancelledAdministratorPrivilegesRequired"));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            ShowActionError(GetText("Drivers.ActionFailed", ex.Message));
        }
    }

    private void OpenDownloadFolder(object sender, RoutedEventArgs e)
    {
        HideActionError();
        try
        {
            Directory.CreateDirectory(driverCacheFolder);
            using var _ = Process.Start(new ProcessStartInfo(driverCacheFolder)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowActionError(GetText("Drivers.CouldNotOpenTheDownloadFolder", ex.Message));
        }
    }

    private void ConfirmDowngrade(object sender, RoutedEventArgs e) => DialogResult = true;

    private void ShowActionError(string message)
    {
        ActionErrorText.Text = message;
        ActionErrorText.Visibility = Visibility.Visible;
    }

    private void HideActionError()
    {
        ActionErrorText.Text = "";
        ActionErrorText.Visibility = Visibility.Collapsed;
    }
}
