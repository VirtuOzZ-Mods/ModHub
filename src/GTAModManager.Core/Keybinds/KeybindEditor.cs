using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Keybinds;

public sealed record ParamChange(string Param, string Old, string New);

/// <summary>Rebinding preview: whether it is possible, what changes in the file and what it may overlap with.</summary>
public sealed record EditPreview(
    bool Allowed,
    string? Error,
    List<ParamChange> Changes,
    List<KeybindConflict> Conflicts
);

/// <summary>
/// Safe INI hotkey rebinding: supported settings only, written through a transaction with a ModHub backup
/// (appears in History and can be undone); only the value characters on the target lines change,
/// encoding and BOM are preserved; the file is re-read after writing and the change is rolled back on any mismatch.
/// </summary>
public static class KeybindEditor
{
    public static EditPreview Preview(KeybindEntry e, KeyChord newChord, IReadOnlyList<KeybindEntry> all)
    {
        var none = new List<ParamChange>();
        if (
            e.File.Replace('/', '\\').Equals("GMMPedSelector.ini", StringComparison.OrdinalIgnoreCase)
            && !newChord.IsEmpty
            && (newChord.Mods != KeyMods.None || newChord.Keys.Count() != 1 || !PedMenuKey(newChord.Vk))
        )
            return new(false, L.T("core.keybinds.gmmAddonpedsChooseSingleKey"), none, []);
        if (e.Format != KeybindFormat.Ini)
            return new(false, e.EditBlockReason ?? L.T("core.keybinds.formatCannotModifiedYet"), none, []);
        if (!e.Editable)
            return new(false, e.EditBlockReason ?? L.T("core.keybinds.bindingCannotChangedSafely"), none, []);
        if (newChord == e.Chord)
            return new(
                false,
                newChord.IsEmpty
                    ? L.T("core.keybinds.keyAlreadyDisabled")
                    : L.T("core.keybinds.shortcutAlreadyAssigned"),
                none,
                []
            );

        var changes = new List<ParamChange>();
        foreach (var p in e.Params)
        {
            // When disabling, the modifier is left alone: disabling the key itself makes a modifier meaningless.
            if (newChord.IsEmpty && p.Role == ParamRole.Modifier)
            {
                changes.Add(new ParamChange(p.Name, p.Raw, p.Raw));
                continue;
            }
            var (value, error) = newChord.IsEmpty
                ? KeyNotation.FormatDisableToken(p)
                : KeyNotation.Format(newChord, p, e.Chord);
            if (value is null)
                return new(false, error, none, []);
            changes.Add(new ParamChange(p.Name, p.Raw, value));
        }
        // Verify that the written value reads back as the same shortcut.
        if (!RoundTrips(e, changes, newChord))
            return new(false, L.T("core.keybinds.combinationCannotSavedModS"), none, []);

        var others = all.Where(x => x.Id != e.Id).ToList();
        var updated = e with { Chord = newChord };
        var conflicts = KeybindConflictAnalyzer
            .Analyze([.. others, updated])
            .Where(c => c.A.Id == e.Id || c.B.Id == e.Id)
            .ToList();
        return new(true, null, changes, conflicts);
    }

