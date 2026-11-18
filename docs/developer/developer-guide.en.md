# Mod Hub: developer guide

This guide explains how Mod Hub works inside: what it is made of, where and how it writes files, how it installs and undoes mods, and why it is built this way. It is for anyone who wants to study the code, check what the program does to the game, or contribute.

Source code: `src`, tests: `tests`, native components: `native`. The same text is in the repository under `docs/developer`.

## Core principles

- **Rockstar's original files are never modified.** Everything related to RPF archives and `update\` is written to copies in the `mods\` folder (as OpenIV does). Protected game files and folders are listed in `GameInstall` and cannot be overwritten or deleted.
- **Every change to the game is a transaction.** First a plan, then staged copies, then a journal, then files are replaced one by one with hash checks. A failure or a power cut in the middle of an install is rolled back on the next start.
- **Every install can be undone.** Before a file is replaced, its previous content is kept: in the service folder next to the game and, if enabled, in the backup library.
- **No game keys are distributed.** RPF decryption keys are found in the exe of your own copy of the game and stay on your computer.
- **The program is portable.** It keeps everything of its own in its own folder; deleting the folder removes the program.
- **The window never writes to the game folder itself.** All game changes are done by the console tool `gmm.exe`, which relaunches itself with administrator rights (UAC) when needed.

## Projects

- `GTAModManager.Core` — all logic: the RPF7 format, keys, the OIV interpreter, mod archive analysis, transactions, backups, hotkeys, the recommendations catalog, NaturalVision, Add-On PEDs, localization. No WPF dependency.
- `GTAModManager.Cli` — the `gmm.exe` tool. It runs every operation that changes the game. The window starts it as a separate process and reads its output.
- `GTAModManager.App` — the WPF application (`ModHub.exe`): user interface, embedded browser (WebView2), install queues.
- `GTAModManager.Probe` — a helper for inspecting archives during development.
- `tests/GTAModManager.Core.Tests` — xUnit tests for Core (several hundred, including transactions, RPF, OIV, undo and migrations).
- `tests/GTAModManager.Localization.UiHarness` — checks that the UI renders correctly in every language.
- `native/GMMPedSelector` — Mod Hub's own ASI character selector menu for Legacy.
- `native/ModHubLauncher` — the small `Mod Hub.exe` launcher in the program folder that starts `Program\ModHub.exe`.

## Where Mod Hub keeps its data

All program paths are defined in one class: `Core/AppPaths.cs`. Writing anywhere else around it is not allowed, and the `AppPathsTests` test enforces this.

```
Mod Hub by VirtuOzZ\
  Mod Hub.exe      launcher
  Settings.json    settings
  Languages\       user translations
  Program\         ModHub.exe, gmm.exe, .NET and libraries
  Data\
    Game keys\     key cache from the game exe and magic.dat
    Browser\       embedded browser profile
    Downloads\     archives downloaded in the embedded browser
    Cache\         data that can be rebuilt (mod pages, previews)
    Library\       records of installed mods and user choices
    Logs\          reports of unexpected errors (crash-*.txt, the latest 20)
    Temp\          temporary files; emptied on exit
```

Next to `Program\ModHub.exe` there is a `ModHub.portable` marker with the line `root=..`: it says the program folder is one level up. Without the marker (a developer build) the exe folder is the program folder. If the program folder is not writable (for example Program Files), data goes to Documents\ModHub.

Temporary files: `ModHub.exe` and `gmm.exe` point the `TEMP`/`TMP` variables to `Data\Temp` at startup, so child processes do not write to the system `%TEMP%` either.

Apart from the program folder Mod Hub writes only:

- **to the game folder** — the mods, loaders and ASI plugins themselves;
- **to the service folder on the game's drive** `X:\ModHub\Legacy` or `X:\ModHub\Enhanced` (`GameInstall.WorkDir`): transaction journals (`journals`), staged copies (`tx`, `prep`), switched-off mods (`disabled-mods`), what Restore vanilla game took out (`vanilla-restore`), the hotkey cache (`keybinds`). It must be on the same volume as the game: then installing and undoing are instant renames, not copies of gigabytes;
- **to the backup folder** chosen by the user (default `X:\ModHub\Backups`);
- **to the registry** `HKCU\Software\Classes` — only if the user turns on opening `.oiv` files with a double click (`App/FileAssociation.cs`).

