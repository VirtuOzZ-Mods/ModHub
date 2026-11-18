using System.Globalization;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Keybinds;

public enum PadButton
{
    A,
    B,
    X,
    Y,
    LB,
    RB,
    LT,
    RT,
    L3,
    R3,
    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,
    Start,
    Back,
}

public enum GamepadLayout
{
    Xbox,
    PlayStation,
}

/// <summary>A GTA V control (INPUT_*) and the gamepad button it is on by default.</summary>
public sealed record PadControl(int Id, string Name, PadButton Button, KeybindContext Context);

/// <summary>
/// GTA V controls that mods use for gamepad buttons. Mods (N.O.S., Simple Trainer…) store a control index, not a
/// physical button: the game decides which button that is, by default as listed here.
/// </summary>
public static class GamepadControls
{
    public static IReadOnlyList<PadControl> All { get; } =
    [
        new(0, "NEXT_CAMERA", PadButton.Back, KeybindContext.Global),
        new(19, "CHARACTER_WHEEL", PadButton.DpadDown, KeybindContext.OnFoot),
        new(20, "MULTIPLAYER_INFO", PadButton.DpadDown, KeybindContext.OnFoot),
        new(21, "SPRINT", PadButton.A, KeybindContext.OnFoot),
        new(22, "JUMP", PadButton.X, KeybindContext.OnFoot),
        new(23, "ENTER", PadButton.Y, KeybindContext.OnFoot),
        new(24, "ATTACK", PadButton.RT, KeybindContext.OnFoot),
        new(25, "AIM", PadButton.LT, KeybindContext.OnFoot),
        new(26, "LOOK_BEHIND", PadButton.R3, KeybindContext.OnFoot),
        new(27, "PHONE", PadButton.DpadUp, KeybindContext.OnFoot),
        new(36, "DUCK", PadButton.L3, KeybindContext.OnFoot),
        new(37, "SELECT_WEAPON", PadButton.LB, KeybindContext.OnFoot),
        new(44, "COVER", PadButton.RB, KeybindContext.OnFoot),
        new(45, "RELOAD", PadButton.B, KeybindContext.OnFoot),
        new(46, "TALK", PadButton.DpadRight, KeybindContext.OnFoot),
        new(47, "DETONATE", PadButton.DpadLeft, KeybindContext.OnFoot),
        new(51, "CONTEXT", PadButton.DpadRight, KeybindContext.OnFoot),
        new(52, "CONTEXT_SECONDARY", PadButton.DpadLeft, KeybindContext.OnFoot),
        new(71, "VEH_ACCELERATE", PadButton.RT, KeybindContext.Vehicle),
        new(72, "VEH_BRAKE", PadButton.LT, KeybindContext.Vehicle),
        new(73, "VEH_DUCK", PadButton.A, KeybindContext.Vehicle),
        new(74, "VEH_HEADLIGHT", PadButton.DpadRight, KeybindContext.Vehicle),
        new(75, "VEH_EXIT", PadButton.Y, KeybindContext.Vehicle),
        new(76, "VEH_HANDBRAKE", PadButton.RB, KeybindContext.Vehicle),
        new(79, "VEH_LOOK_BEHIND", PadButton.R3, KeybindContext.Vehicle),
        new(80, "VEH_CIN_CAM", PadButton.B, KeybindContext.Vehicle),
        new(85, "VEH_RADIO_WHEEL", PadButton.DpadLeft, KeybindContext.Vehicle),
        new(86, "VEH_HORN", PadButton.L3, KeybindContext.Vehicle),
        new(99, "VEH_SELECT_NEXT_WEAPON", PadButton.X, KeybindContext.Vehicle),
        new(101, "VEH_ROOF", PadButton.DpadRight, KeybindContext.Vehicle),
        new(172, "PHONE_UP", PadButton.DpadUp, KeybindContext.Menu),
        new(173, "PHONE_DOWN", PadButton.DpadDown, KeybindContext.Menu),
        new(174, "PHONE_LEFT", PadButton.DpadLeft, KeybindContext.Menu),
        new(175, "PHONE_RIGHT", PadButton.DpadRight, KeybindContext.Menu),
        new(176, "PHONE_SELECT", PadButton.A, KeybindContext.Menu),
        new(177, "PHONE_CANCEL", PadButton.B, KeybindContext.Menu),
        new(178, "PHONE_OPTION", PadButton.Y, KeybindContext.Menu),
        new(179, "PHONE_EXTRA_OPTION", PadButton.X, KeybindContext.Menu),
        new(187, "FRONTEND_DOWN", PadButton.DpadDown, KeybindContext.Menu),
        new(188, "FRONTEND_UP", PadButton.DpadUp, KeybindContext.Menu),
        new(189, "FRONTEND_LEFT", PadButton.DpadLeft, KeybindContext.Menu),
        new(190, "FRONTEND_RIGHT", PadButton.DpadRight, KeybindContext.Menu),
        new(199, "FRONTEND_PAUSE", PadButton.Start, KeybindContext.Menu),
        new(201, "FRONTEND_ACCEPT", PadButton.A, KeybindContext.Menu),
        new(202, "FRONTEND_CANCEL", PadButton.B, KeybindContext.Menu),
        new(203, "FRONTEND_X", PadButton.X, KeybindContext.Menu),
        new(204, "FRONTEND_Y", PadButton.Y, KeybindContext.Menu),
        new(205, "FRONTEND_LB", PadButton.LB, KeybindContext.Menu),
        new(206, "FRONTEND_RB", PadButton.RB, KeybindContext.Menu),
        new(207, "FRONTEND_LT", PadButton.LT, KeybindContext.Menu),
        new(208, "FRONTEND_RT", PadButton.RT, KeybindContext.Menu),
        new(209, "FRONTEND_LS", PadButton.L3, KeybindContext.Menu),
        new(210, "FRONTEND_RS", PadButton.R3, KeybindContext.Menu),
        new(217, "FRONTEND_SELECT", PadButton.Back, KeybindContext.Menu),
    ];

