**Language / Sprache:** **English** · [Deutsch](README.de.md)

<p align="center">
  <img src="resources/logo.png" alt="DLSS Manager logo" width="200">
</p>

# DLSS Manager

**Manage game DLLs, Frame Generation mods and per-game settings in a dark Windows workspace.** DLSS Manager brings NVIDIA DLSS, Frame Generation, Ray Reconstruction, Intel XeSS, AMD FSR, the FrameGen & MFG Unlocker (SM86) and OptiScaler together in one game library.

**Windows x64 · C# / .NET 10 · WPF · German interface**

[Installation](#installation) · [Getting started](#getting-started) · [Features](#features) · [Driver previews](#driver-previews) · [Build from source](#build-from-source) · [Credits](#data-sources-and-credits) · [Releases](https://github.com/MrSchwafel/DLSS-Manager/releases)

DLSS Manager is currently in C# preview development. This GitHub repository contains project documentation and resources. Build the app from the separate C# source checkout.

The logo pokes fun at artificial hardware restrictions. DLSS Manager is a community project developed independently of NVIDIA.

## Installation

Use the [build instructions](#build-from-source) to create a self-contained Windows x64 package. The output is named `DLSS-Manager-CSharp-win-x64-<version>.zip`.

1. If a `.zip.sha256` file accompanies the package, compare its hash with the output of `Get-FileHash -Algorithm SHA256 .\package.zip` in PowerShell.
2. Extract the whole ZIP to a writable folder and run `DLSSManager.App.exe`.
3. The default data folder is `Documents\DLSS Manager`. Choose a different location in **Einstellungen** and restart, or launch with `--portable` to use the `data` folder next to the executable.

### Requirements

- Windows x64. The C# development build has been tested on Windows 11.
- The self-contained package includes the .NET Desktop Runtime. Framework-dependent builds require the x64 .NET 10 Desktop Runtime.
- Write access to the app's data folder and to the game files you choose to change. The global DLSS indicator also needs permission to write to NVIDIA's machine-wide registry key.
- Internet access for catalog, DLSSdb, mod and release checks or downloads. Local catalog files can be used offline. OptiScaler downloads provided as 7-Zip archives use Windows `tar.exe`.
- A compatible game, GPU and driver for the selected feature or mod. Consult [SM86](https://github.com/sdli1995/dlssg_for_sm86) and [OptiScaler](https://github.com/optiscaler/OptiScaler) for their compatibility details.

> [!WARNING]
> **Use only with single-player games.** DLL replacements and proxy mods can trigger anti-cheat systems and lead to bans. Anti-cheat detection can miss games. Batch updates skip entries with detected anti-cheat, and cockpit changes for those entries require explicit confirmation.

## Getting started

1. Use **Scan** for Steam, Epic, Ubisoft Connect, GOG, Xbox / Game Pass installations and configured folders. **Alle Laufwerke** adds a search for native upscaling DLLs on local drives. You can also add a game manually.
2. Select a game, run **DLLs scannen** and confirm the game executable if several candidates appear. Check which DLL path the app proposes before changing it. Double-click a library entry to launch its saved executable.
3. Refresh the **DLL-Katalog** or import a local x64 DLL. Apply a version from the game cockpit or choose **Alle aktualisieren** to review a batch plan.
4. For a mod, use its card on the game page to select a proxy, install the package or open its INI editor. Use **Wiederherstellung** to review backups and unfinished file transactions.

## Features

### Game library and scanning

- Launcher discovery and optional launch-time scanning, manual paths and game entries, exclusions, an executable blocklist and explicit executable selection.
- Detection of DLSS Super Resolution (**SR**, `nvngx_dlss.dll`), Frame Generation (**FG**, `nvngx_dlssg.dll`), Ray Reconstruction (**RR**, `nvngx_dlssd.dll`), XeSS and FSR. The scan records DLL paths in subfolders, including copies next to the game binary or under Unreal Engine plugins. Review the exact path when several copies exist.
- Live search by title and path, a launcher/status filter, colored badges and counted chips for **Alle Spiele**, **Mit DLSS**, **Mit FrameGen**, **DLL geändert**, **FG & MFG Unlocker** and **OptiScaler**. **DLL geändert** reflects a detected original-DLL backup.

### DLL catalog and updates

- Online catalog and local cache, numeric version comparison, SHA-256 for locally verified files and local x64 DLL import. Verification depends on the source and file type.
- Replace individual files or review multiple games in a batch. **Batch-Ziel** lets you pin a locally available catalog version per DLL type. Running games and other ineligible entries are skipped or deferred.
- Independently enable optional background updates for DLLs, SM86 and OptiScaler in Settings.

### FrameGen & MFG Unlocker and OptiScaler

- Download upstream packages through the catalog, install or update per game and choose an available proxy such as `version.dll` or `dxgi.dll`, depending on the mod and existing files.
- SM86 installation requires a detected native `nvngx_dlssg.dll`. Sync DLSSdb for game feature information. When an unambiguous MFG recommendation exists, the manager can use it as an SM86 default. Global and per-game overrides take precedence.
- Configure `dlssg_sm86.ini` and `OptiScaler.ini` in a data-driven editor with search, key settings, all INI sections and expert entries in `Section|Key=Value` format.
- Settings inherit in the order **mod defaults → global app template → per-game profile**. The manager stores templates and overrides in its data folder so they survive mod removal and updates.

### Backups and recovery

- Native DLL replacement saves the original as a sibling `.bak` when needed. Further swaps preserve the original backup.
- Restore individual DLLs or group eligible backups by game. File changes use a transaction journal with SHA-256 checks, staged originals and rollback for unfinished operations. Conflicting external changes can block rollback.
- Managed SM86 installs support versioned rollback. Use the relevant mod or DLL action for the files you want to recover.

### Driver previews

Choose **Alle Versionen & Übersicht…** from the driver rollback menu. In **Treiber-Übersicht & Rollback**, hover over a WHQL or Hotfix row to see its release notes.

- **Kompakt (Summary)** is the default. It shows game and feature highlights and up to three fixes.
- **Detailliert (Release-Notes mit Badges)** shows games, features and driver status as badges. It lists up to four fixes and shows how many more are available.

Choose the style in **Einstellungen → Treiber & Cache → Treiber-Vorschau (Hover-Card)** or switch between **Kompakt** and **Details** directly in the dialog. Both controls share one preference, which is saved across restarts.

### NVIDIA tools and system tray

- Read or change existing NVIDIA application profiles through **Profi-Tweaks & Presets**. The game-specific DLSS indicator applies when launching directly from the manager. The global indicator uses the NVIDIA registry setting.
- The dark tray menu opens the app or settings, scans games, syncs the catalog and databases, checks for app updates and opens the data/backup location. Minimize to the tray or launch with `--minimized`. Windows autostart is optional.
- **Werkzeuge** (Tools) includes a GitHub release check and diagnostic export. Install manager updates from a release package manually.

## Build from source

Run these PowerShell 7 commands from the separate C# source checkout containing `DLSSManager.slnx`. Install a compatible .NET SDK, with `10.0.100` as the `global.json` baseline. The scripts use `%LocalAppData%\DLSSManagerDev\dotnet\dotnet.exe` if present, otherwise `dotnet` on PATH.

```powershell
.\build.ps1
.\src\DLSSManager.App\bin\Release\net10.0-windows\DLSSManager.App.exe
```

`build.ps1` builds the solution and runs the automated test executable. To create and verify the self-contained Windows x64 package, use a version with no existing package in `artifacts`:

```powershell
$version = '0.1.0-preview.9'
.\publish.ps1 -Version $version
.\verify-release.ps1 -Package ".\artifacts\DLSS-Manager-CSharp-win-x64-$version.zip"
```

The publish script writes the ZIP and its SHA-256 sidecar under `artifacts` and the unpackaged app under `artifacts\win-x64`.

The Windows frontend in `src/DLSSManager.App` uses WPF, XAML and MVVM, with WinForms for the notification icon and menu. `src/DLSSManager.Core` implements scanning, catalogs, mod profiles and file transactions. `tests/DLSSManager.Tests` exercises those services using temporary fixtures.

## Troubleshooting

Include the manager version, Windows build, GPU and driver, game, affected DLL or mod version, steps to reproduce and a relevant diagnostic excerpt. Remove personal paths, user names and other private data before posting.

## Data sources and credits

- [AutoHotkey](https://www.autohotkey.com/) and [Ahk2Exe](https://github.com/AutoHotkey/Ahk2Exe) — the scripting language, Windows interface and build tool for the earlier AutoHotkey edition.
- [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) — the managed FrameGen mod.
- [beeradmoore/dlss-swapper](https://github.com/beeradmoore/dlss-swapper) and [DLSS Swapper Manifest Builder](https://github.com/beeradmoore/dlss-swapper-manifest-builder) — DLL version information and download references. DLSS Manager uses the catalog independently of the DLSS Swapper app.
- [JZ6/DLSSdb](https://github.com/JZ6/DLSSdb) — the RTX/DLSS database information used by the manager.
- [OptiScaler](https://github.com/optiscaler/OptiScaler) — managed upscaling and frame-generation mod.

Third-party DLLs, mods, and data sources remain subject to the terms of their respective rights holders and projects.

[Documentation for the earlier AutoHotkey edition](https://github.com/MrSchwafel/DLSS-Manager/blob/0b5072243014602e5047eb1bcccd747ad0c5d6c7/README.md).
