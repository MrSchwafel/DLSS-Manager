**Sprache / Language:** **Deutsch** · [English](README.md)

<p align="center">
  <img src="resources/logo.png" alt="DLSS Manager Logo" width="200">
</p>

# DLSS Manager

**Verwalte Spiele-DLLs, Frame-Generation-Mods und Spielprofile in einer dunklen Windows-Oberfläche.** Der DLSS Manager bündelt NVIDIA DLSS, Frame Generation, Ray Reconstruction, Intel XeSS, AMD FSR, den FrameGen & MFG Unlocker (SM86) und OptiScaler in einer Spielebibliothek.

**Windows x64 · C# / .NET 10 · WPF · Deutsche Oberfläche**

[Installation](#installation) · [Erste Schritte](#erste-schritte) · [Funktionen](#funktionen) · [Treiber-Vorschau](#treiber-vorschau) · [Aus dem Quellcode bauen](#aus-dem-quellcode-bauen) · [Credits](#datenquellen-und-credits) · [Releases](https://github.com/MrSchwafel/DLSS-Manager/releases)

Der DLSS Manager befindet sich als C#-Vorschau in Entwicklung. Dieses GitHub-Repository enthält die Projektdokumentation und Ressourcen. Gebaut wird die App im separaten C#-Quellcode-Checkout.

Das Logo nimmt künstliche Hardware-Grenzen aufs Korn. DLSS Manager ist ein Community-Projekt, das unabhängig von NVIDIA entwickelt wird.

## Installation

Erstelle mit der [Build-Anleitung](#aus-dem-quellcode-bauen) ein eigenständiges Windows-x64-Paket. Die ZIP-Datei heißt `DLSS-Manager-CSharp-win-x64-<version>.zip`.

1. Wenn eine `.zip.sha256`-Datei beiliegt, vergleiche ihren Hash mit der PowerShell-Ausgabe von `Get-FileHash -Algorithm SHA256 .\paket.zip`.
2. Entpacke das komplette ZIP in einen beschreibbaren Ordner und starte `DLSSManager.App.exe`.
3. Standardmäßig nutzt die App den Datenordner `Dokumente\DLSS Manager`. Wähle in **Einstellungen** einen anderen Ordner für den nächsten Start oder starte mit `--portable`, um den Ordner `data` neben der EXE zu verwenden.

### Systemvoraussetzungen

- Windows x64. Der C#-Entwicklungsbuild wurde unter Windows 11 getestet.
- Das eigenständige Paket enthält die .NET Desktop Runtime. Frameworkabhängige Builds benötigen die x64-Version der .NET 10 Desktop Runtime.
- Schreibzugriff auf den Datenordner und die zu ändernden Spieldateien. Der globale DLSS-Indikator benötigt zusätzlich Schreibrechte auf den systemweiten NVIDIA-Registrierungsschlüssel.
- Internet für Katalog, DLSSdb, Mod-Downloads und Releaseprüfung. Lokale Katalogdateien lassen sich offline verwenden. Für OptiScaler-Downloads im 7-Zip-Format verwendet die App Windows-`tar.exe`.
- Ein kompatibles Spiel, eine geeignete GPU und ein passender Treiber für die gewünschte Funktion. Details stehen bei [SM86](https://github.com/sdli1995/dlssg_for_sm86) und [OptiScaler](https://github.com/optiscaler/OptiScaler).

> [!WARNING]
> **Nur für Einzelspieler-Spiele verwenden.** DLL-Tausch und Proxy-Mods können Anti-Cheat-Systeme auslösen und zu Sperren führen. Die Anti-Cheat-Erkennung kann Spiele übersehen. Batch-Updates überspringen Spiele mit erkanntem Anti-Cheat. Änderungen im Spiel-Cockpit erfordern bei solchen Spielen eine ausdrückliche Bestätigung.

## Erste Schritte

1. Verwende **Scan** für Steam, Epic, Ubisoft Connect, GOG, Xbox / Game Pass und hinterlegte Ordner. **Alle Laufwerke** sucht zusätzlich nach nativen Upscaling-DLLs auf lokalen Laufwerken. Spiele lassen sich auch manuell hinzufügen.
2. Wähle ein Spiel, starte **DLLs scannen** und bestätige bei mehreren Treffern die Spiel-EXE. Prüfe den vorgeschlagenen DLL-Pfad vor dem Tausch. Ein Doppelklick auf den Eintrag startet die hinterlegte EXE.
3. Aktualisiere den **DLL-Katalog** oder importiere eine lokale x64-DLL. Wende die Version im Spiel-Cockpit an oder prüfe den Batch-Plan unter **Alle aktualisieren**.
4. Wähle auf der Mod-Karte einen verfügbaren Proxy, installiere das Paket oder öffne den INI-Konfigurator. Unter **Wiederherstellung** findest du Sicherungen und offene Dateitransaktionen.

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

## Aus dem Quellcode bauen

Führe die folgenden Befehle mit PowerShell 7 im separaten C#-Checkout mit `DLSSManager.slnx` aus. Installiere ein kompatibles .NET SDK. In `global.json` ist `10.0.100` als Basis festgelegt. Die Skripte verwenden bevorzugt `%LocalAppData%\DLSSManagerDev\dotnet\dotnet.exe` und greifen sonst auf `dotnet` aus dem Suchpfad zurück.

```powershell
.\build.ps1
.\src\DLSSManager.App\bin\Release\net10.0-windows\DLSSManager.App.exe
```

`build.ps1` baut die Solution und startet das automatisierte Testprogramm. Für ein eigenständiges Windows-x64-Paket verwende eine Version, für die noch kein Paket in `artifacts` liegt.

```powershell
$version = '0.1.0-preview.9'
.\publish.ps1 -Version $version
.\verify-release.ps1 -Package ".\artifacts\DLSS-Manager-CSharp-win-x64-$version.zip"
```

Das Publish-Skript schreibt das ZIP und die zugehörige SHA-256-Datei nach `artifacts` sowie die entpackte App nach `artifacts\win-x64`.

Die Windows-Oberfläche in `src/DLSSManager.App` nutzt WPF, XAML und MVVM sowie WinForms für Tray-Symbol und Menü. `src/DLSSManager.Core` enthält Scan, Kataloge, Mod-Profile und Dateitransaktionen. `tests/DLSSManager.Tests` prüft diese Dienste mit temporären Testdaten.

## Fehlerbehebung

Gib die Manager-Version, den Windows-Build, GPU und Treiberversion, das Spiel, die betroffene DLL- oder Mod-Version, die Schritte zum Reproduzieren und einen relevanten Diagnoseauszug an. Entferne persönliche Pfade, Benutzernamen und andere private Angaben vor dem Posten.

## Datenquellen und Credits

- [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) — verwaltete FrameGen-Mod.
- [beeradmoore/dlss-swapper](https://github.com/beeradmoore/dlss-swapper) und [DLSS-Swapper Manifest Builder](https://github.com/beeradmoore/dlss-swapper-manifest-builder) — DLL-Versionsinformationen und Downloadreferenzen. DLSS Manager verwendet den Katalog unabhängig von der DLSS-Swapper-App.
- [JZ6/DLSSdb](https://github.com/JZ6/DLSSdb) — verwendete RTX-/DLSS-Datenbankinformationen.
- [OptiScaler](https://github.com/optiscaler/OptiScaler) — verwaltete Mod für Upscaling und Frame Generation.

Fremde DLLs, Mods und Datenquellen unterliegen den Bedingungen ihrer jeweiligen Rechteinhaber und Projekte.
