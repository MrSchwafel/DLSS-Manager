# DLSS Manager user guide

[Back to the README](../../README.md) · [Deutsch](usage.de.md)

## Package verification

If a SHA-256 file accompanies the ZIP, run the following command in PowerShell with your package path and compare the result with the hash in that file.

    Get-FileHash -Algorithm SHA256 .\DLSS-Manager-CSharp-win-x64-1.0.0.zip

## Requirements

- Windows x64. The C# development build has been tested on Windows 11.
- The self-contained package includes the .NET Desktop Runtime. Framework-dependent builds require the x64 .NET 10 Desktop Runtime.
- Write access to the app's data folder and to the game files you choose to change. The global DLSS indicator also needs permission to write to NVIDIA's machine-wide registry key.
- Internet access for catalog, DLSSdb, mod and release checks or downloads. Local catalog files can be used offline. OptiScaler downloads provided as 7-Zip archives use Windows `tar.exe`.
- A compatible game, GPU and driver for the selected feature or mod. Consult [SM86](https://github.com/sdli1995/dlssg_for_sm86) and [OptiScaler](https://github.com/optiscaler/OptiScaler) for their compatibility details.

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

## Troubleshooting

Include the manager version, Windows build, GPU and driver, game, affected DLL or mod version, steps to reproduce and a relevant diagnostic excerpt. Remove personal paths, user names and other private data before posting.

## Repository layout

| Path | Contents |
| --- | --- |
| [src/DLSSManager.App](../../src/DLSSManager.App/) | WPF windows, views, view models and system tray |
| [src/DLSSManager.Core](../../src/DLSSManager.Core/) | Game discovery, DLL catalogs, mod profiles and file transactions |
| [docs/guides](.) | English and German user guides |
| [docs/screenshots](../screenshots/) | Current C# app screenshots for both READMEs |
| [resources/branding](../../resources/branding/) | Logo, icons and favicon |
| [resources/legacy/autohotkey](../../resources/legacy/autohotkey/) | Language packs and DLL preload script from the earlier AutoHotkey edition |
| [build.ps1](../../build.ps1), [publish.ps1](../../publish.ps1), [verify-release.ps1](../../verify-release.ps1) | Build, Windows x64 packaging and package verification |

The build scripts prefer the SDK under %LocalAppData%\DLSSManagerDev\dotnet and fall back to the dotnet command on PATH. Publishing writes the ZIP and SHA-256 file under artifacts and the unpackaged app under artifacts\win-x64.
