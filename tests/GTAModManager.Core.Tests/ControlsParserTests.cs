using GTAModManager.Core.Mods;

namespace GTAModManager.Core.Tests;

public class ControlsParserTests
{
    // Excerpt of the T&P Recorder README (in the author's format).
    private const string Sample = """
        Some intro text about the mod.

        BASIC CONTROLS

        Menu (arrow keys and NumPad both work)
        ]                    Open / close the menu
        Up / Down (Num8/2)      Select an item
        Enter (Num5)            Confirm
        Right (Num6)            Open a submenu
        Left / Right (Num4/6)   Change a value
        Backspace (Num0)        Back
        Ctrl+] or Num -         Hide / show the mod interface

        Takes
        Enter                   Start the take from the starting grid
        Backspace               Cancel take preparation
        K                       Autopilot on / off

        Director Preview
        Space                   Pause / resume
        Left Mouse Button       Seek on the timeline
        PageUp / Home / End     Orbit pivot / reset orbit / game camera
        R, then F1              Prepare a Rockstar Editor clip, then start / stop it
        Backspace               Exit preview

        Free camera: W/A/S/D move, Q/E down and up, mouse to look.
        LMB removes a background object, Ctrl+Z restores it.
        """;

    [Fact]
    public void Groups_rows_and_notes_are_recognized()
    {
        var groups = ControlsParser.Parse(Sample);
        Assert.Equal(
            ["Menu (arrow keys and NumPad both work)", "Takes", "Director Preview"],
            groups.Select(g => g.Title)
        );
        Assert.Equal(7, groups[0].Rows.Count);
        Assert.Equal(3, groups[1].Rows.Count);
        Assert.Equal(5, groups[2].Rows.Count);
        Assert.Contains(groups[2].Notes, n => n.StartsWith("Free camera"));
        Assert.DoesNotContain(groups.SelectMany(g => g.Rows), r => r.Action.Contains("intro"));
    }

    [Fact]
    public void Key_tokens_are_split_into_keys_joiners_and_hints()
    {
        var row = ControlsParser.TryParseRow("Up / Down (Num8/2)      Select an item")!;
        Assert.Equal(
            [KeyTokenKind.Key, KeyTokenKind.Joiner, KeyTokenKind.Key, KeyTokenKind.Hint],
            row.Keys.Select(k => k.Kind)
        );
        Assert.Equal("Select an item", row.Action);

        var combo = ControlsParser.TryParseRow("Ctrl+] or Num -         Hide / show the mod interface")!;
        Assert.Equal(["Ctrl", "+", "]", "or", "Num -"], combo.Keys.Select(k => k.Text));

        var mouse = ControlsParser.TryParseRow("Left Mouse Button       Seek on the timeline")!;
        Assert.Equal("Left Mouse Button", mouse.Keys.Single().Text);
    }

    [Theory]
    [InlineData("This mod adds    a lot of features")]
    [InlineData("Requires  ScriptHookV")]
    [InlineData("Version 1.1  — bug fixes")]
    public void Ordinary_text_is_not_mistaken_for_controls(string line) =>
        Assert.Null(ControlsParser.TryParseRow(line));

    [Fact]
    public void Too_few_rows_give_no_controls_tab()
    {
        Assert.Empty(ControlsParser.Parse("Install:\nF5     open menu\n\nThanks!"));
    }

    [Fact]
    public void Russian_controls_are_recognized()
    {
        var groups = ControlsParser.Parse(
            """
            Управление
            F5          Открыть меню
            Пробел      Нитро
            ЛКМ         Выстрел
            """
        );
        Assert.Equal(3, groups.Single().Rows.Count);
    }
}
