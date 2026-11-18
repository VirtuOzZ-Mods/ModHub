using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Library;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public class KeyNotationTests
{
    [Theory]
    [InlineData("F5", 0x74, KeyMods.None)]
    [InlineData("VK_F5", 0x74, KeyMods.None)]
    [InlineData("0x74", 0x74, KeyMods.None)]
    [InlineData("NumPad7", 0x67, KeyMods.None)]
    [InlineData("Numpad0", 0x60, KeyMods.None)]
    [InlineData("VK_NUMPAD3", 0x63, KeyMods.None)]
    [InlineData("D7", 0x37, KeyMods.None)]
    [InlineData("Oem6", 0xDD, KeyMods.None)]
    [InlineData("Return", 0x0D, KeyMods.None)]
    [InlineData("Control+R", 'R', KeyMods.Ctrl)]
    [InlineData("Ctrl+Shift+N", 'N', KeyMods.Ctrl | KeyMods.Shift)]
    [InlineData("ctrl + alt + f12", 0x7B, KeyMods.Ctrl | KeyMods.Alt)]
    [InlineData("F5, Control", 0x74, KeyMods.Ctrl)]
    [InlineData("45,0,0,0", 0x2D, KeyMods.None)]
    [InlineData("36,1,1,0", 0x24, KeyMods.Ctrl | KeyMods.Shift)]
    public void Strict_notations_are_recognized(string raw, int vk, KeyMods mods)
    {
        var r = KeyNotation.ParseValue(raw)!;
        Assert.True(r.Strict);
        Assert.Equal(vk, r.Chord.Vk);
        Assert.Equal(mods, r.Chord.Mods);
    }

    [Fact]
    public void Numpad_and_digit_row_are_different_keys()
    {
        Assert.Equal(0x37, KeyNotation.ParseValue("D7")!.Chord.Vk);
        // A bare "7": either the key 7 or the code 7; it cannot be decided unambiguously, so it is rejected.
        Assert.Null(KeyNotation.ParseValue("7"));
        Assert.Equal(0x67, KeyNotation.ParseValue("NumPad7")!.Chord.Vk);
        Assert.NotEqual(KeyNotation.ParseValue("NumPad7")!.Chord, KeyNotation.ParseValue("D7")!.Chord);
    }

    [Fact]
    public void Bare_number_is_only_a_candidate()
    {
        var r = KeyNotation.ParseValue("116")!;
        Assert.False(r.Strict);
        Assert.Equal(0x74, r.Chord.Vk);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("Enabled")]
    [InlineData("Hello world")]
    [InlineData("1.5")]
    [InlineData("Ctrl+Banana")]
    public void Non_keys_are_rejected(string raw) => Assert.Null(KeyNotation.ParseValue(raw));
}

public sealed class KeybindIniTests
{
    private static readonly ModRef Mod = new("mod", "Mod");

    [Fact]
    public void Ordinary_ini_parameters_and_comments()
    {
        const string ini = """
            [Controls]
            ; MenuKey=F9  — закомментировано, не назначение
            MenuKey = Oem6
            NitroKey=CapsLock ; клавиша нитро
            UseKeyboard=true
            KeyCount=5
            Control=1
            ControllerEnter=201
            NitroButton=73
            OverlayKey=0,0,0,0
            EmptyKey=
            """;
        var e = IniKeybindParser.Parse(ini, "scripts\\Mod.ini", Mod);
        Assert.Equal(["MenuKey", "NitroKey", "OverlayKey", "EmptyKey"], e.Select(x => x.Params[0].Name));
        Assert.Equal(0xDD, e[0].Chord.Vk);
        Assert.Equal(0x14, e[1].Chord.Vk);
        Assert.All(e, x => Assert.True(x.Editable));
        // OverlayKey=0,0,0,0 and EmptyKey= are explicitly disabled keys (not silently skipped).
        Assert.True(e[2].Chord.IsEmpty);
        Assert.Equal(KeybindStatus.Disabled, e[2].Status);
        Assert.True(e[3].Chord.IsEmpty);
        Assert.Equal(KeybindStatus.Disabled, e[3].Status);
    }