Documents\Rockstar Games is only read: the launcher log (whether the game was launched, whether BattlEye is off) and the `UserMods` folder.

## Finding the game

`GameDetector` reads the registry and manifests of Rockstar Games Launcher, Steam and Epic Games — read-only. The edition is told by the exe: `GTA5_Enhanced.exe` means Enhanced, `GTA5.exe` means Legacy. The path can also be set by hand.

## Keys and RPF archives

- `Crypto/Gta5Keys` finds the decryption keys in the game exe by SHA-1 hashes (as CodeWalker does; the hashes are in `Gta5KeyHashes.cs`). Found keys are cached in `Data\Game keys` by the exe's size and modification time, so the 50 MB exe is not read on every start.
- `Crypto/MagicDat` is a table file from the open-source CodeWalker project. If it is missing, it is downloaded from the official repository and verified by hash. It is useless without the game exe.
- `Rpf/Rpf7Archive` reads RPF7, `Rpf/Rpf7Writer` writes it. Archive copies in `mods\` are written with an unencrypted table of contents, as OpenIV does. The source archive is never changed.

## Installing mods

1. **Analysis** (`Mods/ModAnalyzer`, `Mods/ModArchive`). An archive (ZIP, RAR, 7Z) or an OIV package is examined: is it an add-on DLC, an ASI plugin, scripts, file replacements, an OIV inside an archive, "pick one" variants (`Instructions/Variants`). The author's README is used as a hint, never as a source of commands.
2. **Plan.** For OIV packages `Oiv/OivInstaller` turns package commands into a list of file changes without touching the game. Changes inside archives are prepared in memory (`Oiv/ArchiveSession`), and the previous content of every file is recorded for targeted undo. Text and XML edits (`Oiv/TextEdits`) keep the BOM and line endings.
3. **Path rules** (`GameInstall`): everything related to RPF and `update\` goes to `mods\`; ASI, `scripts\` and other loose files go to the game root. Originals are protected.
4. **Add-on DLC** is registered in `dlclist.xml` with a minimal edit (`Dlc/DlcList`): existing lines are never rewritten. If NaturalVision Enhanced's onigiri loader is installed, the registration is duplicated into its copies of the files (`Oiv/OnigiriOverlay`), otherwise the game would not see the pack.
5. **The transaction** (see below) applies the plan.
6. **A record** of the install goes to the journal, to My Mods and, if enabled, to the backup library.

A queue of several mods is approved with a single UAC prompt (`Installation/BatchChannel`, `DropBatch`).

## Transactions

`Transactions/FileTransactionEngine` applies a set of changes inside one folder (the game root):

1. Checks: write access, free space, expected "before" hashes of the files.
2. Staging: new files are copied to `WorkDir\tx\<id>` — on the same volume, so they can be moved instantly later.
3. A write-ahead journal (`WorkDir\journals\<id>.json`): before each file changes, the journal records what is about to happen.
4. Replacing one file at a time: the old file is set aside as a backup, the new one is put in place, its hash is verified.
5. A failure at any step or a crashed process is rolled back from the journal on the next start (`Recover`). If the state is ambiguous (the file was changed by someone else), the transaction is marked `NeedsIntervention` and nothing is overwritten blindly.

`Undo` first checks that the installed files have not changed since the install; otherwise it touches nothing. "Remove" on a mod card (`Inventory/ModRemoval`) undoes every install of that mod, newest first, and restores even entries inside shared archives such as `update.rpf` without touching other mods.

A cross-process lock per game folder (`Game/GameOperationGate`) prevents two operations from changing the game at the same time.

## Backups

`Library/BackupLibrary` is an optional library in a folder chosen by the user. It keeps only changed files (whole files and single files inside RPFs), stored by SHA-256, so identical content is kept once. It has a size limit; old backups are never deleted automatically — only content no active install refers to is freed. `Library/VanillaOracle` tells whether a file's previous content is a game original by comparing it with the same file in the unmodified archive.

The backup folder also holds `Mod Hub settings` (`Library/SettingsMirror`): a copy of `Settings.json`, `Data\Library` and translations. It is refreshed on every settings save and on exit. If the same folder is chosen after a reinstall, the program offers to restore the settings.

## Switching mods off, clean game

- `Game/ModModeService` switches mods off without deleting them: the loaders (Script Hook V, ScriptHookVDotNet, ASI and `mods` folder loaders) are renamed in place to `*.gmm-off`; switching on restores the names.
- `Game/VanillaRestore` removes everything Rockstar did not ship from the game folder: `mods\`, ASI/DLL/scripts in the root, mod (unencrypted) archives in `update\` and `x64\`, switched-off loaders. What is taken out is either deleted or moved to `WorkDir\vanilla-restore`. Original files are not touched.
- `Game/GamePermissions` checks with a real write attempt whether the user can write to the game folder and, if needed, fixes the permissions in a separate process with UAC.
- `Game/GameReadiness` only reads the launcher log: whether the game was launched at least once and whether BattlEye is off.
- `Game/GameLauncher` starts the game through the official launcher, not by running the exe directly.

## Mod hotkeys

`Keybinds/KeybindScanner` looks for key bindings only in allowed places: the game root (`*.ini`, `modhub.json`), `scripts\` and `plugins\` (up to 4 levels deep). The `mods\` folder, RPF archives and game files are never read. The INI, JSON and XML parsers recognize keys strictly; doubtful values need confirmation. A change (`KeybindEditor`) is written as a regular transaction with a targeted line edit that keeps the formatting; JSON and XML are read-only for now. `KeybindConflictAnalyzer` finds overlaps, including with the game's default keys.

Any key can be switched off (`KeyNotation.FormatDisableToken`): where the mod's own "empty" value is known it is written (`None`, `0,0,0,0`, code `0`), otherwise F24, which keyboards do not have; such a value reads back as "disabled". `Keybinds/KeybindSwitches` knows mods' master switches for all their hotkeys as documented by their authors (Simple Trainer: `KeyBindingsDisabled`). `Keybinds/Gamepad` finds gamepad buttons: mods store a GTA control index in their INI (`GamepadControls`), not the button itself; the screen names it for an Xbox or PlayStation layout (`AppSettings.GamepadLayout`).

## Recommendations and downloads

- The recommendations catalog is `Discover/catalog.json`; covers are in `App/Discover/Assets`. Dynamic data (rating, downloads, author) is read from GTA5-Mods pages (`Discover/Gta5ModsPageParser`): only what the site publishes for machines (schema.org JSON-LD, og tags, counters). User comments are not read.
- Downloading (`Discover/Gta5ModsDownloads`) follows the path of a regular visitor: mod page → Download button → file from `files.gta5-mods.com`, with the right Referer, so the download counts for the author. Only https and only mod archives are accepted.
- The embedded browser (WebView2) opens https only; archives downloaded in it go to `Data\Downloads`. The user's Downloads folder is never listed or cleaned.
- Modding essentials (`Base/*`): Mod Hub does not ship the RageOpenV `mods` folder loader (by martonp96 and Chiheb-Bacha); it is downloaded from the authors' official GitHub release, pinned to a reviewed version and checked by SHA-256 (`Base/RageOpenVDownloads.cs`). Script Hook V is downloaded from the dev-c.com page the same way a visitor's click does; ScriptHookVDotNet from GitHub releases.
- "Support the author" (`Discover/AuthorThanks`) offers once to open the mod page after the mod has been tried in game. The program itself sends nothing.

## NaturalVision and LCPP

`NaturalVision/NveService` installs selected NaturalVision Enhanced components from an archive the user downloaded, taking the onigiri loader into account. LCPP components are installed as separate transactions of one install, so they are undone piece by piece in the right order.

The onigiri loader (NaturalVision Enhanced) does not read `mods\`: it mounts its own folder over the game devices (`onigiri\common` = `common:/`, `onigiri\platform` = `platform:/`, i.e. `update.rpfd` and the `x64*.rpf` archives, `onigiri\dlcpacks` = `dlcpacks:/`). So with onigiri `GameInstall.CheckModLoader` reports it as the loader, and installing NVE removes OpenRPF/RageOpenV in the same transaction (`OivInstaller.PrepareLoaderHandOff`). After every installation and undo `Oiv/OnigiriLayer` recomputes loose copies of everything mods changed in the `mods\` archives (a file, or the first nested archive as a whole) and lists them in `ModHubState\onigiri-layer.json`. Such files are marked derived in the record (`FileBackup.Derived`): undo recomputes them instead of restoring them. Foreign files in `onigiri\` (NVE's own included) are never overwritten; the plan shows a warning instead. Add-on DLC, dlclist and pools are still handled by `Oiv/ModCompatibility`.

Limits (`Installation/LimitSetup`): either the classic set (HeapAdjuster, the Packfile/Modkit/StaticBounds adjusters and a gameconfig; with NVE its onigiri gameconfig is enough) or Resource Extensions (`ResourceExtensions.asi`), which replaces all of that and does not support a custom gameconfig. With Resource Extensions no adjusters are offered, and a mixed or doubled setup is shown in the yellow banner.

## Add-On PEDs (Legacy only)

`Peds/*` prepares a regular OIV transaction: a pack with the layout of the classic Ped Selector (`pedselector`); the character list lives inside the pack (`peds.meta`). Mod Hub's own ASI selector (`native/GMMPedSelector`) is optional; third-party selectors are only detected. The selector writes saved character looks to `GMMPedSelector\looks.txt` in the game folder; it keeps no log.

`Peds/PedSelectorImport` brings characters back from a saved Ped Selector pack: `peds.rpf` from `pedselector\dlc.rpf` is unpacked into a folder and goes through the usual queue; the pack's registrations are not taken over, the gender is chosen again.

## Localization

Texts are in JSON packs in `Core/Localization/Languages` (ru is the source language, plus en and uk); official packs are embedded in the build. English is selected on first start. User packs go to the `Languages` folder and are picked up without a restart; missing strings come from the official pack of the same language, then from Russian. Format and rules: `docs/localization.md`. `OfficialLanguageCoverageTests` requires en and uk to contain every key.

## The gmm.exe tool

`gmm` without arguments prints the list of commands. The main ones: `info`, `oiv`, `mod`, `history`, `undo`, `mods-status`, `mods-disable`, `mods-enable`, `vanilla-restore`, `keybinds`, `keybind-set`, `peds-*`, `base-install`, `backup-setup`, `backup-info`. Commands that change the game relaunch `gmm` with UAC; output comes back through a log file in the program's temp folder (`Processes/LiveLogRelay`). The window calls the same commands.

## Removing the program

Settings → "Remove Mod Hub…" (`App/MainWindow.Uninstall.cs`): depending on the choice it restores clean games or removes only Mod Hub's own components, keeps or deletes the backups together with the service folder, removes the `.oiv` association, and deletes the program folder after exit with `cmd.exe` (no temporary scripts).

## Building and checking

Requirements: Windows 10/11 x64, .NET SDK 10 (version in `global.json`). Native components need Visual Studio with C++ and CMake; the application itself does not, since ready binaries are in the repository.

```
dotnet build GTAModManager.slnx -c Release
dotnet test GTAModManager.slnx
.\scripts\publish-portable.ps1        # tests → publish → build\ModHub-portable.zip
.\scripts\check-portable-writes.ps1   # the built release writes nothing outside its folder
.\scripts\build-addonpeds.ps1         # rebuild GMMPedSelector.asi
```

## Rules for changes

- Code comments are in English; UI text goes only through localization (`L.T("key")`), with en and uk translations.
- New program files only through `AppPaths`; game service data only in `GameInstall.WorkDir`.
- Any change to the game folder only through `FileTransactionEngine` and `gmm.exe`, never directly from the window.
- Rockstar's original files are never overwritten or deleted.
- New logic comes with tests in `GTAModManager.Core.Tests`.
