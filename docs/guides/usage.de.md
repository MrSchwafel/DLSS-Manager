# Anleitung für DLSS Manager

[Zurück zur README](../../README.de.md) · [English](usage.md)

## Paket prüfen

Wenn dem ZIP eine SHA-256-Datei beiliegt, führe in PowerShell den folgenden Befehl mit dem Pfad zu deinem Paket aus und vergleiche das Ergebnis mit dem Hash in der Datei.

    Get-FileHash -Algorithm SHA256 .\DLSS-Manager-CSharp-win-x64-1.0.0.zip

## Systemvoraussetzungen

- Windows x64. Der C#-Entwicklungsbuild wurde unter Windows 11 getestet.
- Das eigenständige Paket enthält die .NET Desktop Runtime. Frameworkabhängige Builds benötigen die x64-Version der .NET 10 Desktop Runtime.
- Schreibzugriff auf den Datenordner und die zu ändernden Spieldateien. Der globale DLSS-Indikator benötigt zusätzlich Schreibrechte auf den systemweiten NVIDIA-Registrierungsschlüssel.
- Internet für Katalog, DLSSdb, Mod-Downloads und Releaseprüfung. Lokale Katalogdateien lassen sich offline verwenden. Für OptiScaler-Downloads im 7-Zip-Format verwendet die App Windows-`tar.exe`.
- Ein kompatibles Spiel, eine geeignete GPU und ein passender Treiber für die gewünschte Funktion. Details stehen bei [SM86](https://github.com/sdli1995/dlssg_for_sm86) und [OptiScaler](https://github.com/optiscaler/OptiScaler).

## Funktionen

### Spielebibliothek und Erkennung

- Launcher-Erkennung, optionaler Scan beim Start, manuelle Scanpfade und Spieleinträge, Ordnerausschlüsse, EXE-Blockliste und Auswahl der Spiel-EXE.
- Erkennung von DLSS Super Resolution (**SR**, `nvngx_dlss.dll`), Frame Generation (**FG**, `nvngx_dlssg.dll`), Ray Reconstruction (**RR**, `nvngx_dlssd.dll`), XeSS und FSR. Der Scan erfasst DLL-Pfade in Unterordnern, etwa neben der Spiel-EXE oder unter Unreal-Engine-Plugins. Prüfe bei mehreren Kopien den genauen Pfad.
- Live-Suche nach Titel und Pfad, Launcher- und Statusfilter, farbcodierte Badges sowie Filter-Chips mit Zählern für **Alle Spiele**, **Mit DLSS**, **Mit FrameGen**, **DLL geändert**, **FG & MFG Unlocker** und **OptiScaler**. **DLL geändert** kennzeichnet eine erkannte Original-DLL-Sicherung.

### DLL-Katalog und Batch-Updates

- Online-Katalog und lokaler Cache, numerischer Versionsvergleich, SHA-256 für lokal geprüfte Dateien und Import lokaler x64-DLLs. Die Prüfungen hängen von Quelle und Dateityp ab.
- DLLs einzeln tauschen oder einen Batch-Plan für mehrere Spiele prüfen. Mit **Batch-Ziel** lässt sich pro DLL-Typ eine lokal verfügbare Katalogversion festlegen. Laufende Spiele und andere ungeeignete Einträge werden übersprungen oder zurückgestellt.
- Optionale Hintergrundupdates für DLLs, SM86 und OptiScaler lassen sich unabhängig voneinander aktivieren.

### FrameGen & MFG Unlocker und OptiScaler

- Upstream-Pakete über den Katalog laden, pro Spiel installieren oder aktualisieren und je nach Mod und vorhandenen Dateien einen verfügbaren Proxy wie `version.dll` oder `dxgi.dll` wählen.
- Für die SM86-Installation muss eine native `nvngx_dlssg.dll` erkannt worden sein. DLSSdb liefert Funktionsangaben zur Bibliothek. Bei einer eindeutigen MFG-Empfehlung kann der Manager den Wert als SM86-Standard übernehmen. Globale und spielspezifische Vorgaben haben Vorrang.
- `dlssg_sm86.ini` und `OptiScaler.ini` über einen datengetriebenen Editor mit Suche, wichtigsten Einstellungen, allen INI-Sektionen und Experteneinträgen im Format `Abschnitt|Schlüssel=Wert` konfigurieren.
- Die Werte werden in der Reihenfolge **Mod-Standard → globale App-Vorlage → spielspezifisches Profil** zusammengeführt. Vorlagen und Abweichungen bleiben im Manager-Datenordner über Mod-Updates und Deinstallationen hinweg gespeichert.

### Backups und Wiederherstellung

- Beim nativen DLL-Tausch wird bei Bedarf eine Originalkopie als `.bak` neben der DLL angelegt. Weitere Wechsel erhalten die Originalkopie.
- Original-DLLs einzeln oder gruppiert pro Spiel wiederherstellen. Dateitransaktionen sichern Originale, prüfen SHA-256 und führen ein Journal. Offene Vorgänge können zurückgerollt werden. Abweichende externe Änderungen können den Rollback blockieren.
- Verwaltete SM86-Installationen unterstützen den Rückwechsel auf frühere Paketversionen. Verwende für die jeweiligen Dateien die passende Mod- oder DLL-Wiederherstellung.

### Treiber-Vorschau

Wähle im Treiber-Rollback-Menü **Alle Versionen & Übersicht…**. Halte in **Treiber-Übersicht & Rollback** den Mauszeiger über eine WHQL- oder Hotfix-Zeile, um die Release Notes zu sehen.

- **Kompakt (Summary)** ist die Standardeinstellung. Die Karte zeigt Spiele- und Feature-Highlights sowie bis zu drei Fixes.
- **Detailliert (Release-Notes mit Badges)** zeigt Spiele, Features und Treiberstatus als Badges. Die Karte listet bis zu vier Fixes auf und zeigt die Anzahl weiterer Fixes an.

Wähle den Stil unter **Einstellungen → Treiber & Cache → Treiber-Vorschau (Hover-Card)** oder wechsle direkt im Dialog zwischen **Kompakt** und **Details**. Beide Umschalter verwenden dieselbe Einstellung, die nach einem Neustart erhalten bleibt.

### Profi-Tweaks und System-Tray

- Unter **Profi-Tweaks & Presets** vorhandene NVIDIA-Anwendungsprofile lesen und ändern. Der spielbezogene DLSS-Indikator greift beim Direktstart aus dem Manager. Der globale Indikator verwendet die NVIDIA-Registrierungseinstellung.
- Im dunklen Tray-Menü die App oder Einstellungen öffnen, die Bibliothek scannen, DLL-Katalog und Datenbanken synchronisieren, auf App-Updates prüfen oder den Daten- und Backup-Ordner öffnen. Die App wechselt beim Minimieren in den Infobereich und kann dort mit `--minimized` starten. Windows-Autostart ist optional.
- Unter **Werkzeuge** stehen GitHub-Releaseprüfung und Diagnoseexport zur Verfügung. Manager-Updates werden manuell aus einem Release-Paket installiert.

## Fehlerbehebung

Gib die Manager-Version, den Windows-Build, GPU und Treiberversion, das Spiel, die betroffene DLL- oder Mod-Version, die Schritte zum Reproduzieren und einen relevanten Diagnoseauszug an. Entferne persönliche Pfade, Benutzernamen und andere private Angaben vor dem Posten.

## Repository-Aufbau

| Pfad | Inhalt |
| --- | --- |
| [src/DLSSManager.App](../../src/DLSSManager.App/) | WPF-Fenster, Ansichten, ViewModels und Infobereich |
| [src/DLSSManager.Core](../../src/DLSSManager.Core/) | Spieleerkennung, DLL-Kataloge, Mod-Profile und Dateitransaktionen |
| [docs/guides](.) | Englische und deutsche Anleitung |
| [docs/screenshots](../screenshots/) | Aktuelle Screenshots der C#-App für beide READMEs |
| [resources/branding](../../resources/branding/) | Logo, Icons und Favicon |
| [resources/legacy/autohotkey](../../resources/legacy/autohotkey/) | Sprachpakete und DLL-Preload-Skript der früheren AutoHotkey-Version |
| [build.ps1](../../build.ps1), [publish.ps1](../../publish.ps1), [verify-release.ps1](../../verify-release.ps1) | Build, Windows-x64-Paket und Paketprüfung |

Die Build-Skripte verwenden bevorzugt das SDK unter %LocalAppData%\DLSSManagerDev\dotnet und greifen sonst auf dotnet im Suchpfad zurück. Das Publish-Skript schreibt ZIP und SHA-256-Datei nach artifacts und die entpackte App nach artifacts\win-x64.
