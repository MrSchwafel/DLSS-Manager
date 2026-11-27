using System.IO;
using System.Security;
using System.Windows.Input;
using static DLSSManager.Core.LocalizationService;

namespace DLSSManager.App;

public sealed partial class LibraryViewModel
{
    public sealed record DisplayOption(string Code, string LabelKey);

    private bool languageSettingsReady;
    private bool savingLanguage;
    private LocalizedText languageSaveError = "";

    private static LocalizedText Message(string key, params object?[] arguments) => LocalizedText.Message(key, arguments);

    public DisplayOption[] LanguageChoices { get; } =
    [
        new(Auto, SystemLanguage == German ? "Settings.SystemDefaultGerman" : "Settings.SystemDefaultEnglish"),
        new(German, "Settings.LanguageGerman"),
        new(English, "Settings.LanguageEnglish")
    ];

    public DisplayOption[] LibraryFilterOptions { get; } =
    [
        new("All", "Library.AllGames"), new("WithDlss", "Library.WithDlss"),
        new("WithFrameGeneration", "Library.WithFrameGeneration"), new("ChangedDll", "Library.ChangedDll"),
        new("Unlocker", "Mods.Unlocker"), new("OptiScaler", "Mods.OptiScaler"),
        new("UpdateAvailable", "Library.UpdateAvailable"), new("Modified", "Library.Modified"),
        new("DlssSrOnly", "Library.DlssSrOnly"), new("Excluded", "Library.Excluded"),
        new("Steam", "Steam"), new("Epic", "Epic"), new("Ubisoft", "Ubisoft"),
        new("GOG", "GOG"), new("Xbox", "Xbox"), new("Lokal", "Common.Local")
    ];

    public DisplayOption[] ProxyOptions { get; } =
    [
        new("", "Common.None"), new("version.dll", "version.dll"),
        new("winmm.dll", "winmm.dll"), new("dxgi.dll", "dxgi.dll")
    ];

    internal Task LanguageSave { get; private set; } = Task.CompletedTask;
    public bool CanChangeLanguage => languageSettingsReady && !savingLanguage;
    public string LanguageSaveError => languageSaveError.Text;

    public string LanguagePreference
    {
        get => NormalizePreference(settings.PreferredLanguage);
        set
        {
            var preference = NormalizePreference(value);
            if (CanChangeLanguage && preference != LanguagePreference)
                LanguageSave = SaveLanguageAsync(preference);
        }
    }

    private async Task SaveLanguageAsync(string preference)
    {
        savingLanguage = true;
        languageSaveError = "";
        Notify(nameof(CanChangeLanguage));
        Notify(nameof(LanguageSaveError));
        try
        {
            await WriteSettingsAsync(current => current, preferredLanguage: preference);
            SetPreference(preference);
            Notify(string.Empty);
            if (!IsBusy) Status = Message("Settings.Saved");
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            languageSaveError = Message("Settings.LanguageSaveFailed", error.Message);
            Notify(nameof(LanguageSaveError));
        }
        finally
        {
            savingLanguage = false;
            Notify(nameof(LanguagePreference));
            Notify(nameof(CanChangeLanguage));
        }
    }
}