    [Fact]
    public void Decimal_code_needs_confirmation_unless_author_comment_matches()
    {
        const string ini = "[K]\nMenuKey=114\t\t//Enter Menu, Default F3\nOtherKey=79\nWrongKey=115 // Default F9\n";
        var e = IniKeybindParser.Parse(ini, "trainer.ini", Mod).ToDictionary(x => x.Params[0].Name);
        Assert.Equal(KeybindStatus.Recognized, e["MenuKey"].Status);
        Assert.Equal("Enter Menu", e["MenuKey"].Action);
        Assert.Equal(new KeyChord(0x72), e["MenuKey"].Default);
        Assert.Equal(KeybindStatus.NeedsConfirmation, e["OtherKey"].Status);
        Assert.Equal(KeybindStatus.NeedsConfirmation, e["WrongKey"].Status); // the comment says F9, but the code is F4
        Assert.False(e["OtherKey"].Editable);
    }

    [Fact]
    public void Key_and_modifier_params_form_one_chord()
    {
        var e = IniKeybindParser.Parse("[Controls]\nMenuKey=NumPad0\nMenuModifier=Control\n", "nos.ini", Mod).Single();
        Assert.Equal(new KeyChord(0x60, KeyMods.Ctrl), e.Chord);
        Assert.Equal([ParamRole.Key, ParamRole.Modifier], e.Params.Select(p => p.Role));
    }

    [Fact]
    public void Numbered_pairs_form_two_key_chords()
    {
        const string ini =
            "[K]\nGodKeyOn1=163 //Default RCTRL\nGodKeyOn2=116 //Default F5\nEPMKey1=74 //Default J\nEPMKey2=97 //Default Num 1\nEPMKey3=74 //Default J\nEPMKey4=98 //Default Num 2\n";
        var e = IniKeybindParser.Parse(ini, "t.ini", Mod);
        Assert.Contains(e, x => x.Chord == new KeyChord(0x74, KeyMods.Ctrl, KeySide.RCtrl));
        Assert.Contains(e, x => x.Chord == new KeyChord('J', Second: 0x61));
        Assert.Contains(e, x => x.Chord == new KeyChord('J', Second: 0x62));
    }

    [Fact]
    public void Duplicate_parameter_is_not_guessed()
    {
        var e = IniKeybindParser.Parse(
            "[K]\nCruiseControl1=71 //Default G\nCruiseControl1=55 //Default 7\nHelpKey=XX see table\n",
            "t.ini",
            Mod
        );
        Assert.Equal(2, e.Count);
        Assert.All(e, x =>
        {
            Assert.Equal(KeybindStatus.NeedsConfirmation, x.Status);
            Assert.False(x.Editable);
        });
    }
}

public class KeybindConflictTests
{
    private static KeybindEntry E(
        string mod,
        KeyChord chord,
        KeybindContext ctx = KeybindContext.Unknown,
        string action = "A"
    ) =>
        new()
        {
            Id = $"{mod}|{action}|{chord}",
            ModId = mod,
            ModName = mod,
            Action = action,
            Chord = chord,
            Context = ctx,
            Format = KeybindFormat.Ini,
            Status = KeybindStatus.Recognized,
            Editable = true,
        };

    [Fact]
    public void Same_hotkey_in_two_mods_with_unknown_context_is_potential()
    {
        var c = KeybindConflictAnalyzer.Analyze([E("a", new(0x74)), E("b", new(0x74))]).Single();
        Assert.Equal(ConflictKind.Potential, c.Kind);
    }

    [Fact]
    public void Same_hotkey_in_same_known_context_or_global_is_direct()
    {
        Assert.Equal(
            ConflictKind.Direct,
            KeybindConflictAnalyzer
                .Analyze([E("a", new(0x74), KeybindContext.Vehicle), E("b", new(0x74), KeybindContext.Vehicle)])
                .Single()
                .Kind
        );
        Assert.Equal(
            ConflictKind.Direct,
            KeybindConflictAnalyzer
                .Analyze([E("a", new(0x74), KeybindContext.Global), E("b", new(0x74), KeybindContext.OnFoot)])
                .Single()
                .Kind
        );
    }

    [Fact]
    public void Different_known_contexts_do_not_conflict() =>
        Assert.Empty(
            KeybindConflictAnalyzer.Analyze([
                E("a", new(0x74), KeybindContext.Vehicle),
                E("b", new(0x74), KeybindContext.OnFoot),
            ])
        );

