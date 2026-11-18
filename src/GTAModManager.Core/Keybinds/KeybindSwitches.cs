using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Keybinds;

/// <summary>A mod's own master switch for its hotkeys, found in its settings file.</summary>
/// <param name="HotkeysOn">The mod's hotkeys work now.</param>
public sealed record KeybindSwitch(
    string ModId,
    string ModName,
    string File,
    string Section,
    string Key,
    int Line,
    string Raw,
    bool HotkeysOn,
    string Note
);

/// <summary>
/// Known per-mod switches that turn all of a mod's hotkeys on or off at once. Only switches documented by the mod's
/// author are listed: guessing another mod's setting could break it.
/// </summary>
public static class KeybindSwitches
{
    private sealed record Known(
        string FileName,
        string Section,
        string Key,
        string OnValue,
        string OffValue,
        string NoteKey
    );

    private static readonly Known[] Catalog =
    [
        // Simple Trainer for GTA V (sjaak327): "KeyBindingsDisabled=0 //Key Bindings (except menu keys) disabled on startup".
        new("trainerv.ini", "Defaults", "KeyBindingsDisabled", "0", "1", "core.keybinds.switchTrainerV"),
    ];

    public static bool Applies(string rel) =>
        Catalog.Any(k => Path.GetFileName(rel).Equals(k.FileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Switches in one settings file (relative path), or none.</summary>
    public static IEnumerable<(string Section, string Key, int Line, string Raw, bool On, string NoteKey)> Find(
        string rel,
        string text
    )
    {
        var known = Catalog.Where(k => Path.GetFileName(rel).Equals(k.FileName, StringComparison.OrdinalIgnoreCase));
        if (!known.Any())
            yield break;
        var doc = IniDocument.Parse(text);
        foreach (var k in known)
        {
            var v = doc.Values.FirstOrDefault(x =>
                x.Section.Equals(k.Section, StringComparison.OrdinalIgnoreCase)
                && x.Key.Equals(k.Key, StringComparison.OrdinalIgnoreCase)
            );
            if (v is null)
                continue;
            var raw = v.Value.Trim();
            if (raw == k.OnValue || raw == k.OffValue)
                yield return (v.Section, v.Key, v.Line, v.Value, raw == k.OnValue, k.NoteKey);
        }
    }

    /// <summary>Writes the switch (runs elevated through gmm) as an undoable transaction.</summary>
    public static TxResult Apply(GameInstall game, BackupLibrary? library, string file, bool on)
    {
        if (library is null)
            return new("", TxState.Rejected, L.T("core.keybinds.enableBackupsSettingsChangeHotkeys"));
        if (game.IsRunning())
            return new("", TxState.Rejected, L.T("core.keybinds.gameRunningChangesProhibitedClose"));
        var scan = new KeybindScanner(game, library).Scan();
        var sw = scan.Switches.FirstOrDefault(s => s.File.Equals(file, StringComparison.OrdinalIgnoreCase));
        if (sw is null)
            return new("", TxState.Rejected, L.T("core.keybinds.bindingNotFoundFileMay"));
        if (sw.HotkeysOn == on)
            return new(
                "",
                TxState.Rejected,
                L.T(on ? "core.keybinds.switchAlreadyOn" : "core.keybinds.switchAlreadyOff")
            );
        var known = Catalog.First(k =>
            Path.GetFileName(file).Equals(k.FileName, StringComparison.OrdinalIgnoreCase)
            && k.Key.Equals(sw.Key, StringComparison.OrdinalIgnoreCase)
        );
        var title = L.T(on ? "core.keybinds.switchOnTitle" : "core.keybinds.switchOffTitle", sw.ModName);
        return KeybindEditor.WriteValues(
            game,
            library,
            file,
            [(sw.Line, sw.Key, sw.Raw, on ? known.OnValue : known.OffValue)],
            title,
            after => Find(file, after).Any(x => x.Line == sw.Line && x.On == on)
        );
    }
}
