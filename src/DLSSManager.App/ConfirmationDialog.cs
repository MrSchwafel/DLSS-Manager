using System.Windows;
using System.Windows.Controls;
using static DLSSManager.Core.LocalizationService;

namespace DLSSManager.App;

internal static class ConfirmationDialog
{
    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var window = new Window
        {
            Owner = owner,
            Title = title,
            SizeToContent = SizeToContent.Height,
            Width = 520,
            MaxWidth = Math.Min(640, SystemParameters.WorkArea.Width),
            MaxHeight = SystemParameters.WorkArea.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        window.SetResourceReference(FrameworkElement.StyleProperty, "CustomCaptionWindow");
        window.SetResourceReference(Window.BackgroundProperty, "Canvas");
        window.SetResourceReference(Window.ForegroundProperty, "Ink");

        var body = new DockPanel { Margin = new(18) };
        window.Content = body;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom);
        body.Children.Add(actions);

        var endResult = buttons switch
        {
            MessageBoxButton.YesNo => MessageBoxResult.No,
            MessageBoxButton.OKCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.OK
        };
        var result = endResult;
        if (buttons == MessageBoxButton.YesNo)
        {
            AddButton("Common.Yes", MessageBoxResult.Yes);
            AddButton("Common.No", MessageBoxResult.No);
        }
        else
        {
            AddButton("Common.Ok", MessageBoxResult.OK);
            if (buttons == MessageBoxButton.OKCancel) AddButton("Common.Cancel", MessageBoxResult.Cancel);
        }

        var content = new DockPanel { Margin = new(0, 0, 0, 12) };
        if (icon is MessageBoxImage.Warning or MessageBoxImage.Question)
        {
            var glyph = new TextBlock { Text = icon == MessageBoxImage.Warning ? "⚠" : "?", FontSize = 20, Margin = new(0, 0, 10, 0) };
            DockPanel.SetDock(glyph, Dock.Left);
            content.Children.Add(glyph);
        }
        content.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height * 0.65,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 }
        });
        body.Children.Add(content);
        window.ShowDialog();
        return result;

        void AddButton(string key, MessageBoxResult buttonResult)
        {
            var button = new Button
            {
                Content = GetText(key),
                MinWidth = 82,
                IsDefault = defaultResult == buttonResult || (defaultResult == MessageBoxResult.None && actions.Children.Count == 0),
                IsCancel = buttonResult == endResult
            };
            button.Click += (_, _) =>
            {
                result = buttonResult;
                window.Close();
            };
            actions.Children.Add(button);
        }
    }
}
