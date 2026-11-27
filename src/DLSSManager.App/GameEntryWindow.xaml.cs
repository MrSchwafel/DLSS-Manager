using static DLSSManager.Core.LocalizationService;
using System.IO;
using System.Windows;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public partial class GameEntryWindow : Window
{
    private readonly LibraryRow? original;
    public LibraryRow? Entry { get; private set; }

    public GameEntryWindow(LibraryRow? original, string defaultProxy)
    {
        InitializeComponent();
        this.original = original;
        DefaultProxy = defaultProxy;
        TitleInput.Text = original?.Title ?? "";
        PathInput.Text = original?.Path ?? "";
        ExeInput.Text = original?.Executable ?? "";
        ArgumentsInput.Text = original?.Arguments ?? "";
        IconInput.Text = original?.Icon ?? "";
    }

    private void PickFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) PathInput.Text = dialog.FolderName;
    }

    private string DefaultProxy { get; }

    private void PickExe(object sender, RoutedEventArgs e)
    {
        Pick(ExeInput, GetText("GameEntry.ProgramsExe"));
        if (ExeInput.Text.Length == 0 || PathInput.Text.Length != 0) return;
        PathInput.Text = Path.GetDirectoryName(ExeInput.Text);
        if (TitleInput.Text.Length == 0) TitleInput.Text = Path.GetFileNameWithoutExtension(ExeInput.Text);
    }
    private void PickIcon(object sender, RoutedEventArgs e) => Pick(IconInput, LibraryRow.IconFilter);

    private void Pick(System.Windows.Controls.TextBox field, string filter)
    {
        var dialog = new OpenFileDialog { InitialDirectory = PathInput.Text, Filter = filter };
        if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = TitleInput.Text.Trim();
            var folder = PathInput.Text.Trim();
            var exe = ExeInput.Text.Trim();
            var icon = IconInput.Text.Trim();
            if (title.Length == 0 || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                throw new IOException(GetText("GameEntry.AGameTitleAndAnExistingInstallation"));
            folder = SafePaths.Root(folder);
            if (exe.Length != 0 && (!Path.IsPathFullyQualified(exe) || !File.Exists(exe) ||
                !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !SafePaths.Contains(folder, exe)))
                throw new IOException(GetText("GameEntry.TheGameExecutableMustBeInsideThe"));
            if (icon.Length != 0 && (!Path.IsPathFullyQualified(icon) || !File.Exists(icon) ||
                Path.GetExtension(icon).ToLowerInvariant() is not (".ico" or ".png" or ".jpg" or ".jpeg" or ".exe" or ".dll")))
                throw new IOException(GetText("GameEntry.SelectAnExistingImageFileForThe"));
            Entry = new(title, folder, original?.Proxy ?? DefaultProxy, exe, Id: original?.Id ?? Guid.NewGuid(),
                Icon: icon.Length == 0 ? null : icon, Ignored: original?.Ignored ?? false,
                Launcher: original?.Launcher ?? "Lokal", Arguments: ArgumentsInput.Text, Overlay: original?.Overlay ?? false);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { ErrorText.Text = ex.Message; }
    }
}