    private static bool RoundTrips(KeybindEntry e, List<ParamChange> changes, KeyChord expected)
    {
        if (expected.IsEmpty)
        {
            // When disabling, the modifier is ignored (its value is not touched); the key(s) must become unassigned.
            return e
                .Params.Zip(changes)
                .Where(x => x.First.Role != ParamRole.Modifier)
                .All(x =>
                    KeyNotation.IsUnassigned(x.Second.New)
                    || KeyNotation.ParseValue(x.Second.New) is { } r && KeyNotation.IsDisabledMarker(r.Chord)
                );
        }
        var vks = new List<int>();
        var mods = KeyMods.None;
        for (int i = 0; i < e.Params.Count; i++)
        {
            var p = e.Params[i];
            if (p.Role == ParamRole.Modifier)
            {
                if (changes[i].New.Equals("None", StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = KeyNames.ParseModifier(changes[i].New);
                if (m is null)
                    return false;
                mods |= m.Value.mod;
                continue;
            }
            var r = KeyNotation.ParseValue(changes[i].New);
            if (r is null)
                return false;
            if (p.Role is ParamRole.First or ParamRole.Second)
                vks.Add(r.Chord.Vk);
            else
            {
                vks.AddRange(r.Chord.Keys);
                mods |= r.Chord.Mods;
            }
        }
        var chord = e.Params.Any(p => p.Role is ParamRole.First or ParamRole.Second)
            ? KeyNotation.FromKeys(vks)
            : new KeyChord(vks[0], mods, default, vks.Count > 1 ? vks[1] : 0);
        return chord is { } c && c.Mods == expected.Mods && c.Keys.Order().SequenceEqual(expected.Keys.Order());
    }

    /// <summary>
    /// Writes the change (runs elevated through gmm). Requires backups to be enabled.
    /// Returns the transaction result; Completed means changed and verified by re-reading.
    /// </summary>
    public static TxResult Apply(GameInstall game, BackupLibrary? library, string entryId, KeyChord newChord)
    {
        if (library is null)
            return new("", TxState.Rejected, L.T("core.keybinds.enableBackupsSettingsChangeHotkeys"));
        if (game.IsRunning())
            return new("", TxState.Rejected, L.T("core.keybinds.gameRunningChangesProhibitedClose"));

        var scanner = new KeybindScanner(game, library);
        var scan = scanner.Scan();
        var e = scan.Entries.FirstOrDefault(x => x.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (e is null)
            return new("", TxState.Rejected, L.T("core.keybinds.bindingNotFoundFileMay"));
        var preview = Preview(e, newChord, scan.Entries);
        if (!preview.Allowed)
            return new("", TxState.Rejected, preview.Error ?? L.T("core.keybinds.cannotModify"));

        var full = Path.Combine(game.Root, e.File);
        var before = KeybindScanner.ParseFile(e.File, KeybindScanner.ReadText(full));
        var arrowText = newChord.IsEmpty ? L.T("core.keybinds.disabled") : newChord.ToString();
        var r = WriteValues(
            game,
            library,
            e.File,
            e.Params.Select((p, i) => (p.Line, p.Name, p.Raw, preview.Changes[i].New)).ToList(),
            L.T("core.keybinds.hotkey", e.ModName, e.Action, e.Chord, arrowText),
            afterText =>
            {
                // Re-read check: the new value is in place and the file's other bindings are unchanged.
                var after = KeybindScanner.ParseFile(e.File, afterText);
                var updated = after.FirstOrDefault(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
                bool othersSame = before
                    .Where(x => x.Id != e.Id)
                    .Select(x => (x.Id, x.Chord))
                    .SequenceEqual(after.Where(x => x.Id != e.Id).Select(x => (x.Id, x.Chord)));
                return updated is not null
                    && updated.Chord.Mods == newChord.Mods
                    && updated.Chord.Keys.Order().SequenceEqual(newChord.Keys.Order())
                    && othersSame;
            }
        );
        return r.State == TxState.Completed
            ? r with
            {
                Message = L.T("core.keybinds.canUndoHistory", e.Action, e.Chord, arrowText),
            }
            : r;
    }

    /// <summary>
    /// Replaces values in an INI file of the game folder as one undoable transaction, keeping the encoding, BOM and
    /// every other byte. <paramref name="verify"/> re-reads the written text; on false the change is undone.
    /// </summary>
    internal static TxResult WriteValues(
        GameInstall game,
        BackupLibrary library,
        string rel,
        IReadOnlyList<(int Line, string Key, string Old, string New)> values,
        string title,
        Func<string, bool> verify
    )
    {
        var full = Path.Combine(game.Root, rel);
        var bytes = File.ReadAllBytes(full);
        var preHash = FileTransactionEngine.Hash(full);
        var (text, encoding) = KeybindScanner.DecodeText(bytes);
        var doc = IniDocument.Parse(text);
        foreach (var (line, key, old, value) in values)
        {
            var v = doc.Values.FirstOrDefault(x => x.Line == line);
            if (v is null || !v.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || v.Value != old)
                return new("", TxState.Rejected, L.T("core.keybinds.lineChangedAfterScanningRefresh", key, rel));
            doc = doc.WithValue(line, value);
        }
        var newText = doc.ToString();
        var newBytes =
            encoding.GetPreamble().Length > 0 && HasPreamble(bytes, encoding)
                ? [.. encoding.GetPreamble(), .. encoding.GetBytes(newText)]
                : encoding.GetBytes(newText);
        if (KeybindScanner.DecodeText(newBytes).text != newText)
            return new("", TxState.Rejected, L.T("core.keybinds.newValueCannotSavedFile"));

        var installer = new OivInstaller(game);
        var plan = new InstallPlan
        {
            Title = title,
            PrepDir = Path.Combine(game.WorkDir, "prep", "keybind-" + Guid.NewGuid().ToString("N")[..8]),
        };
        using (plan)
        {
            Directory.CreateDirectory(plan.PrepDir);
            var staged = Path.Combine(plan.PrepDir, "file");
            File.WriteAllBytes(staged, newBytes);
            plan.Changes.Add(new FileChange(rel, preHash, staged, MoveSource: true));
            plan.BackupEstimate = bytes.LongLength;
            var r = installer.Install(plan, library);
            if (r.State != TxState.Completed)
                return r;
            if (!verify(KeybindScanner.ReadText(full)))
            {
                var undo = installer.Undo(r.Id, library);
                return new(
                    r.Id,
                    TxState.RolledBack,
                    L.T("core.keybinds.postWriteVerificationFailedOriginal", undo.Message)
                );
            }
            return r;
        }
    }

    private static bool HasPreamble(byte[] bytes, System.Text.Encoding enc)
    {
        var p = enc.GetPreamble();
        return bytes.Length >= p.Length && bytes.AsSpan(0, p.Length).SequenceEqual(p);
    }

    private static bool PedMenuKey(int vk) =>
        vk
            is >= 0x30
                and <= 0x39
                or >= 0x41
                and <= 0x5a
                or >= 0x70
                and <= 0x7b
                or 0x61
                or 0x63
                or 0x67
                or 0x69
                or >= 0xba
                and <= 0xc0
                or >= 0xdb
                and <= 0xdf
                or 0xe2
                or 0x20
                or 9
                or 0x1b
                or 0x13
                or 0x21
                or 0x22
                or 0x23
                or 0x24
                or 0x25
                or 0x27
                or 0x2d
                or 0x2e
                or 0x14
                or 0x91
                or 0x90
                or 0x2c;
}
