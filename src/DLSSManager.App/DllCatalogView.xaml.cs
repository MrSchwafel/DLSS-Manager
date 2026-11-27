using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DLSSManager.Core;

namespace DLSSManager.App;

public partial class DllCatalogView : UserControl
{
    public static readonly RoutedUICommand Download = new("DLL herunterladen", nameof(Download), typeof(DllCatalogView));
    public static readonly RoutedUICommand Pin = new("Als Batch-Ziel speichern", nameof(Pin), typeof(DllCatalogView));
    public static readonly RoutedUICommand CopyHash = new("SHA-256 kopieren", nameof(CopyHash), typeof(DllCatalogView));

    public DllCatalogView() => InitializeComponent();

    private LibraryViewModel? Model => DataContext as LibraryViewModel;

    private DllCatalogEntry[] DownloadEntries(object? parameter) =>
        (parameter is Row row ? new[] { row } : CatalogTable.SelectedItems.OfType<Row>())
        .Select(item => item.Entry).Where(entry => entry.Availability == "Online").ToArray();

    private DllCatalogEntry? PinEntry(object? parameter) =>
        (parameter is Row row ? new[] { row } : CatalogTable.SelectedItems.OfType<Row>())
        .Select(item => item.Entry).LastOrDefault(LibraryViewModel.CanPinCatalogEntry);

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != CatalogTable) return;
        var count = DownloadEntries(null).Length;
        DownloadSelection.Content = count > 0 ? $"↓  Auswahl herunterladen ({count})" : "↓  Auswahl herunterladen";
        CommandManager.InvalidateRequerySuggested();
    }

    private void CanDownload(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = Model is { IsBusy: false } && DownloadEntries(e.Parameter).Length > 0;

    private async void ExecuteDownload(object sender, ExecutedRoutedEventArgs e)
    {
        if (Model is { } model) await model.DownloadCatalogEntriesAsync(DownloadEntries(e.Parameter));
    }

    private void CanPin(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = Model is { IsBusy: false } && PinEntry(e.Parameter) is not null;

    private async void ExecutePin(object sender, ExecutedRoutedEventArgs e)
    {
        if (Model is { } model && PinEntry(e.Parameter) is { } entry) await model.PinCatalogEntryAsync(entry);
    }

    private void CanCopyHash(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = e.Parameter is Row { Entry.Sha256.Length: > 0 };

    private void ExecuteCopyHash(object sender, ExecutedRoutedEventArgs e)
    {
        if (Model is { } model && e.Parameter is Row { Entry.Sha256: { } hash })
            model.CopyCatalogHash(hash);
    }

    private void ChangeFilter(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && sender is Button { Tag: string filter }) model.CatalogFilter = filter;
    }

    private async void OpenUnlockerReleases(object sender, RoutedEventArgs e)
    {
        if (Model is { } model) await model.OpenUnlockerReleasesAsync();
    }

    public sealed record Row(DllCatalogEntry Entry, bool IsBatchTarget)
    {
        private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
        public bool IsOnline => Entry.Availability == "Online";
        public bool CanPin => LibraryViewModel.CanPinCatalogEntry(Entry);
        public string SizeLabel => Entry.Size is { } size ? (size / 1048576d).ToString("N2", German) + " MB" : "—";
        public string? SizeTooltip => Entry.Size?.ToString("N0", German) is { } size ? size + " Bytes" : null;
        public string DateLabel => Entry.CatalogDate?.ToString("dd.MM.yyyy", German) ?? "—";
        public string HashLabel => Entry.Sha256 is { Length: > 17 } hash ? $"{hash[..8]}…{hash[^8..]}" : Entry.Sha256 ?? "—";
    }
}
