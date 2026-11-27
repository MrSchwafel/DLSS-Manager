using System.IO;
using System.Windows;
using System.Windows.Input;
using DLSSManager.Core;
using Microsoft.Win32;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    public sealed record ExecutableChoice(string Path, string Label)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public string? Folder => System.IO.Path.GetDirectoryName(Label);
    }

    private ExecutableChoice? executableChoice;
    public ExecutableChoice[] ExecutableChoices { get; private set; } = [];
    public string CurrentExecutableName => SelectedLibraryGame?.Executable is { Length: > 0 } path
        ? Path.GetFileName(path) : "Keine EXE ausgewählt";
    public bool HasExecutableChoices => ExecutableChoices.Length > 0;
    public ExecutableChoice? SelectedExecutableChoice
    {
        get => executableChoice;
        set => Set(ref executableChoice, value);
    }

    public ICommand SaveExecutableChoice { get; private set; } = null!;
    public ICommand BrowseExecutable { get; private set; } = null!;

    private void InitializeExecutableCommands()
    {
        SaveExecutableChoice = new Command(() => RunAsync(token =>
            SaveExecutableAsync(SelectedLibraryGame!, SelectedExecutableChoice!.Path, token)),
            () => HasGame() && SelectedExecutableChoice is { } choice &&
                !SamePath(choice.Path, SelectedLibraryGame!.Executable));
        BrowseExecutable = new Command(() => RunAsync(async token =>
        {
            var game = SelectedLibraryGame!;
            var root = SafePaths.Root(game.Path);
            var picker = new OpenFileDialog { Title = "Spiel-EXE auswählen", InitialDirectory = root, Filter = "Programme|*.exe" };
            if (picker.ShowDialog(Application.Current.MainWindow) == true)
                await SaveExecutableAsync(game, picker.FileName, token);
        }), HasGame);
    }

    private void RefreshExecutableChoices()
    {
        var game = SelectedLibraryGame;
        ExecutableChoices = game is null ? [] :
            new[] { game.Executable, game.Inventory?.Executable }.Concat(game.Inventory?.Alternatives ?? [])
                .OfType<string>().Where(path => Path.IsPathFullyQualified(path) && SafePaths.Contains(game.Path, path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new ExecutableChoice(path, Path.GetRelativePath(game.Path, path))).ToArray();
        Notify(nameof(CurrentExecutableName));
        Notify(nameof(ExecutableChoices));
        Notify(nameof(HasExecutableChoices));
        SelectedExecutableChoice = ExecutableChoices.FirstOrDefault(choice => SamePath(choice.Path, game!.Executable));
    }

    private async Task SaveExecutableAsync(LibraryRow game, string executable, CancellationToken token)
    {
        var root = SafePaths.Root(game.Path);
        var path = SafePaths.Child(root, Path.GetRelativePath(root, executable));
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new IOException("Wähle eine vorhandene EXE im Installationsordner.");
        if (allGames.Any(row => row.Id != game.Id && SamePath(row.Path, root) && SamePath(row.Executable, path)))
            throw new IOException("Diese Spiel-EXE ist bereits einem anderen Bibliothekseintrag zugeordnet.");
        await SaveGamesAsync(allGames.Select(row => row.Id == game.Id ? row with { Executable = path } : row).ToArray(), token);
        Toast = Status = $"Spiel-EXE gespeichert: {Path.GetFileName(path)}";
    }
}
