using System.Windows;

namespace DLSSManager.App;

public partial class BatchUpdateWindow : Window
{
    public sealed record UpdateRow(string Game, string Change);

    public BatchUpdateWindow(UpdateRow[] updates, int gameCount, string title = "Alle aktualisieren")
    {
        InitializeComponent();
        Title = title;
        var updateLabel = updates.Length == 1 ? "1 DLL-Update" : $"{updates.Length} DLL-Updates";
        var gameLabel = gameCount == 1 ? "1 Spiel" : $"{gameCount} Spiele";
        DataContext = new { Updates = updates, Summary = $"{updateLabel} für {gameLabel}" };
    }

    private void Apply(object sender, RoutedEventArgs e) => DialogResult = true;
}