    private static readonly Dictionary<int, PadControl> ById = All.ToDictionary(c => c.Id);

    public static PadControl? Find(int id) => ById.GetValueOrDefault(id);

    /// <summary>The button's name on the chosen controller.</summary>
    public static string Label(PadButton button, GamepadLayout layout) =>
        layout == GamepadLayout.Xbox
            ? button switch
            {
                PadButton.L3 => "LS",
                PadButton.R3 => "RS",
                PadButton.DpadUp => "D-pad ↑",
                PadButton.DpadDown => "D-pad ↓",
                PadButton.DpadLeft => "D-pad ←",
                PadButton.DpadRight => "D-pad →",
                PadButton.Start => "Menu",
                PadButton.Back => "View",
                _ => button.ToString(),
            }
            : button switch
            {
                PadButton.A => "✕ Cross",
                PadButton.B => "○ Circle",
                PadButton.X => "□ Square",
                PadButton.Y => "△ Triangle",
                PadButton.LB => "L1",
                PadButton.RB => "R1",
                PadButton.LT => "L2",
                PadButton.RT => "R2",
                PadButton.L3 => "L3",
                PadButton.R3 => "R3",
                PadButton.DpadUp => "D-pad ↑",
                PadButton.DpadDown => "D-pad ↓",
                PadButton.DpadLeft => "D-pad ←",
                PadButton.DpadRight => "D-pad →",
                PadButton.Start => "Options",
                PadButton.Back => "Touchpad",
                _ => button.ToString(),
            };

    public static GamepadLayout ParseLayout(string? text) =>
        string.Equals(text, "playstation", StringComparison.OrdinalIgnoreCase)
            ? GamepadLayout.PlayStation
            : GamepadLayout.Xbox;
}

/// <summary>A gamepad button a mod reads from its settings file, written as a GTA control index.</summary>
public sealed record GamepadEntry
{
    public required string Id { get; init; }
    public required string ModId { get; init; }
    public required string ModName { get; init; }
    public required string Action { get; init; }
    public required string File { get; init; }
    public required string Section { get; init; }
    public required string Key { get; init; }
    public int Line { get; init; }
    public required string Raw { get; init; }
    public int Control { get; init; }
}

