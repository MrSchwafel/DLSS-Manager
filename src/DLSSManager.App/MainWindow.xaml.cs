using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace DLSSManager.App;

public partial class MainWindow : Window
{
    private readonly LibraryViewModel model;
    private readonly Size preferredSize;
    private readonly Forms.NotifyIcon tray = new();
    private readonly TrayMenu trayMenu;
    private WindowsAutostart.State autostart = new(false, true);
    private bool shownInTray;
    private bool closed;
    private bool waitingForDriverTooltipSave;

    public MainWindow()
    {
        InitializeComponent();
        preferredSize = new Size(Width, Height);
        ApplyBrandImage();
        model = new LibraryViewModel();
        DataContext = model;
        using var resource = Application.GetResourceStream(new Uri("/Manager.ico", UriKind.Relative))!.Stream;
        using var sourceIcon = new System.Drawing.Icon(resource);
        tray.Icon = (System.Drawing.Icon)sourceIcon.Clone();
        tray.Text = "DLSS Manager";
        trayMenu = new TrayMenu(model,
            () => DispatchTrayAction(ShowFromTray),
            () => DispatchTrayAction(() => RunTrayActionAsync(model.ScanLibraryAsync, "Spielebibliothek")),
            () => DispatchTrayAction(() => RunTrayActionAsync(model.SyncCatalogAndDatabaseAsync, "Katalog und Datenbanken")),
            () => DispatchTrayAction(() => RunTrayActionAsync(model.CheckStableReleaseAsync, "App-Updates")),
            () => DispatchTrayAction(() =>
            {
                if (model.OpenDataFolder.CanExecute(null)) model.OpenDataFolder.Execute(null);
            }),
            () => DispatchTrayAction(() =>
            {
                ShowFromTray();
                SettingsTab.IsSelected = true;
            }),
            () => DispatchTrayAction(Close));
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) DispatchTrayAction(ShowFromTray);
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            tray.Visible = true;
            Hide();
            if (shownInTray) return;
            shownInTray = true;
            tray.ShowBalloonTip(2500, "DLSS Manager", "Über das Taskleistensymbol kannst du das Fenster wieder öffnen.",
                Forms.ToolTipIcon.Info);
        };
        Closing += async (_, args) =>
        {
            if (model.DriverTooltipStyleSave.IsCompleted)
                return;

            args.Cancel = true;
            if (waitingForDriverTooltipSave)
                return;

            waitingForDriverTooltipSave = true;
            while (!model.DriverTooltipStyleSave.IsCompleted)
                await model.DriverTooltipStyleSave;
            waitingForDriverTooltipSave = false;
            if (!closed)
                Close();
        };
        Closed += (_, _) =>
        {
            closed = true;
            tray.Visible = false;
            tray.ContextMenuStrip = null;
            var icon = tray.Icon;
            tray.Dispose();
            trayMenu.Dispose();
            icon?.Dispose();
            model.StopAutoUpdates();
            model.RequestCancel();
        };
    }

    internal void Start(bool inTray)
    {
        if (inTray)
        {
            ShowInTaskbar = false;
            shownInTray = true;
            tray.Visible = true;
            new WindowInteropHelper(this).EnsureHandle();
        }
        else
        {
            Show();
        }
        Dispatcher.InvokeAsync(InitializeAsync, System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // CenterScreen places the hidden HWND on the launch monitor before we read its DPI.
        var handle = new WindowInteropHelper(this).Handle;
        var workArea = Forms.Screen.FromHandle(handle).WorkingArea;
        var fromDevice = HwndSource.FromHwnd(handle)!.CompositionTarget!.TransformFromDevice;
        LockToWorkArea(new Rect(
            fromDevice.Transform(new Point(workArea.Left, workArea.Top)),
            fromDevice.Transform(new Point(workArea.Right, workArea.Bottom))));
        WindowStartupLocation = WindowStartupLocation.Manual;
        base.OnSourceInitialized(e);
    }

    private void LockToWorkArea(Rect workArea)
    {
        var scale = Math.Min(1, Math.Min(workArea.Width / preferredSize.Width, workArea.Height / preferredSize.Height));
        // Keep the ratio in layout units and let the window round to device pixels.
        var width = preferredSize.Width * scale;
        var height = preferredSize.Height * scale;
        Width = MinWidth = MaxWidth = width;
        Height = MinHeight = MaxHeight = height;
        Left = workArea.Left + (workArea.Width - width) / 2;
        Top = workArea.Top + (workArea.Height - height) / 2;
    }

    private void ApplyBrandImage()
    {
        using var resource = Application.GetResourceStream(new Uri("/Manager.ico", UriKind.Relative))!.Stream;
        var decoder = new System.Windows.Media.Imaging.IconBitmapDecoder(resource,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Resources["BrandImage"] = decoder.Frames.MaxBy(frame => frame.PixelWidth);
    }

    private void ResizeInspector(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged) return;
        const int mainMinWidth = 480;
        const int sideMinWidth = 330;
        const int gap = 14;
        var twoColumns = args.NewSize.Width >= mainMinWidth + gap + sideMinWidth;
        InspectorMainColumn.MinWidth = twoColumns ? mainMinWidth : 0;
        InspectorMainColumn.Width = new GridLength(twoColumns ? 1.45 : 1, GridUnitType.Star);
        InspectorColumnGap.Width = new GridLength(twoColumns ? gap : 0);
        InspectorSideColumn.MinWidth = twoColumns ? sideMinWidth : 0;
        InspectorSideColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(InspectorSideCards, twoColumns ? 2 : 3);
        Grid.SetColumn(InspectorSideCards, twoColumns ? 2 : 0);
        InspectorMainCards.Margin = new Thickness(0, 0, 0, twoColumns ? 0 : gap);
    }

    private void ResizeInspectorHeader(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged) return;
        var inline = args.NewSize.Width >= 520;
        Grid.SetRow(InspectorGlobalActions, inline ? 0 : 1);
        Grid.SetColumn(InspectorGlobalActions, inline ? 1 : 0);
        Grid.SetColumnSpan(InspectorGlobalActions, inline ? 1 : 2);
        InspectorGlobalActions.Margin = inline ? new Thickness(8, 0, 0, 0) : new Thickness(0, 6, 0, 0);
    }

    private void ResizeInspectorMods(object sender, SizeChangedEventArgs args)
    {
        if (!args.WidthChanged) return;
        const int cardWidth = 360;
        const int gap = 12;
        var twoColumns = args.NewSize.Width >= 2 * cardWidth + gap;
        InspectorModGap.Width = new GridLength(twoColumns ? gap : 0);
        InspectorModColumn.Width = twoColumns ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(OptiScalerCard, twoColumns ? 0 : 1);
        Grid.SetColumn(OptiScalerCard, twoColumns ? 2 : 0);
        UnlockerCard.Margin = new Thickness(0, 0, 0, twoColumns ? 0 : gap);
    }

    private void ResizeInspectorDllRows(object sender, SizeChangedEventArgs args)
    {
        if (args.WidthChanged && sender is ItemsControl rows)
            rows.Tag = args.NewSize.Width < 480 ? "Compact" : null;
    }

    private void OpenInspectorMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenRollbackMenu(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } trigger) return;
        menu.DataContext = model;
        menu.PlacementTarget = trigger.Tag as UIElement ?? trigger;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void HandleRollbackIconClick(object sender, RoutedEventArgs args)
    {
        if (model.IsDowngradeDownloading)
        {
            if (model.PrimaryRollbackAction.CanExecute(null))
                model.PrimaryRollbackAction.Execute(null);
            return;
        }
        OpenRollbackMenu(sender, args);
    }

    private void ToggleInspectorExecutable(object sender, RoutedEventArgs args)
    {
        InspectorExecutableChoices.Visibility = InspectorExecutableChoices.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task InitializeAsync()
    {
        if (closed) return;
        await model.LoadAsync();
        if (closed) return;
        _ = model.RefreshSystemAsync();
        await model.ScanOnStartupAsync();
        if (!closed) await model.UpdateModsOnStartupAsync();
    }

    private async void ReadAutostart(object sender, RoutedEventArgs args) => await UpdateAutostartAsync();

    private async void ChangeAutostart(object sender, RoutedEventArgs args)
    {
        var enabled = AutostartItem.IsChecked == true;
        var minimized = !autostart.Enabled || AutostartMinimizedItem.IsChecked == true;
        await UpdateAutostartAsync(new(enabled, enabled ? minimized : true));
    }

    private async Task UpdateAutostartAsync(WindowsAutostart.State? requested = null)
    {
        if (!AutostartOptions.IsEnabled) return;
        AutostartOptions.IsEnabled = false;
        try
        {
            if (requested is { } change)
            {
                await WindowsAutostart.SetAsync(change);
                autostart = change;
            }
            else
            {
                autostart = await Task.Run(WindowsAutostart.Read);
            }
            AutostartStatus.Text = "";
        }
        catch (Exception e) when (WindowsAutostart.IsSettingsError(e))
        {
            AutostartStatus.Text = e.Message;
        }
        finally
        {
            AutostartItem.IsChecked = autostart.Enabled;
            AutostartMinimizedItem.IsChecked = autostart.Minimized;
            AutostartMinimizedItem.IsEnabled = autostart.Enabled;
            MigrateAutostartItem.Visibility = autostart.Legacy ? Visibility.Visible : Visibility.Collapsed;
            AutostartOptions.IsEnabled = true;
        }
    }

    internal void ShowFromTray()
    {
        if (closed) return;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        Activate();
        tray.Visible = false;
    }

    private void DispatchTrayAction(Action action) => Dispatcher.InvokeAsync(() =>
    {
        if (!closed) action();
    });

    private void DispatchTrayAction(Func<Task> action) => Dispatcher.InvokeAsync(async () =>
    {
        if (!closed) await action();
    });

    private async Task RunTrayActionAsync(Func<Task> action, string title)
    {
        if (model.IsBusy) return;
        await action();
        if (closed) return;
        if (tray.Visible)
            tray.ShowBalloonTip(3500, title, model.Status, Forms.ToolTipIcon.Info);
        else
            model.ShowStatusToast();
    }

    private void LaunchLibraryGame(object sender, MouseButtonEventArgs args)
    {
        if (args.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement((ItemsControl)sender, source) is not ListViewItem)
            return;
        if (model.LaunchGame.CanExecute(null)) model.LaunchGame.Execute(null);
    }

    private void SelectLibraryFilter(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string filter })
            model.LibraryFilter = model.LibraryFilter == filter ? model.LibraryFilters[0] : filter;
    }

    private void OpenScanDetails(object sender, RoutedEventArgs args) => model.OpenScanDetails();
}
