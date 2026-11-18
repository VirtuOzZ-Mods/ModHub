using System.Text.Json.Serialization;

namespace GTAModManager.Core.Keybinds;

[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>Modifier side, only if the format distinguishes it (LCtrl/RCtrl).</summary>
[Flags]
public enum KeySide
{
    None = 0,
    LCtrl = 1,
    RCtrl = 2,
    LShift = 4,
    RShift = 8,
    LAlt = 16,
    RAlt = 32,
    LWin = 64,
    RWin = 128,
}

/// <summary>
/// Normalized shortcut: main key + modifiers, plus (rarely) a second regular key
/// that must be held at the same time (TrainerV: "AirBreakKey1=G, AirBreakKey2=6").
/// </summary>
public readonly record struct KeyChord(
    int Vk,
    KeyMods Mods = KeyMods.None,
    KeySide Sides = KeySide.None,
    int Second = 0
)
{
    public bool IsEmpty => Vk == 0;

    /// <summary>All keys without modifiers (for keyboard highlighting).</summary>
    public IEnumerable<int> Keys => Second == 0 ? [Vk] : [Vk, Second];

    public override string ToString()
    {
        if (IsEmpty)
            return "—";
        var parts = new List<string>();
        var mods = Mods;
        var sides = Sides;
        void Mod(KeyMods m, KeySide l, KeySide r, string name)
        {
            if (!mods.HasFlag(m))
                return;
            parts.Add(
                sides.HasFlag(l) ? "L" + name
                : sides.HasFlag(r) ? "R" + name
                : name
            );
        }
        Mod(KeyMods.Ctrl, KeySide.LCtrl, KeySide.RCtrl, "Ctrl");
        Mod(KeyMods.Shift, KeySide.LShift, KeySide.RShift, "Shift");
        Mod(KeyMods.Alt, KeySide.LAlt, KeySide.RAlt, "Alt");
        Mod(KeyMods.Win, KeySide.LWin, KeySide.RWin, "Win");
        parts.Add(KeyNames.Get(Vk)?.Display ?? KeyNames.Hex(Vk));
        if (Second != 0)
            parts.Add(KeyNames.Get(Second)?.Display ?? KeyNames.Hex(Second));
        return string.Join(" + ", parts);
    }

    private static readonly HashSet<string> DisableWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "none",
        "disable",
        "disabled",
        "off",
        "выкл",
        "откл",
        "отключено",
        "отключить",
        "—",
        "-",
        "unbound",
    };

    /// <summary>
    /// Parses the ToString / command-line notation: "Ctrl + Shift + N", "RCtrl + F5", "Num 7".
    /// Also recognizes "disable the key": "none", "—", "off" (and the Russian equivalent) → an empty shortcut (not null).
    /// </summary>
    public static KeyChord? Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            return null;
        // An empty chord, not "default": for KeyChord? that would be null, i.e. "could not parse".
        if (DisableWords.Contains(t))
            return new KeyChord();
        return KeyNotation.ParseCombination(t)?.Chord;
    }
}

public enum KeybindFormat
{
    Ini,
    Json,
    Xml,
    Manifest,
    Game,
}

public enum KeybindStatus
{
    /// <summary>Recognized by strict rules.</summary>
    Recognized,

    /// <summary>Looks like a binding but needs user confirmation (for example a bare number).</summary>
    NeedsConfirmation,

    /// <summary>The user confirmed the suggested interpretation.</summary>
    Confirmed,

    /// <summary>The key is explicitly disabled in the mod file (empty, "None" or a zero ReShade quadruple).</summary>
    Disabled,
}

public enum KeybindContext
{
    Unknown,
    OnFoot,
    Vehicle,
    Menu,
    Global,
}

/// <summary>How the value is written in the file, so a change can be written back in the same syntax.</summary>
public enum ValueSyntax
{
    Names, // F5, NumPad7, Ctrl+Shift+N, «F5, Control»
    VkPrefixed, // VK_F5
    Hex, // 0x74
    Decimal, // 116
    ReShadeTuple, // 45,0,0,0: code, Ctrl, Shift, Alt
}

/// <summary>The setting's role in the shortcut when it is written across several settings.</summary>
public enum ParamRole
{
    Whole,
    Key,
    Modifier,
    First,
    Second,
}

/// <summary>One configuration file setting the binding was built from.</summary>
public sealed record ParamRef(
    string Name,
    int Line,
    string Raw,
    ParamRole Role,
    ValueSyntax Syntax,
    NotationStyle Style
);

/// <summary>Naming details (case, separators, name family) used when writing back.</summary>
public sealed record NotationStyle(
    string Separator = "+",
    bool ModifiersAfterKey = false,
    string CtrlWord = "Ctrl",
    string ShiftWord = "Shift",
    string AltWord = "Alt",
    bool FormsNames = false,
    bool UpperCase = false,
    string? NumPadPrefix = null
);

/// <summary>A key binding: who, what, where it is written and whether it can be safely changed.</summary>
public sealed record KeybindEntry
{
    /// <summary>Stable identifier: "file|section|setting[+setting]".</summary>
    public required string Id { get; init; }
    public required string ModId { get; init; }
    public required string ModName { get; init; }
    public required string Action { get; init; }
    public KeyChord Chord { get; init; }
    public KeybindFormat Format { get; init; }

    /// <summary>File path relative to the game folder.</summary>
    public string File { get; init; } = "";
    public string Section { get; init; } = "";
    public List<ParamRef> Params { get; init; } = [];
    public KeybindStatus Status { get; init; }
    public string? StatusNote { get; init; }
    public KeybindContext Context { get; init; }
    public bool Editable { get; init; }
    public string? EditBlockReason { get; init; }

    /// <summary>This key can be reliably disabled (there is a safe way to write "unassigned" for this format).</summary>
    public bool CanDisable { get; init; }

    /// <summary>Default value, if reliably known (modhub manifest or author comment).</summary>
    public KeyChord? Default { get; init; }
    public string? DefaultSource { get; init; }

    /// <summary>The binding is described by the mod author in modhub.json.</summary>
    public bool FromManifest { get; init; }

    [JsonIgnore]
    public string RawValue => string.Join("  ", Params.Select(p => $"{p.Name}={p.Raw}"));

    [JsonIgnore]
    public bool IsGame => Format == KeybindFormat.Game;

    [JsonIgnore]
    public bool IsActive => Status != KeybindStatus.NeedsConfirmation;
}