/// <summary>
/// Gamepad settings in INI files: a setting named as a button ("NitroButton", "ControllerEnter", "PadMenu"…)
/// whose value is a known GTA control index. Anything else is left alone: an unknown number could be an XInput mask,
/// a count or a colour, and changing it could break the mod.
/// </summary>
public static partial class GamepadParser
{
    private static readonly HashSet<string> PadWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "button",
        "btn",
        "controller",
        "pad",
        "gamepad",
        "joy",
        "joystick",
        "xinput",
    };

    private static readonly HashSet<string> NotButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        "enable",
        "enabled",
        "disable",
        "disabled",
        "use",
        "count",
        "delay",
        "time",
        "sensitivity",
        "deadzone",
        "vibration",
        "rumble",
        "mouse",
    };

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex Number();

    public static List<GamepadEntry> Parse(string text, string rel, ModRef mod)
    {
        var result = new List<GamepadEntry>();
        foreach (var v in IniDocument.Parse(text).Values)
        {
            var tokens = KeyParamRules.Tokens(v.Key);
            var sectionIsPad = KeyParamRules.Tokens(v.Section).Any(PadWords.Contains);
            if (
                tokens.Any(NotButtons.Contains)
                || !(tokens.Any(PadWords.Contains) || sectionIsPad && tokens.Contains("button"))
            )
                continue;
            var raw = v.Value.Trim();
            if (
                !Number().IsMatch(raw)
                || GamepadControls.Find(int.Parse(raw, CultureInfo.InvariantCulture)) is not { } control
            )
                continue;
            var comment = v.Comment is { Length: > 0 } c
                ? Regex.Replace(c, @"[,;]?\s*default.*$", "", RegexOptions.IgnoreCase).Trim()
                : "";
            result.Add(
                new GamepadEntry
                {
                    Id = $"{rel}|{v.Section}|{v.Key}",
                    ModId = mod.Id,
                    ModName = mod.Name,
                    Action = comment.Length is > 2 and < 60 ? comment : KeyParamRules.Humanize(v.Key),
                    File = rel,
                    Section = v.Section,
                    Key = v.Key,
                    Line = v.Line,
                    Raw = v.Value,
                    Control = control.Id,
                }
            );
        }
        return result;
    }
}

public static class GamepadEditor
{
    /// <summary>Writes another GTA control index for a mod's gamepad button (runs elevated through gmm).</summary>
    public static TxResult Apply(GameInstall game, BackupLibrary? library, string entryId, int control)
    {
        if (library is null)
            return new("", TxState.Rejected, L.T("core.keybinds.enableBackupsSettingsChangeHotkeys"));
        if (game.IsRunning())
            return new("", TxState.Rejected, L.T("core.keybinds.gameRunningChangesProhibitedClose"));
        if (GamepadControls.Find(control) is not { } target)
            return new("", TxState.Rejected, L.T("core.keybinds.padUnknownControl", control));
        var scan = new KeybindScanner(game, library).Scan();
        var e = scan.Pads.FirstOrDefault(x => x.Id.Equals(entryId, StringComparison.OrdinalIgnoreCase));
        if (e is null)
            return new("", TxState.Rejected, L.T("core.keybinds.bindingNotFoundFileMay"));
        if (e.Control == control)
            return new("", TxState.Rejected, L.T("core.keybinds.shortcutAlreadyAssigned"));
        var value = control.ToString(CultureInfo.InvariantCulture);
        return KeybindEditor.WriteValues(
            game,
            library,
            e.File,
            [(e.Line, e.Key, e.Raw, value)],
            L.T("core.keybinds.padTitle", e.ModName, e.Action, e.Control, target.Id),
            after =>
                GamepadParser
                    .Parse(after, e.File, new ModRef(e.ModId, e.ModName))
                    .Any(x => x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase) && x.Control == control)
        );
    }
}