    [Fact]
    public void E_and_Shift_E_overlap_potentially()
    {
        var c = KeybindConflictAnalyzer.Analyze([E("a", new('E')), E("b", new('E', KeyMods.Shift))]).Single();
        Assert.Equal(ConflictKind.Potential, c.Kind);
        Assert.Contains("содержит", c.Reason);
    }

    [Fact]
    public void Same_mod_is_not_compared_with_itself() =>
        Assert.Empty(KeybindConflictAnalyzer.Analyze([E("a", new('E'), action: "x"), E("a", new('E'), action: "y")]));

    [Fact]
    public void Game_default_overlap_is_only_potential()
    {
        var all = GameKeybindProvider
            .Defaults(GameEdition.Enhanced)
            .Append(E("mod", new('F'), KeybindContext.Global))
            .ToList();
        var cs = KeybindConflictAnalyzer.Analyze(all).Where(c => c.A.ModId == "mod" || c.B.ModId == "mod").ToList();
        Assert.NotEmpty(cs);
        Assert.All(cs, c => Assert.Equal(ConflictKind.Potential, c.Kind));
    }

    [Fact]
    public void Suggestions_skip_used_keys()
    {
        var s = KeybindConflictAnalyzer.SuggestFree([E("a", new(0x74)), E("b", new(0x75))], allowModifiers: false);
        Assert.DoesNotContain(s, c => c.Vk is 0x74 or 0x75);
        Assert.NotEmpty(s);
    }
}

