# Mod Hub by VirtuOzZ

A mod manager for GTA V **Legacy** and **Enhanced** on Windows. Its goal is to make GTA modding easier for
newcomers: what veterans do by hand from each mod's instructions, Mod Hub tries to do for you.

> **Version 0.4 beta.** This is a beta: bugs are possible, please report them in [Issues](../../issues).
> Mod Hub is developed by one person, VirtuOzZ, and it is open: fixes, new features and translations are
> welcome as pull requests (see [Contributing](#contributing)).
>
> **Important:** Mod Hub has only been tested on clean games. Using it on GTA V or GTA V Enhanced that already
> has mods installed (by hand, with OpenIV, etc.) has not been tested yet. Back up your game before using it.

## Features

- **Mod installation** from OIV packages and regular archives (ZIP, RAR, 7Z): add-on DLC, ASI plugins, scripts.
- **The `mods\` folder**: original game files are never changed; mods are loaded through a loader.
- **Modding essentials in one click**: Script Hook V, the `mods` folder loader (RageOpenV), ScriptHookVDotNet and
  the limit adjusters (HeapAdjuster, Packfile / Modkit / StaticBounds Limit Adjuster); Resource Extensions is recognised.
- **History and undo**: every installation is a transaction with a backup and can be undone.
- **Back to a clean game**: removes all mods and their traces without touching Rockstar's original files.
- **Mod catalog** with Legacy / Enhanced compatibility.
- **Add-On peds** for Legacy with a character selector; characters can be brought back from an old Ped Selector folder.
- **NaturalVision Evolved / Enhanced and LCPP**: installs the components you choose from an archive you downloaded
  yourself. On Enhanced, Mod Hub installs NVE's ASI loader and mirrors `mods\` into the onigiri folder so NVE and
  your other mods work together.
- **Mod hotkeys**: keyboard and gamepad (Xbox or PlayStation labels), conflict search, remapping and switching any
  key off.
- **Engine sounds** for add-on cars: a table of cars, one sound for several cars at once.
- **Turn mods off and on** without deleting them.
- **Interface** in English, Russian and Ukrainian.
- **No installation**: the program and all its data live in one folder; nothing is written to AppData.

The game is detected automatically (Rockstar Games Launcher, Steam, Epic Games).

## Download and run

1. Download `ModHub-0.4-beta.zip` from [Releases](../../releases).
2. Unpack it to the root of the system drive, which gives `C:\Mod Hub by VirtuOzZ`. That folder is the installed
   program. Do not unpack it into the game folder, into Program Files (it cannot be written to without administrator
   rights) or into folders synced by OneDrive.
3. Run `C:\Mod Hub by VirtuOzZ\Mod Hub.exe`. There is no need to install .NET, everything is included.
   The build is not code-signed: if Windows SmartScreen warns, click "More info" → "Run anyway".

Requirements: Windows 10/11 x64, GTA V Legacy or Enhanced. The catalog's built-in browser needs the Microsoft Edge
WebView2 Runtime (Windows 11 already has it).

### Is it safe?

Mod Hub is an `.exe`, so it is fair to ask. Here is how you can check it instead of taking anyone's word for it:

- **The code is open.** Everything Mod Hub does is in this repository, under the MIT license.
- **Release archives are built by GitHub, not on a developer's PC.** The
  [release workflow](.github/workflows/release.yml) checks out the tagged source on GitHub's servers, runs all the
  tests, builds the archive and attaches it to the release together with a signed
  [build provenance attestation](../../attestations). To check a downloaded archive (with the GitHub CLI):
  `gh attestation verify ModHub-0.4-beta.zip --repo VirtuOzZ-Mods/ModHub`. The release notes also list its SHA-256.
- **VirusTotal.** Each release links a VirusTotal report of its archive.
- **Why it asks for administrator rights.** Rockstar's launcher locks the game folder, so writing mods there needs
  the UAC prompt. Only the small `gmm.exe` helper runs elevated, and only to change the game folder; the window
  itself runs as a normal user.
- **What it does not do.** No telemetry, no background services, no autostart, no drivers. It never edits Rockstar's
  original files and never ships game files, keys or other people's programs — Script Hook V, RageOpenV,
  ScriptHookVDotNet and the adjusters are downloaded from their authors' official pages when you install them.
  The internet is used only for the catalog and for the downloads you start.
- **Not code-signed yet**, so Windows SmartScreen may warn about an unknown publisher.

### Known limitation

GTA V Enhanced only loads an ASI loader named `xinput1_4.dll` (it never loads `dinput8.dll`). Mod Hub currently
takes it from NaturalVision Enhanced; without NVE, put your own Enhanced ASI loader as `xinput1_4.dll` for .asi mods
and scripts to run.

## Where Mod Hub keeps its data

Mod Hub installs nothing into the system. Everything it writes for itself stays in its own folder:

```
Mod Hub by VirtuOzZ\
  Mod Hub.exe      launcher
  README EN.txt    short instructions (README RU.txt in Russian)
  Settings.json    settings (created on first run)
  Languages\       your own interface translations
  Program\         program files and .NET
  Data\            game keys, cache, built-in browser, downloads, temporary files
```

Besides its own folder, Mod Hub writes only:

- **into the game folder**: the mods, loaders and ASI plugins you install;
- **into a `ModHub` folder in the root of the game's drive** (`X:\ModHub\Legacy`, `X:\ModHub\Enhanced`): the install
  journal, disabled mods and undo copies. It must be on the same drive as the game, so installs and undo are instant
  renames instead of copying gigabytes. Backups go there by default as well (`X:\ModHub\Backups`); the folder can be
  changed;
- **into the registry** (`HKCU\Software\Classes`): only if you turn on opening `.oiv` files with a double click.

The "Documents\Rockstar Games" folder is only read. If the program folder is not writable (for example, unpacked into
Program Files), data goes to "Documents\ModHub".

## Uninstalling

> The "Uninstall Mod Hub" button in the settings does not work yet. To remove Mod Hub, simply delete its folder
> (`C:\Mod Hub by VirtuOzZ`) as described below.

1. If you want the game back in its original state, click "Restore vanilla game" in Mod Hub (or undo the installs you
   want in "History and undo").
2. If you turned on opening `.oiv` with a double click, turn it off in the settings.
3. Delete the `Mod Hub by VirtuOzZ` folder.
4. Delete the `ModHub` folder in the root of the game's drive. Only after step 1: it holds the undo copies and
   disabled mods.

## Game keys

RPF archives are encrypted. Mod Hub **does not contain** any encryption keys or game files: keys are found in the
executable of your own copy of the game (the way CodeWalker does it) and stay only on your computer. On first use it
downloads RageOpenV (the authors' official GitHub release, checked by SHA-256), Script Hook V (dev-c.com),
ScriptHookVDotNet (GitHub) and `magic.dat` from the CodeWalker project (GitHub). Mod Hub does not redistribute
third-party programs: they are always downloaded from their authors' official pages.

## Building from source

You need:
- Windows 10/11 x64;
- [.NET SDK 10](https://dotnet.microsoft.com/download) (the version is pinned in `global.json`);
- for the native components (`native/`): Visual Studio 2022 with C++ and CMake. They are not needed to build the
  application itself: the prebuilt `.asi` and launcher are in the repository.

```powershell
dotnet build GTAModManager.slnx -c Release
dotnet test GTAModManager.slnx
```

A build for users (tests → publish → `build\ModHub-portable.zip`):

```powershell
.\scripts\publish-portable.ps1
```

## Repository layout

| Folder | Contents |
|---|---|
| `src/GTAModManager.App` | The WPF application |
| `src/GTAModManager.Core` | Logic: RPF7, OIV, transactions, backups, catalog, localization |
| `src/GTAModManager.Cli` | The `gmm` command-line tool |
| `native/ModHubLauncher` | The `Mod Hub.exe` launcher that starts `Program\ModHub.exe` |
| `native/GMMPedSelector` | The Add-On ped selector ASI plugin |
| `tests` | Tests |
| `docs` | [Developer guide](docs/developer/developer-guide.en.md), the translation system, user instructions |

## Contributing

Mod Hub is an open project led by VirtuOzZ, and anyone can help:

- found a bug: open an [issue](../../issues) with what you did, what you expected, what happened, Legacy or Enhanced,
  and the build number from Mod Hub's settings;
- want to fix or add something: send a pull request. Read the [developer guide](docs/developer/developer-guide.en.md)
  and [CONTRIBUTING.md](CONTRIBUTING.md) first;
- interface translations: see [docs/localization.md](docs/localization.md).

## License

Mod Hub code is [MIT](LICENSE). Third-party components (the Inter font and others) and the programs Mod Hub downloads
during installation (RageOpenV — GPL-3.0, Script Hook V, ScriptHookVDotNet) keep their own licenses — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Not affiliated with Rockstar Games or Take-Two Interactive. Single player only — do not use mods in GTA Online.
