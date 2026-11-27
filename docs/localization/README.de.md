# Neue App-Sprache hinzufügen

[English](README.md)

Verwende die [englische Sprachdatei](../../src/DLSSManager.Core/Localization/Strings.en.json) oder die [deutsche Sprachdatei](../../src/DLSSManager.Core/Localization/Strings.de.json) als Vorlage. Erzeuge die Kopie aus deinem aktuellen Checkout, damit sie alle aktuellen Schlüssel enthält.

## Sprachdatei erstellen

Führe den folgenden Befehl mit PowerShell 7 im Stammordner des Repositorys aus. Das Beispiel erstellt die französische Vorlage aus dem englischen Katalog.

```powershell
pwsh -NoProfile -File .\docs\localization\New-Language.ps1 -Language fr
```

Mit `-SourceLanguage de` verwendest du stattdessen Deutsch als Ausgangspunkt. Das Skript erstellt `src/DLSSManager.Core/Localization/Strings.fr.json` und bricht ab, falls die Datei schon existiert. Du kannst eine der Vorlagen auch von Hand kopieren.

- Übersetze die Werte auf der rechten Seite. Übernimm die Schlüssel auf der linken Seite unverändert.
- Erhalte Platzhalter wie `{0}` und `{1:t}`. Die Reihenfolge darfst du an den Satz anpassen.
- Speichere die Datei als UTF-8-JSON. Erhalte JSON-Escapes wie `\n` für Zeilenumbrüche und `\"` für doppelte Anführungszeichen.
- Verwende Produktnamen und Fachbegriffe wie DLSS, Frame Generation, OptiScaler, WHQL und Hotfix einheitlich.
- Entferne noch nicht übersetzte Einträge, wenn die App dafür Englisch anzeigen soll. Fehlt auch der englische Wert, zeigt die App den Schlüssel.

## Sprache aktivieren

1. Ergänze in `Catalogs` den Eintrag `["fr"] = (CultureInfo.GetCultureInfo("fr-FR"), Load("fr"))`. Der zweistellige Sprachcode entspricht dem Dateinamen und der Windows-Oberflächensprache. Die vollständige Kultur legt das Datums- und Zahlenformat fest. Der Build bindet `Strings.*.json` automatisch ein. [LocalizationService.cs](../../src/DLSSManager.Core/LocalizationService.cs)
2. Ergänze `new("fr", "Settings.LanguageFrench")` in `LanguageChoices`. Füge den Schlüssel `Settings.LanguageFrench` mit dem Wert `Français (FR)` in die englische, deutsche und französische Sprachdatei ein. [LibraryViewModel.Localization.cs](../../src/DLSSManager.App/LibraryViewModel.Localization.cs)
3. Erweitere den Eintrag `new(Auto, ...)`, sodass bei `SystemLanguage` gleich `"fr"` der Schlüssel `Settings.SystemDefaultFrench` gewählt wird. Hinterlege den Schlüssel in denselben drei Sprachdateien, etwa mit `System Default (French)`, `Systemstandard (Französisch)` und `Langue du système (français)`. Übernimm die Fälle für Deutsch und Englisch. [Sprachauswahl](../../src/DLSSManager.App/LibraryViewModel.Localization.cs)

```csharp
new(Auto, SystemLanguage switch
{
    "fr" => "Settings.SystemDefaultFrench",
    German => "Settings.SystemDefaultGerman",
    _ => "Settings.SystemDefaultEnglish"
}),
```

Nach der Registrierung steht Französisch unter **Einstellungen → Allgemein → Sprache** zur Auswahl. Auf einem französischsprachigen Windows wird es auch über **Systemstandard** erkannt. Die Google-Übersetzung für NVIDIA-Release-Notes ist separat an Deutsch gekoppelt.

## Prüfen und einreichen

- Starte `.\build.ps1` im Stammordner des Repositorys.
- Wechsle zwischen der neuen Sprache, Englisch und Systemstandard. Prüfe die Anzeige der gewählten Sprache und ob sie nach einem Neustart gespeichert ist.
- Prüfe Buttons, Badges, Tabellenüberschriften, Dialoge und das Tray-Menü auf abgeschnittene Texte. Kontrolliere eine Meldung mit Platzhaltern sowie ein Datum oder eine Zahl.
- Nimm die neue JSON-Datei, die Anzeigetexte und die beiden Registrierungsänderungen in deinen GitHub-Pull-Request auf.
