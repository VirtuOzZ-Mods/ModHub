Mod Hub by VirtuOzZ — a mod manager for GTA V (Legacy and Enhanced). Version 0.4 beta.

IMPORTANT: Mod Hub has only been tested on clean games. Using it on GTA V or
GTA V Enhanced that already has mods installed (by hand, with OpenIV, etc.) has
not been tested yet. Back up your game before using it.

INSTALLATION
1. Unpack the archive straight to the root of the system drive C:\ — you get
   C:\Mod Hub by VirtuOzZ. That folder is the installed program.
   Do not unpack it into the game folder, into Program Files (it cannot be written
   to without administrator rights) or into folders synced by OneDrive (Desktop,
   Documents): game keys and the built-in browser cache would end up there.
2. Run "Mod Hub.exe". There is no need to install .NET — everything is included.
   If Windows shows "Windows protected your PC":
   "More info" → "Run anyway" (the build is not signed).
3. The game is found automatically (Rockstar Launcher, Steam, Epic). If not,
   use "Specify game folder manually…".
4. The "Mod installation" page has "Modding essentials": Script Hook V, the mods folder
   loader and ScriptHookVDotNet are installed with one button.

WHAT IS WHERE
Mod Hub installs nothing into Windows and does not write to AppData.
  Mod Hub by VirtuOzZ\
    Mod Hub.exe      — starts the program
    README EN.txt    — this file (README RU.txt — the same in Russian)
    Settings.json    — settings (appears after the first start)
    Languages\       — your own interface translations
    Program\         — program files, no need to touch them
    Data\            — game keys, cache, built-in browser, downloads, temporary files,
                       error reports (Data\Logs)
Next to the game, on the same drive, Mod Hub creates a ModHub folder
(for example D:\ModHub\Legacy or D:\ModHub\Enhanced): the install journal, switched-off
mods and copies for undo. It has to be on the game's drive so installing and undoing
are instant. Backups are kept there by default too (D:\ModHub\Backups).
Mod Hub writes to the Windows registry only if you turn on opening .oiv files with
a double click (Settings).

REMOVAL
Easiest: Settings → "Remove Mod Hub…" — it can restore a clean game, keep or delete
the backups and removes the program folder by itself. By hand:
1. If you want the game back in its original state, press "Restore vanilla game" in
   Mod Hub (or undo the installs in "History and undo").
2. If you turned on opening .oiv with a double click, turn it off in Settings.
3. Delete the "Mod Hub by VirtuOzZ" folder.
4. Delete the ModHub folder in the root of the game's drive (journal, switched-off mods
   and backups). Only after step 1: otherwise installed mods can no longer be undone.

Internet is needed: on the first install Script Hook V (dev-c.com), ScriptHookVDotNet
(GitHub) and the magic.dat key file from the CodeWalker project (GitHub) are downloaded.
Close the game before installing mods. Writing to the game folder needs administrator
confirmation (UAC).

Found a bug? Send a screenshot and what you did before it. If Mod Hub showed an
unexpected error message, attach the file from the Data\Logs folder.
