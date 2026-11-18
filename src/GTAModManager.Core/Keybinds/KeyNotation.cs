using System.Globalization;
using System.Text.RegularExpressions;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>Parse result: shortcut, notation syntax and recognition strictness.</summary>
public sealed record NotationResult(
    KeyChord Chord,
    ValueSyntax Syntax,
    NotationStyle Style,
    bool Strict,
    string? Note = null
);

/// <summary>
/// Parsing and writing back key values. Strictness: key names, VK_, 0x and the ReShade quadruple are strict;
/// a bare decimal number is only a candidate (it needs confirmation or a confirming author comment).
/// </summary>
public static partial class KeyNotation
{
    private static readonly HashSet<string> Unassigned = new(StringComparer.OrdinalIgnoreCase)
    {
        "",
        "none",
        "null",
        "0",
        "-1",
        "false",
        "off",
        "disabled",
        "unset",
        "unbound",
        "no",
        "0x00",
        "0x0",
        "—",
        "-",
    };

    // Names typical of System.Windows.Forms.Keys (used by ScriptHookVDotNet mods).
    private static readonly HashSet<string> FormsOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "ControlKey",
        "ShiftKey",
        "Menu",
        "LControlKey",
        "RControlKey",
        "LShiftKey",
        "RShiftKey",
        "LMenu",
        "RMenu",
        "Return",
        "Back",
        "Escape",
        "Capital",
        "Prior",
        "Next",
        "Snapshot",
        "Scroll",
        "Add",
        "Subtract",
        "Multiply",
        "Divide",
        "Decimal",
    };

    [GeneratedRegex(@"^(\d{1,3}),([01]),([01]),([01])$")]
    private static partial Regex ReShade();

    [GeneratedRegex(@"^0x([0-9a-fA-F]{1,2})$")]
    private static partial Regex HexValue();

    [GeneratedRegex(@"^\d{1,3}$")]
    private static partial Regex Decimal();

    [GeneratedRegex(@"^(D\d|NumPad\d|Oem\w+)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormsPattern();

    [GeneratedRegex(@"^(?<p>num\s?pad|num|kp|numpad\s)\s?(?<n>\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumPadToken();

    public static bool IsUnassigned(string raw) => Unassigned.Contains(raw.Trim()) || raw.Trim() == "0,0,0,0";

    /// <summary>Parses a setting value. null if it does not look like a key.</summary>
    public static NotationResult? ParseValue(string raw)
    {
        var v = raw.Trim().Trim('"');
        if (v.Length == 0 || v.Length > 48)
            return null;

        var rs = ReShade().Match(v);
        if (rs.Success)
        {
            int vk = int.Parse(rs.Groups[1].Value, CultureInfo.InvariantCulture);
            if (KeyNames.Get(vk) is null)
                return null;
            var mods =
                (rs.Groups[2].Value == "1" ? KeyMods.Ctrl : 0)
                | (rs.Groups[3].Value == "1" ? KeyMods.Shift : 0)
                | (rs.Groups[4].Value == "1" ? KeyMods.Alt : 0);
            return new(new KeyChord(vk, mods), ValueSyntax.ReShadeTuple, new NotationStyle(), true);
        }

        var hex = HexValue().Match(v);
        if (hex.Success)
        {
            int vk = int.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return KeyNames.Get(vk) is null
                ? null
                : new(
                    Single(vk),
                    ValueSyntax.Hex,
                    new NotationStyle(UpperCase: v[2..] == v[2..].ToUpperInvariant()),
                    true
                );
        }

        if (Decimal().IsMatch(v))
        {
            int vk = int.Parse(v, CultureInfo.InvariantCulture);
            var key = KeyNames.Get(vk);
            return key is null
                ? null
                : new(
                    Single(vk),
                    ValueSyntax.Decimal,
                    new NotationStyle(),
                    false,
                    L.T("core.keybinds.numberLooksLikeKeyCode", vk, key.Display)
                );
        }

        var combo = ParseCombination(v);
        if (combo is null)
            return null;
        bool vkPrefixed = v.Split('+', ',').All(t => t.Trim().StartsWith("VK_", StringComparison.OrdinalIgnoreCase));
        return combo with { Syntax = vkPrefixed ? ValueSyntax.VkPrefixed : ValueSyntax.Names };
    }

    private static KeyChord Single(int vk) => new(vk);

    /// <summary>"Ctrl+Shift+N", "Control + R", "F5, Control", "LShiftKey", "Num -". null if any part is not a key.</summary>
    public static NotationResult? ParseCombination(string text)
    {
        var v = text.Trim();
        if (v.Length == 0)
            return null;
        // A NumPad key whose name contains "+" or "-": "Num +", "Num-".
        if (KeyNames.Find(v) is { } whole)
            return new(FromKeys([whole.Vk])!.Value, ValueSyntax.Names, StyleFor([v], "+", false, v), true);

        string sep;
        string[] tokens;
        bool modsAfter = false;
        var commaParts = v.Split(',', StringSplitOptions.TrimEntries);
        if (commaParts.Length > 1 && commaParts.All(p => p.Length > 0))
        {
            tokens = commaParts;
            sep = Regex.Match(v, @",\s*").Value;
            modsAfter = true; // Windows.Forms: «F5, Control»
        }
        else
        {
            var m = Regex.Match(v, @"\s*\+\s*");
            if (!m.Success)
                return null;
            sep = m.Value;
            tokens = Regex.Split(v, @"\s*\+\s*");
            if (tokens.Any(t => t.Length == 0))
                return null;
        }

        var keys = new List<int>();
        foreach (var t in tokens)
        {
            var k = KeyNames.Find(t);
            if (k is null)
                return null;
            keys.Add(k.Vk);
        }
        var chord = FromKeys(keys);
        if (chord is null)
            return null;
        return new(chord.Value, ValueSyntax.Names, StyleFor(tokens, sep, modsAfter, v), true);
    }

    /// <summary>Key set → shortcut: modifiers separately, at most two regular keys.</summary>
    public static KeyChord? FromKeys(IReadOnlyList<int> vks)
    {
        var mods = KeyMods.None;
        var sides = KeySide.None;
        var plain = new List<int>();
        foreach (var vk in vks)
        {
            var mod = KeyNames.ModifierFor(vk);
            if (mod != KeyMods.None && vks.Count > 1)
            {
                mods |= mod;
                sides |= KeyNames.SideFor(vk);
            }
            else
                plain.Add(vk);
        }
        // Modifiers only ("Ctrl+Shift"): the last one is treated as the main key.
        if (plain.Count == 0)
        {
            plain.Add(vks[^1]);
            mods &= ~KeyNames.ModifierFor(vks[^1]);
        }
        if (plain.Count > 2)
            return null;
        return new KeyChord(plain[0], mods, sides, plain.Count > 1 ? plain[1] : 0);
    }

    private static NotationStyle StyleFor(IReadOnlyList<string> tokens, string sep, bool modsAfter, string raw)
    {
        string Word(KeyMods m, string fallback) =>
            tokens.FirstOrDefault(t => KeyNames.ParseModifier(t)?.mod == m) ?? fallback;
        bool forms = tokens.Any(t => FormsOnly.Contains(t) || FormsPattern().IsMatch(t));
        bool upper = raw.Any(char.IsLetter) && raw == raw.ToUpperInvariant() && tokens.Any(t => t.Length > 1);
        string? numPrefix = tokens
            .Select(t => NumPadToken().Match(t))
            .Where(m => m.Success)
            .Select(m => m.Groups["p"].Value)
            .FirstOrDefault();
        return new NotationStyle(
            sep,
            modsAfter,
            Word(KeyMods.Ctrl, forms ? "Control" : "Ctrl"),
            Word(KeyMods.Shift, "Shift"),
            Word(KeyMods.Alt, forms ? "Alt" : "Alt"),
            forms,
            upper,
            numPrefix
        );
    }

    // ---------- Writing back ----------

    /// <summary>Key name in the style of the original notation.</summary>
    public static string KeyName(int vk, NotationStyle style)
    {
        var k = KeyNames.Get(vk) ?? throw new ArgumentException(L.T("core.keybinds.unknownKey", KeyNames.Hex(vk)));
        string name;
        if (vk is >= 0x30 and <= 0x39)
            name = style.FormsNames ? "D" + (vk - 0x30) : ((char)vk).ToString();
        else if (vk is >= 0x60 and <= 0x69)
            name = (style.NumPadPrefix ?? "NumPad") + (vk - 0x60);
        else
            name = k.FormsName;
        return style.UpperCase ? name.ToUpperInvariant() : name;
    }

    /// <summary>
    /// Value of a single setting for the new shortcut. null plus a reason if the syntax cannot express the shortcut
    /// (for example a single code without modifiers).
    /// </summary>
    public static (string? value, string? error) Format(KeyChord chord, ParamRef p, KeyChord original)
    {
        bool hasMods = chord.Mods != KeyMods.None || chord.Second != 0;
        switch (p.Role)
        {
            case ParamRole.Modifier:
            {
                var m = chord.Mods;
                if (chord.Second != 0)
                    return (null, L.T("core.keybinds.secondKeyCannotStoredParameter"));
                if (m == KeyMods.None)
                    return p.Raw.Equals("None", StringComparison.OrdinalIgnoreCase)
                        ? ("None", null)
                        : (null, L.T("core.keybinds.unknownHowModStoresNo"));
                if (m is not (KeyMods.Ctrl or KeyMods.Shift or KeyMods.Alt))
                    return (null, L.T("core.keybinds.modSupportsOneModifierCtrl"));
                var forms = p.Raw is "Control" or "None" || p.Style.FormsNames;
                var word = m switch
                {
                    KeyMods.Ctrl => forms ? "Control" : "Ctrl",
                    KeyMods.Shift => "Shift",
                    _ => "Alt",
                };
                return (p.Style.UpperCase ? word.ToUpperInvariant() : word, null);
            }
            case ParamRole.First:
            case ParamRole.Second:
            {
                // Two keys in two settings (TrainerV). The modifier goes into the first setting.
                var mods = Enum.GetValues<KeyMods>().Where(m => m != KeyMods.None && chord.Mods.HasFlag(m)).ToList();
                if (mods.Count + (chord.Second != 0 ? 2 : 1) != 2)
                    return (null, L.T("core.keybinds.modUsesShortcutExactlyTwo"));
                int first = mods.Count == 1 ? ModifierVk(mods[0], chord.Sides, original) : chord.Vk;
                int second = mods.Count == 1 ? chord.Vk : chord.Second;
                int vk = p.Role == ParamRole.First ? first : second;
                return (Code(vk, p), null);
            }
            case ParamRole.Key:
                // The modifier goes into the neighbouring setting (MenuModifier); only the key itself goes here.
                if (chord.Second != 0)
                    return (null, L.T("core.keybinds.modStoresOneKeyModifier"));
                return (Single(chord.Vk, p), null);
        }

        // Whole: the entire value.
        switch (p.Syntax)
        {
            case ValueSyntax.ReShadeTuple:
                if (chord.Second != 0 || chord.Mods.HasFlag(KeyMods.Win))
                    return (null, L.T("core.keybinds.reshadeSupportsOnlyCtrlShift"));
                return (
                    $"{chord.Vk},{B(chord.Mods.HasFlag(KeyMods.Ctrl))},{B(chord.Mods.HasFlag(KeyMods.Shift))},{B(chord.Mods.HasFlag(KeyMods.Alt))}",
                    null
                );
            case ValueSyntax.Hex:
            case ValueSyntax.Decimal:
                if (hasMods)
                    return (null, L.T("core.keybinds.parameterAcceptsOneKeyWithout"));
                return (Code(chord.Vk, p), null);
            default:
                bool originalCombo = p.Raw.Contains('+') || p.Raw.Contains(',');
                if (hasMods && !originalCombo)
                    return (null, L.T("core.keybinds.settingUsesSingleKeyUnknown"));
                return (Names(chord, p), null);
        }

        static string B(bool v) => v ? "1" : "0";
    }

    /// <summary>
    /// The value meaning "key not assigned" in this setting's syntax.
    /// Only unambiguously safe options are supported: the zero ReShade quadruple, "None" for Forms names
    /// and an empty value for a decimal code (only if the file already has such an example; the caller checks this).
    /// </summary>
    /// <summary>
    /// A key nobody can press: F24 exists in Windows but not on keyboards. Mods whose own "unassigned" value is
    /// unknown get it, so the key is off while the setting stays a valid key for the mod.
    /// </summary>
    public const int DisabledVk = 0x87;

    /// <summary>The value is Mod Hub's "disabled" key (<see cref="DisabledVk"/>) on its own.</summary>
    public static bool IsDisabledMarker(KeyChord chord) =>
        chord.Vk == DisabledVk && chord.Mods == KeyMods.None && chord.Second == 0;

    /// <summary>
    /// How to write "unassigned" in this setting's syntax: the mod's own empty value where it is known to work,
    /// a zero virtual-key code (never pressed) for codes, otherwise the unpressable F24.
    /// </summary>
    public static (string? value, string? error) FormatDisableToken(ParamRef p) =>
        p.Syntax switch
        {
            ValueSyntax.ReShadeTuple => ("0,0,0,0", null),
            ValueSyntax.Names when p.Style.FormsNames => ("None", null),
            // Virtual-key code 0 is never pressed (Simple Trainer's own way to switch an option's key off).
            ValueSyntax.Decimal => ("0", null),
            _ => (Single(DisabledVk, p), null),
        };

    private static string Single(int vk, ParamRef p) =>
        p.Syntax switch
        {
            ValueSyntax.Hex or ValueSyntax.Decimal => Code(vk, p),
            ValueSyntax.VkPrefixed => "VK_" + KeyNames.Get(vk)!.Win32Name,
            _ => KeyName(vk, p.Style),
        };

    private static string Code(int vk, ParamRef p) =>
        p.Syntax switch
        {
            ValueSyntax.Hex => "0x" + vk.ToString(p.Style.UpperCase ? "X2" : "x2", CultureInfo.InvariantCulture),
            ValueSyntax.VkPrefixed => "VK_" + KeyNames.Get(vk)!.Win32Name,
            ValueSyntax.Names => KeyName(vk, p.Style),
            _ => vk.ToString(CultureInfo.InvariantCulture),
        };

    private static string Names(KeyChord chord, ParamRef p)
    {
        var s = p.Style;
        var mods = new List<string>();
        if (chord.Mods.HasFlag(KeyMods.Ctrl))
            mods.Add(s.CtrlWord);
        if (chord.Mods.HasFlag(KeyMods.Shift))
            mods.Add(s.ShiftWord);
        if (chord.Mods.HasFlag(KeyMods.Alt))
            mods.Add(s.AltWord);
        if (chord.Mods.HasFlag(KeyMods.Win))
            mods.Add("Win");
        string Name(int vk) =>
            p.Syntax == ValueSyntax.VkPrefixed ? "VK_" + KeyNames.Get(vk)!.Win32Name : KeyName(vk, s);
        var keys = chord.Keys.Select(Name).ToList();
        var parts = s.ModifiersAfterKey ? keys.Concat(mods) : mods.Concat(keys);
        var text = string.Join(s.Separator, parts);
        return s.UpperCase ? text.ToUpperInvariant() : text;
    }

    /// <summary>Modifier VK: keep the side if it is specified in the new shortcut or was present in the original.</summary>
    private static int ModifierVk(KeyMods mod, KeySide sides, KeyChord original)
    {
        var allSides = sides | original.Sides;
        return mod switch
        {
            KeyMods.Ctrl => allSides.HasFlag(KeySide.RCtrl) ? 0xA3
            : allSides.HasFlag(KeySide.LCtrl) ? 0xA2
            : 0x11,
            KeyMods.Shift => allSides.HasFlag(KeySide.RShift) ? 0xA1
            : allSides.HasFlag(KeySide.LShift) ? 0xA0
            : 0x10,
            KeyMods.Alt => allSides.HasFlag(KeySide.RAlt) ? 0xA5
            : allSides.HasFlag(KeySide.LAlt) ? 0xA4
            : 0x12,
            _ => 0x5B,
        };
    }
}
