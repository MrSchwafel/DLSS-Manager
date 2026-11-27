using static DLSSManager.Core.LocalizationService;
using System.Windows;

namespace DLSSManager.App;

public partial class BatchUpdateWindow : Window
{
    public sealed record UpdateRow(string Game, string Change);

    public BatchUpdateWindow(UpdateRow[] updates, int gameCount, string? title = null)
    {
        InitializeComponent();
        if (title is not null) Title = title;
        var updateLabel = updates.Length == 1 ? GetText("BatchUpdate.DLLUpdate") : GetText("BatchUpdate.DLLUpdates", updates.Length);
        var gameLabel = gameCount == 1 ? GetText("BatchUpdate.Game") : GetText("BatchUpdate.Games", gameCount);
        DataContext = new { Updates = updates, Summary = GetText("BatchUpdate.For", updateLabel, gameLabel) };
    }

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
