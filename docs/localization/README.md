# Add an app language

[Deutsch](README.de.md)

Use either the [English catalog](../../src/DLSSManager.Core/Localization/Strings.en.json) or the [German catalog](../../src/DLSSManager.Core/Localization/Strings.de.json) as your translation template. Generate a copy from the current checkout so it includes every current key.

## Create the catalog

From the repository root, run this in PowerShell 7. This example prepares French from English.

```powershell
pwsh -NoProfile -File .\docs\localization\New-Language.ps1 -Language fr
```

Add `-SourceLanguage de` to start from German. The script creates `src/DLSSManager.Core/Localization/Strings.fr.json` and stops if that file already exists. You can also copy either source catalog by hand.

- Translate the values on the right. Keep the keys on the left as written.
- Preserve format placeholders such as `{0}` and `{1:t}`. You can change their order to suit the sentence.
- Save as UTF-8 JSON. Keep JSON escapes such as `\n` for line breaks and `\"` for literal double quotes.
- Keep product names and technical terms such as DLSS, Frame Generation, OptiScaler, WHQL and Hotfix recognizable.
- Remove any untranslated entries if you want the app to use the English fallback for them. If English has no value either, the app displays the key.

## Enable the language

1. Add `["fr"] = (CultureInfo.GetCultureInfo("fr-FR"), Load("fr"))` to `Catalogs`. The two-letter code matches the filename and Windows UI language. The full culture controls date and number formatting. The project embeds `Strings.*.json` during the build. [LocalizationService.cs](../../src/DLSSManager.Core/LocalizationService.cs)
2. Add `new("fr", "Settings.LanguageFrench")` to `LanguageChoices`. Add `Settings.LanguageFrench` with the value `Français (FR)` to the English, German and French catalogs. [LibraryViewModel.Localization.cs](../../src/DLSSManager.App/LibraryViewModel.Localization.cs)
3. Extend the `new(Auto, ...)` entry to select `Settings.SystemDefaultFrench` when `SystemLanguage` is `"fr"`. Define that key in the same three catalogs, for example `System Default (French)`, `Systemstandard (Französisch)` and `Langue du système (français)`. Keep the German and English cases. [Language picker](../../src/DLSSManager.App/LibraryViewModel.Localization.cs)

```csharp
new(Auto, SystemLanguage switch
{
    "fr" => "Settings.SystemDefaultFrench",
    German => "Settings.SystemDefaultGerman",
    _ => "Settings.SystemDefaultEnglish"
}),
```

Once registered, French is available under **Settings → General → Language** and is selected in **System Default** mode on French-language Windows. The Google Translate option for NVIDIA release notes has a separate German-only setting.

## Verify and submit

- Run `.\build.ps1` from the repository root.
- Switch between the new language, English and System Default. Check the picker label and confirm that your selection survives a restart.
- Check buttons, badges, table headers, dialogs and the tray menu for clipping. Verify a message with placeholders and a date or number.
- Include the new JSON file, the label entries and the two registration changes in your GitHub pull request.
