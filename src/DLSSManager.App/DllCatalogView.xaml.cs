using static DLSSManager.Core.LocalizationService;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DLSSManager.Core;

namespace DLSSManager.App;

public partial class DllCatalogView : UserControl
{
    public static readonly RoutedUICommand Download = new(GetText("Catalog.DownloadDll"), nameof(Download), typeof(DllCatalogView));
    public static readonly RoutedUICommand Pin = new(GetText("Catalog.SaveBatchTarget"), nameof(Pin), typeof(DllCatalogView));
    public static readonly RoutedUICommand CopyHash = new(GetText("Catalog.CopySha256"), nameof(CopyHash), typeof(DllCatalogView));

    public DllCatalogView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            LocalizationService.LanguageChanged += RefreshDownloadLabel;
            RefreshDownloadLabel(null, EventArgs.Empty);
        };
        Unloaded += (_, _) => LocalizationService.LanguageChanged -= RefreshDownloadLabel;
    }

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
        RefreshDownloadLabel(null, EventArgs.Empty);
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshDownloadLabel(object? sender, EventArgs e)
    {
        var count = DownloadEntries(null).Length;
        DownloadSelection.Content = count > 0 ? GetText("DllCatalog.DownloadSelectionCount", count) : GetText("DllCatalog.DownloadSelection");
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
        public bool IsOnline => Entry.Availability == "Online";
        public string AvailabilityLabel => GetText(IsOnline ? "DllCatalog.Online" : "Common.Local");
        public bool CanPin => LibraryViewModel.CanPinCatalogEntry(Entry);
        public string SizeLabel => Entry.Size is { } size ? (size / 1048576d).ToString("N2", Culture) + " MB" : "—";
        public string? SizeTooltip => Entry.Size?.ToString("N0", Culture) is { } size ? size + GetText("DllCatalog.Bytes") : null;
        public string DateLabel => Entry.CatalogDate?.ToString("d", Culture) ?? "—";
        public string HashLabel => Entry.Sha256 is { Length: > 17 } hash ? $"{hash[..8]}…{hash[^8..]}" : Entry.Sha256 ?? "—";
    }
}
