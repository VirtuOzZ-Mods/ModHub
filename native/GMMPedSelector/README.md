# GMMPedSelector native test build

Minimal x64 ASI for **GTA V Legacy Story Mode**. It registers only in `GTA5.exe`; Enhanced and other process names are refused. This build has not been tested in a running game. No trainer features, installation, game launch, or third-party binaries are included.

Dependencies: a separately obtained, game-compatible **ScriptHookV.dll** and ASI loader. ScriptHookVDotNet is not required. The build generates its own import library from the public export declarations in `ScriptHookV.def`; no ScriptHookV SDK code, DLL, or third-party library is redistributed. The Microsoft CRT is statically linked.

Read `GMMPedSelector.models.txt` and `GMMPedSelector.ini` from the game EXE folder at script startup. Catalog rows are UTF-8 `spawn<TAB>display` (literal tab). Blank/comment lines and malformed rows are ignored; duplicate spawn names are case-insensitive. Technical model names are preserved (including parentheses, hyphens and spaces), max 128 printable ASCII bytes; path separators, Windows filename metacharacters and HUD markup are refused. Display names max 160 UTF-8 bytes; HUD formatting/control characters are removed. The file is capped at 1 MiB / 1024 accepted rows. Empty/missing catalogs show a message; the ASI creates no models or DLC entries itself. Restart the game after catalog/config changes.

`[Menu] MenuKey=L` is the default. A-Z, 0-9/D0-D9, F1-F12, non-reserved NumPad keys, Windows Forms key names (e.g. Insert, OemCloseBrackets), OEM aliases such as Oem6/Oem102, VK_F7 and hexadecimal/decimal virtual keys are accepted case-insensitively. `None`/`Disabled` disables the toggle. Modifier chords are unsupported and disable the toggle instead of silently becoming plain keys; the manager should offer single keys only. Unknown/reserved saved bindings disable the toggle and write a diagnostic; missing configuration defaults to L. Navigation/apply/close keys are reserved. Holding a key does not repeat actions. L (or configured key, shown in the HUD) toggles, Up/Down or NumPad8/2 selects, Left/Right or NumPad4/6 change a value on the customization page, Enter/NumPad5 applies, Backspace/NumPad0 closes. With NumLock off Windows can report different virtual keys; arrows remain available. Focus loss closes the menu and cancels loading. Held keys cannot reopen it on focus regain.

While open (including opening and closing frames), only the game actions bound to the menu keys are disabled each frame: phone, frontend navigation/accept/cancel/pause, and aircraft/submarine controls on the NumPad. Walking, sprinting, jumping, the camera and driving (WASD, Shift, Space, mouse) keep working. Win32 key polling remains available to the menu. After closing, gameplay is restored on the next frame; only standard phone/frontend cancel/pause actions (177/194/199/200/202) remain blocked while Backspace/NumPad0 is held. Releasing the close key removes that last block. Custom gameplay mappings and other plugins reading raw keyboard input cannot be intercepted by GTA's control natives. Opening an already open phone is not necessary to use this menu.

Model changes require Story Mode, a live/playing local player and a valid ped model in the game image. Requests time out after five seconds or cancel on closing/focus loss/unsafe state. The player ped is reacquired after changing its model, default components are set, and the model reference is released. Online/session-active and dead/not-playing states close the menu and refuse model changes. The selector writes no log file. This does not prove compatibility with future game/ScriptHookV versions or arbitrary ped assets.

## Customize (PED SELECTOR · MOD HUB ADD-ON)

The first row of the list, `Customize current ped >`, opens a page for the current player ped: every component slot that has more than one option for this model (head, hair, torso, legs, shoes, accessories, top…) and the props it has (hat, glasses, ears, watch, bracelet). Left/Right picks the previous/next part (combinations the model does not have are skipped; props include `none`), Enter cycles the colour. `Save look` stores the outfit for this model in `GMMPedSelector\looks.txt` (game folder) and it is applied automatically every time this ped is spawned from the menu. `Reset to default` restores the author's default look (default components, no props) and forgets the saved look. `< Back` returns to the list. ModHub creates the `GMMPedSelector` folder and grants users write access to that folder only when it installs or updates the menu; the rest of the game folder stays read-only. Without it Save reports that the menu must be reinstalled in ModHub.

## Build and automated tests

Run with CMake 3.24+ and Visual Studio 18 2026 x64 tools:

```powershell
cmake -S . -B build -G "Visual Studio 18 2026" -A x64
cmake --build build --config Release
ctest --test-dir build -C Release --output-on-failure
```

Binary: `build/Release/GMMPedSelector.asi`. Tests: `build/Release/selector_tests.exe`. Portable catalog/INI/key policy tests can be built on other C++20 systems with `-DBUILD_ASI=OFF`. Tests exercise real parser/input policy behavior; they do not execute GTA natives.

## Manual acceptance tests (not yet performed)

1. In a backed-up offline Legacy setup with compatible dependencies, test a known working installed ped. Verify L opens black/orange menu and holding L does not flicker it.
2. Test Up/Down, NumPad8/2, Enter and NumPad5. Up must not open the phone. While open, movement, firing and vehicle controls must not run.
3. Close separately with Backspace, NumPad0 and toggle. Hold the close key, release it, then verify normal gameplay/phone/pause controls immediately work. Repeat with custom menu key and NumLock settings.
4. Apply a valid ped: confirm appearance/default components. Missing or non-ped model must report refusal without changing the player; unloading/missing assets must stop loading within five seconds.
5. Close or alt-tab during loading: request cancels, menu stays closed on focus regain until a fresh key press. Test while dead/respawning: no change is applied. Verify no registration in Enhanced (no native script calls).
6. Test empty, malformed, duplicate and UTF-8 catalogs; review the log. Do not use save-game persistence or Online play to validate this experimental build.

