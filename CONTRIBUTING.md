# Contributing to Mod Hub

Mod Hub is developed and maintained by **VirtuOzZ**. It is an open project: bug reports, fixes, new
features and translations from anyone are welcome. VirtuOzZ reviews and merges pull requests and keeps
the overall direction of the project.

## Reporting a bug

Open an [issue](../../issues) with:

- what you did, what you expected and what happened;
- GTA V Legacy or Enhanced, and the game version;
- the Mod Hub build number (Settings, the line under the title);
- the crash report from `Mod Hub by VirtuOzZ\Data\Logs` if Mod Hub showed an unexpected error.

## Sending a pull request

1. Fork the repository and create a branch from `main`.
2. Read the [developer guide](docs/developer/developer-guide.en.md): where Mod Hub may write, how game
   changes go through transactions (`gmm.exe`), and how the interface is localized.
3. Keep the rules of the project:
   - every change to the game is an undoable transaction; Mod Hub never edits original Rockstar files;
   - Mod Hub writes for itself only through `AppPaths` and `GameInstall.WorkDir`;
   - every user-facing string goes through `L.T(...)` with entries in `ru.json`, `en.json` and `uk.json`;
   - code comments in English; format with `dotnet csharpier format .`;
   - add or update tests in `tests/GTAModManager.Core.Tests`.
4. Run `dotnet test GTAModManager.slnx` — all tests must pass.
5. Open the pull request with a short description of what changed and why.

Third-party mods and tools are never added to the repository: Mod Hub downloads them from their authors'
pages. Please respect their licences.