/// <summary>Scanner and safe writing on a synthetic game.</summary>
public sealed class KeybindScannerEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-kb-" + Guid.NewGuid().ToString("N"));

    private string Game(string edition) => Path.Combine(_root, "GTA V " + edition);

    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    public KeybindScannerEditorTests()
    {
        foreach (var ed in new[] { "Enhanced", "Legacy" })
        {
            Directory.CreateDirectory(Path.Combine(Game(ed), "scripts"));
            File.WriteAllText(Path.Combine(Game(ed), ed == "Enhanced" ? "GTA5_Enhanced.exe" : "GTA5.exe"), "stub");
            File.WriteAllText(Path.Combine(Game(ed), ed == "Enhanced" ? "OpenRPF.asi" : "OpenIV.asi"), "MZ stub");
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
        // WorkDir is shared with other installations/tests; never delete it from one fixture.
    }

    private GameInstall Open(string ed = "Enhanced")
    {
        var g = GameInstall.Open(Game(ed));
        g.RunningCheckOverride = () => false;
        return g;
    }

    private void Write(string rel, string text, Encoding? enc = null, string ed = "Enhanced")
    {
        var p = Path.Combine(Game(ed), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(
            p,
            (enc ?? new UTF8Encoding(false))
                .GetPreamble()
                .Concat((enc ?? new UTF8Encoding(false)).GetBytes(text))
                .ToArray()
        );
    }

    [Fact]
    public void Scan_finds_mods_json_xml_and_manifest_only_in_allowed_places()
    {
        Write(@"scripts\Speedo\Settings.ini", "[Keys]\nToggleKey=F7\n");
        Write(@"scripts\Radio\config.json", "{ \"controls\": { \"nextStationKey\": \"PageUp\", \"volume\": 5 } }");
        Write(@"scripts\Cam\Cam.xml", "<Config><MenuKey>F9</MenuKey><Key name=\"x\" /></Config>");
        Write("SomeTool.ini", "[Hotkeys]\nOpenKey=Ctrl+F2\n");
        Write(@"mods\update\fake.ini", "[K]\nMenuKey=F11\n"); // mods\ is not scanned
        Write(
            @"scripts\Speedo\modhub.json",
            """{ "id": "speedo", "name": "Speedometer Pro", "keybinds": [ { "action": "Показать спидометр", "file": "Settings.ini", "section": "Keys", "key": "ToggleKey", "default": "F7", "context": "vehicle" } ] }"""
        );

        var r = new KeybindScanner(Open()).Scan();
        var byFile = r.Entries.ToLookup(e => e.File);
        Assert.Equal("Speedometer Pro", byFile[@"scripts\Speedo\Settings.ini"].Single().ModName);
        Assert.Equal(KeybindContext.Vehicle, byFile[@"scripts\Speedo\Settings.ini"].Single().Context);
        Assert.True(byFile[@"scripts\Speedo\Settings.ini"].Single().FromManifest);
        Assert.Equal(0x21, byFile[@"scripts\Radio\config.json"].Single().Chord.Vk);
        Assert.False(byFile[@"scripts\Radio\config.json"].Single().Editable);
        Assert.Equal(0x78, byFile[@"scripts\Cam\Cam.xml"].Single().Chord.Vk);
        Assert.Equal(new KeyChord(0x71, KeyMods.Ctrl), byFile["SomeTool.ini"].Single().Chord);
        Assert.Empty(byFile[@"mods\update\fake.ini"]);
    }

    [Fact]
    public void Settings_files_installed_by_a_package_outside_scripts_are_scanned_under_its_name()
    {
        // An OIV package that puts its .ini in its own folder in the game root (not scripts\ or plugins\).
        var source = Path.Combine(_root, "NOS.ini");
        File.WriteAllText(source, "[Controls]\nNitroKey=X\n");
        var g = Open();
        var tx = g.CreateEngine()
            .Execute([new FileChange(@"NOS\config\NOS.ini", null, source)], "NOS Nitrous Oxide 1.9.5");
        Assert.Equal(Transactions.TxState.Completed, tx.State);
        Write(@"Unrelated\other.ini", "[K]\nMenuKey=F11\n"); // not installed by Mod Hub, not in a scanned folder

        var r = new KeybindScanner(g).Scan();

        var nitro = Assert.Single(r.Entries, e => e.File == @"NOS\config\NOS.ini");
        Assert.Equal("NOS Nitrous Oxide", nitro.ModName);
        Assert.DoesNotContain(r.Entries, e => e.File == @"Unrelated\other.ini");
    }

    [Fact]
    public void Cache_is_used_for_unchanged_files_and_editions_are_separate()
    {
        Write(@"scripts\A.ini", "[K]\nMenuKey=F5\n");
        Write(@"scripts\A.ini", "[K]\nMenuKey=F6\n", ed: "Legacy");
        var enh = new KeybindScanner(Open("Enhanced"));
        var leg = new KeybindScanner(Open("Legacy"));
        Assert.Equal(1, enh.Scan().FilesScanned);
        var again = enh.Scan();
        Assert.Equal(0, again.FilesScanned);
        Assert.Equal(1, again.FilesFromCache);
        Assert.NotEqual(enh.StoreDir, leg.StoreDir);
        Assert.Equal(0x75, leg.Scan().Entries.Single().Chord.Vk);
        Assert.Equal(0x74, enh.Scan().Entries.Single().Chord.Vk);
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        for (int i = 0; i < 50; i++)
            Write($@"scripts\M{i}\s.ini", "[K]\nMenuKey=F5\n");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new KeybindScanner(Open()).ScanAsync(cts.Token));
    }

    [Fact]
    public void Confirmation_makes_decimal_entry_editable_without_touching_the_file()
    {
        Write("trainer.ini", "[K]\nMenuKey=114\n");
        var before = File.ReadAllBytes(Path.Combine(Game("Enhanced"), "trainer.ini"));
        var scanner = new KeybindScanner(Open());
        var e = scanner.Scan().Entries.Single();
        Assert.Equal(KeybindStatus.NeedsConfirmation, e.Status);
        scanner.Confirm(e);
        var after = scanner.Scan().Entries.Single();
        Assert.Equal(KeybindStatus.Confirmed, after.Status);
        Assert.True(after.Editable);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(Game("Enhanced"), "trainer.ini")));
    }

    [Fact]
    public void Edit_changes_only_the_value_preserves_comments_and_utf8_bom_and_undoes()
    {
        const string text =
            "; Настройки мода — комментарий\r\n[Controls]\r\nMenuKey = F5   ; меню\r\nOther=keep me\r\n";
        Write(@"scripts\Mod.ini", text, new UTF8Encoding(true));
        var path = Path.Combine(Game("Enhanced"), @"scripts\Mod.ini");
        var original = File.ReadAllBytes(path);

        var r = KeybindEditor.Apply(Open(), Lib, @"scripts\Mod.ini|Controls|MenuKey", new KeyChord(0x75));
        Assert.Equal(TxState.Completed, r.State);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal(text.Replace("MenuKey = F5", "MenuKey = F6"), Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));

        Assert.Equal(TxState.Undone, new OivInstaller(Open()).Undo(r.Id, Lib).State);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Edit_preserves_cp1251_encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        const string text = "[Клавиши]\nМеню=1\nMenuKey=NumPad0 ; клавиша меню\n";
        Write("Old.ini", text, cp1251);
        var r = KeybindEditor.Apply(Open(), Lib, "Old.ini|Клавиши|MenuKey", new KeyChord(0x67));
        Assert.Equal(TxState.Completed, r.State);
        Assert.Equal(
            text.Replace("NumPad0", "NumPad7"),
            cp1251.GetString(File.ReadAllBytes(Path.Combine(Game("Enhanced"), "Old.ini")))
        );
    }

    [Theory]
    [InlineData(
        "[C]\nMenuKey=NumPad0\nMenuModifier=Control\n",
        "Controls.ini|C|MenuKey+MenuModifier",
        "Shift + F7",
        "[C]\nMenuKey=F7\nMenuModifier=Shift\n"
    )]
    [InlineData(
        "[K]\nGodKeyOn1=163\nGodKeyOn2=116\n",
        "Controls.ini|K|GodKeyOn1+GodKeyOn2",
        "RCtrl + F6",
        "[K]\nGodKeyOn1=163\nGodKeyOn2=117\n"
    )]
    [InlineData(
        "[INPUT]\nKeyOverlay=45,0,0,0\n",
        "Controls.ini|INPUT|KeyOverlay",
        "Ctrl + Home",
        "[INPUT]\nKeyOverlay=36,1,0,0\n"
    )]
    [InlineData("[K]\nOpenKey=VK_F2\n", "Controls.ini|K|OpenKey", "F3", "[K]\nOpenKey=VK_F3\n")]
    [InlineData(
        "[K]\nOpen = Ctrl + Shift + N\n",
        "Controls.ini|K|Open",
        "Ctrl + Alt + M",
        "[K]\nOpen = Ctrl + Alt + M\n"
    )]
    public void Edit_keeps_original_syntax(string ini, string id, string chord, string expected)
    {
        Write("Controls.ini", ini);
        var g = Open();
        var scan = new KeybindScanner(g).Scan();
        var e = scan.Entries.Single(x => x.Id == id);
        if (e.Status == KeybindStatus.NeedsConfirmation)
            new KeybindScanner(g).Confirm(e);
        var r = KeybindEditor.Apply(g, Lib, id, KeyChord.Parse(chord)!.Value);
        Assert.Equal(TxState.Completed, r.State);
        Assert.Equal(expected, File.ReadAllText(Path.Combine(Game("Enhanced"), "Controls.ini")));
    }

    [Fact]
    public void Disable_and_reenable_reshade_tuple()
    {
        Write("ReShade.ini", "[GENERAL]\nKeyOverlay=45,0,0,0\n");
        var g = Open();
        const string id = "ReShade.ini|GENERAL|KeyOverlay";
        var before = new KeybindScanner(g).Scan().Entries.Single(x => x.Id == id);
        Assert.True(before.CanDisable);

        var off = KeybindEditor.Apply(g, Lib, id, default);
        Assert.Equal(TxState.Completed, off.State);
        Assert.Equal(
            "[GENERAL]\nKeyOverlay=0,0,0,0\n",
            File.ReadAllText(Path.Combine(Game("Enhanced"), "ReShade.ini"))
        );

        var disabled = new KeybindScanner(g).Scan().Entries.Single(x => x.Id == id);
        Assert.Equal(KeybindStatus.Disabled, disabled.Status);
        Assert.True(disabled.Chord.IsEmpty);
        Assert.True(disabled.Editable);

        var on = KeybindEditor.Apply(g, Lib, id, new KeyChord(0x2D));
        Assert.Equal(TxState.Completed, on.State);
        Assert.Equal(
            "[GENERAL]\nKeyOverlay=45,0,0,0\n",
            File.ReadAllText(Path.Combine(Game("Enhanced"), "ReShade.ini"))
        );
    }

    [Fact]
    public void Disable_decimal_pair_requires_empty_evidence_in_file()
    {
        // TrainerV style: one setting is already empty, so an empty value is safe for the others too.
        Write("trainerv.ini", "[K]\nSpawn1Key1=\nSpawn1Key2=\nMenuKey=114\n");
        var g = Open();
        var scan = new KeybindScanner(g).Scan();
        var disabledPair = scan.Entries.Single(x => x.Id == "trainerv.ini|K|Spawn1Key1+Spawn1Key2");
        Assert.Equal(KeybindStatus.Disabled, disabledPair.Status);

        const string menuId = "trainerv.ini|K|MenuKey";
        var menu = scan.Entries.Single(x => x.Id == menuId);
        Assert.True(menu.CanDisable); // the file has an "empty" example, so we trust the mod's convention
        Assert.Equal(KeybindStatus.NeedsConfirmation, menu.Status); // a bare number needs confirmation first
        new KeybindScanner(g).Confirm(menu);

        var r = KeybindEditor.Apply(g, Lib, menuId, default);
        Assert.Equal(TxState.Completed, r.State);
        // Simple Trainer's own way to switch an option's key off: code 0.
        Assert.Contains("MenuKey=0\n", File.ReadAllText(Path.Combine(Game("Enhanced"), "trainerv.ini")));
        Assert.Equal(KeybindStatus.Disabled, new KeybindScanner(g).Scan().Entries.Single(x => x.Id == menuId).Status);
    }

    [Fact]
    public void Any_key_can_be_disabled_with_the_unpressable_F24_and_enabled_again()
    {
        // No "unassigned" convention is known for plain key names: Mod Hub writes F24, which no keyboard has.
        Write("A.ini", "[K]\nMenuKey=F5\n");
        var g = Open();
        const string id = "A.ini|K|MenuKey";
        Assert.True(new KeybindScanner(g).Scan().Entries.Single().CanDisable);

        Assert.Equal(TxState.Completed, KeybindEditor.Apply(g, Lib, id, default).State);
        Assert.Contains("MenuKey=F24", File.ReadAllText(Path.Combine(Game("Enhanced"), "A.ini")));
        var off = new KeybindScanner(g).Scan().Entries.Single();
        Assert.Equal(KeybindStatus.Disabled, off.Status);
        Assert.True(off.Chord.IsEmpty);

        Assert.Equal(TxState.Completed, KeybindEditor.Apply(g, Lib, id, new KeyChord(0x75)).State);
        Assert.Contains("MenuKey=F6", File.ReadAllText(Path.Combine(Game("Enhanced"), "A.ini")));
    }

    [Fact]
    public void Single_key_parameter_rejects_modifiers()
    {
        Write("A.ini", "[K]\nMenuKey=F5\n");
        var e = new KeybindScanner(Open()).Scan().Entries.Single();
        var p = KeybindEditor.Preview(e, new KeyChord(0x75, KeyMods.Ctrl), [e]);
        Assert.False(p.Allowed);
        Assert.Contains("одной клавишей", p.Error);
    }

    [Fact]
    public void Edit_is_refused_without_backups_running_game_or_write_access()
    {
        Write("A.ini", "[K]\nMenuKey=F5\n");
        var path = Path.Combine(Game("Enhanced"), "A.ini");
        var original = File.ReadAllBytes(path);
        const string id = "A.ini|K|MenuKey";

        Assert.Equal(TxState.Rejected, KeybindEditor.Apply(Open(), null, id, new KeyChord(0x75)).State);

        var running = Open();
        running.RunningCheckOverride = () => true;
        Assert.Equal(TxState.Rejected, KeybindEditor.Apply(running, Lib, id, new KeyChord(0x75)).State);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var r = KeybindEditor.Apply(Open(), Lib, id, new KeyChord(0x75));
            Assert.NotEqual(TxState.Completed, r.State);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Changed_file_after_scan_is_not_overwritten()
    {
        Write("A.ini", "[K]\nMenuKey=F5\n");
        var r = KeybindEditor.Apply(Open(), Lib, "A.ini|K|SomethingElse", new KeyChord(0x75));
        Assert.Equal(TxState.Rejected, r.State);
    }
}
