using GTAModManager.Core.Game;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Library;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

/// <summary>Gamepad buttons written as GTA control indices, and mods' own master switches for their hotkeys.</summary>
public sealed class KeybindPadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-kbpad-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "GTA V Enhanced");
    private BackupLibrary? _lib;
    private BackupLibrary Lib => _lib ??= BackupLibrary.Create(Path.Combine(_root, "backups"), 1L << 30);

    // The N.O.S. 1.9 settings file (abridged): a keyboard key and a gamepad button as a GTA control.
    private const string Nos = """
        [Controls]
        ; Nitro key (CapsLock, LShiftKey, X, ...).
        NitroKey=CapsLock
        NitroMode=Hold

        [Gamepad]
        Enabled=true
        ; Nitro button as a GTA control id (default 73). Menu on a gamepad: hold L3 + R3.
        NitroButton=73

        """;

    private const string TrainerV = """
        [KeyBindings]
        MenuKey=114		//Enter Menu, Default F3
        ControllerEnter=201		// Default A
        ControllerUp=188		// Default Dpad Up
        [Defaults]
        KeyBindingsDisabled=0	//Key Bindings (except menu keys) disabled on startup

        """;

    public KeybindPadTests()
    {
        Directory.CreateDirectory(Path.Combine(Game, "scripts"));
        File.WriteAllText(Path.Combine(Game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(Game, "OpenRPF.asi"), "MZ stub");
        File.WriteAllText(Path.Combine(Game, "scripts", "N.O.S - Nitro Overdrive System.ini"), Nos);
        File.WriteAllText(Path.Combine(Game, "trainerv.ini"), TrainerV);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private GameInstall Open()
    {
        var g = GameInstall.Open(Game);
        g.RunningCheckOverride = () => false;
        return g;
    }

    [Fact]
    public void Gamepad_buttons_are_found_and_shown_for_both_layouts()
    {
        var scan = new KeybindScanner(Open()).Scan();
        var nitro = scan.Pads.Single(p => p.Key == "NitroButton");
        Assert.Equal(73, nitro.Control);
        var button = GamepadControls.Find(nitro.Control)!.Button;
        Assert.Equal("A", GamepadControls.Label(button, GamepadLayout.Xbox));
        Assert.Equal("✕ Cross", GamepadControls.Label(button, GamepadLayout.PlayStation));

        Assert.Equal(["ControllerEnter", "ControllerUp", "NitroButton"], scan.Pads.Select(p => p.Key).Order());
        Assert.DoesNotContain(scan.Pads, p => p.Key == "Enabled");
        // The keyboard side still sees the keyboard keys only.
        Assert.DoesNotContain(scan.Entries, e => e.Params.Any(p => p.Name.StartsWith("Controller")));
    }

    [Fact]
    public void Gamepad_button_is_changed_and_undone()
    {
        var g = Open();
        var id = new KeybindScanner(g).Scan().Pads.Single(p => p.Key == "NitroButton").Id;
        var r = GamepadEditor.Apply(g, Lib, id, 86); // horn: L3 / LS
        Assert.Equal(TxState.Completed, r.State);
        var path = Path.Combine(Game, "scripts", "N.O.S - Nitro Overdrive System.ini");
        Assert.Contains("NitroButton=86", File.ReadAllText(path));
        Assert.Contains("NitroKey=CapsLock", File.ReadAllText(path));

        Assert.Equal(TxState.Rejected, GamepadEditor.Apply(g, Lib, id, 9999).State);

        Assert.Equal(TxState.Undone, new Oiv.OivInstaller(g).Undo(r.Id, Lib).State);
        Assert.Equal(Nos, File.ReadAllText(path));
    }

    [Fact]
    public void Disable_survives_the_trip_through_gmm_text()
    {
        // The window sends the empty chord to gmm as text ("—"); it must read back as "disabled", not as unparsable.
        var text = new KeyChord().ToString();
        var parsed = KeyChord.Parse(text);
        Assert.NotNull(parsed);
        Assert.True(parsed.Value.IsEmpty);

        File.WriteAllText(
            Path.Combine(Game, "ScriptHookVDotNet.ini"),
            "ReloadKeyBinding=Insert\nConsoleKeyBinding=F4\n"
        );
        var g = Open();
        var id = new KeybindScanner(g).Scan().Entries.Single(e => e.Params[0].Name == "ReloadKeyBinding").Id;
        Assert.Equal(TxState.Completed, KeybindEditor.Apply(g, Lib, id, parsed.Value).State);
        // F24 is a valid SHVDN "Keys" name that no keyboard can press.
        Assert.Equal(
            "ReloadKeyBinding=F24\nConsoleKeyBinding=F4\n",
            File.ReadAllText(Path.Combine(Game, "ScriptHookVDotNet.ini"))
        );
    }

    [Fact]
    public void Simple_trainer_master_switch_turns_its_hotkeys_off_and_on()
    {
        var g = Open();
        var sw = Assert.Single(new KeybindScanner(g).Scan().Switches);
        Assert.Equal("trainerv.ini", sw.File);
        Assert.True(sw.HotkeysOn);

        Assert.Equal(TxState.Completed, KeybindSwitches.Apply(g, Lib, "trainerv.ini", on: false).State);
        var text = File.ReadAllText(Path.Combine(Game, "trainerv.ini"));
        Assert.Contains("KeyBindingsDisabled=1\t//Key Bindings", text);
        Assert.False(new KeybindScanner(g).Scan().Switches.Single().HotkeysOn);
        Assert.Equal(TxState.Rejected, KeybindSwitches.Apply(g, Lib, "trainerv.ini", on: false).State);

        Assert.Equal(TxState.Completed, KeybindSwitches.Apply(g, Lib, "trainerv.ini", on: true).State);
        Assert.Equal(TrainerV, File.ReadAllText(Path.Combine(Game, "trainerv.ini")));
    }
}
