using System.Globalization;

namespace GTAModManager.Core.Keybinds;

/// <summary>One physical keyboard key: Windows virtual-key code, display name and notation variants.</summary>
public sealed record KeyInfo(int Vk, string Display, string FormsName, string Win32Name, string[] Aliases)
{
    public bool IsModifier => KeyNames.ModifierFor(Vk) != KeyMods.None;
    public bool IsNumPad => Vk is >= 0x60 and <= 0x6F;
}

/// <summary>
/// Key table and notation parsing: F5, NumPad7/Numpad7/Num7, VK_F5, 0x74, Oem6, D1, Return, "]", etc.
/// NumPad and regular digits are different keys (VK 0x60–0x69 and 0x30–0x39).
/// </summary>
public static class KeyNames
{
    public static readonly IReadOnlyList<KeyInfo> All = Build();
    private static readonly Dictionary<int, KeyInfo> ByVk = All.ToDictionary(k => k.Vk);
    private static readonly Dictionary<string, KeyInfo> ByAlias = BuildAliases();

    public static KeyInfo? Get(int vk) => ByVk.GetValueOrDefault(vk);

    /// <summary>Key by name (ignoring case, "_" and spaces); null if not a key.</summary>
    public static KeyInfo? Find(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        var t = token.Trim();
        if (t.Length == 1)
        {
            var c = char.ToUpperInvariant(t[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
                return Get(c);
        }
        var n = Norm(t);
        if (ByAlias.TryGetValue(n, out var k))
            return k;
        if (n.StartsWith("vk") && n.Length > 2 && ByAlias.TryGetValue(n[2..], out k))
            return k; // VK_F5 → F5
        return null;
    }

    public static KeyMods ModifierFor(int vk) =>
        vk switch
        {
            0x10 or 0xA0 or 0xA1 => KeyMods.Shift,
            0x11 or 0xA2 or 0xA3 => KeyMods.Ctrl,
            0x12 or 0xA4 or 0xA5 => KeyMods.Alt,
            0x5B or 0x5C => KeyMods.Win,
            _ => KeyMods.None,
        };

    /// <summary>Modifier side, if the code distinguishes it (LCtrl/RCtrl…).</summary>
    public static KeySide SideFor(int vk) =>
        vk switch
        {
            0xA0 => KeySide.LShift,
            0xA1 => KeySide.RShift,
            0xA2 => KeySide.LCtrl,
            0xA3 => KeySide.RCtrl,
            0xA4 => KeySide.LAlt,
            0xA5 => KeySide.RAlt,
            0x5B => KeySide.LWin,
            0x5C => KeySide.RWin,
            _ => KeySide.None,
        };

    /// <summary>Modifier by word: Ctrl/Control/LControl/Shift/Alt/Menu/Win.</summary>
    public static (KeyMods mod, KeySide side)? ParseModifier(string token)
    {
        var k = Find(token);
        if (k is null)
            return null;
        var mod = ModifierFor(k.Vk);
        return mod == KeyMods.None ? null : (mod, SideFor(k.Vk));
    }

    private static string Norm(string s) => s.ToLowerInvariant().Replace("_", "").Replace(" ", "");

    private static Dictionary<string, KeyInfo> BuildAliases()
    {
        var d = new Dictionary<string, KeyInfo>();
        foreach (var k in All)
        foreach (var a in k.Aliases.Append(k.Display).Append(k.FormsName).Append(k.Win32Name))
        {
            if (a.Length == 1 && !char.IsLetterOrDigit(a[0]))
                d[a] = k; // symbols such as "]" and ";" are kept as-is
            else if (a.Length > 0)
                d.TryAdd(Norm(a), k);
        }
        return d;
    }

    private static List<KeyInfo> Build()
    {
        var list = new List<KeyInfo>();
        void Add(int vk, string display, string forms, string win32, params string[] aliases) =>
            list.Add(new(vk, display, forms, win32, aliases));

        Add(0x08, "Backspace", "Back", "BACK", "Backspace", "BkSp");
        Add(0x09, "Tab", "Tab", "TAB");
        Add(0x0D, "Enter", "Return", "RETURN", "Enter");
        Add(0x10, "Shift", "ShiftKey", "SHIFT", "Shift");
        Add(0x11, "Ctrl", "ControlKey", "CONTROL", "Control", "Ctrl", "Ctl", "Strg");
        Add(0x12, "Alt", "Menu", "MENU", "Alt");
        Add(0x13, "Pause", "Pause", "PAUSE", "Break");
        Add(0x14, "Caps Lock", "CapsLock", "CAPITAL", "Capital", "Caps", "CapsLock");
        Add(0x1B, "Esc", "Escape", "ESCAPE", "Esc");
        Add(0x20, "Space", "Space", "SPACE", "Spacebar", "Пробел");
        Add(0x21, "Page Up", "PageUp", "PRIOR", "Prior", "PgUp", "PageUp");
        Add(0x22, "Page Down", "PageDown", "NEXT", "Next", "PgDn", "PageDown");
        Add(0x23, "End", "End", "END");
        Add(0x24, "Home", "Home", "HOME");
        Add(0x25, "←", "Left", "LEFT", "Left", "LeftArrow", "ArrowLeft");
        Add(0x26, "↑", "Up", "UP", "Up", "UpArrow", "ArrowUp");
        Add(0x27, "→", "Right", "RIGHT", "Right", "RightArrow", "ArrowRight");
        Add(0x28, "↓", "Down", "DOWN", "Down", "DownArrow", "ArrowDown");
        Add(0x2C, "Print Screen", "PrintScreen", "SNAPSHOT", "Snapshot", "PrtSc", "PrintScreen");
        Add(0x2D, "Insert", "Insert", "INSERT", "Ins");
        Add(0x2E, "Delete", "Delete", "DELETE", "Del");
        for (int i = 0; i <= 9; i++)
            Add(0x30 + i, i.ToString(), "D" + i, i.ToString(), "D" + i, "Digit" + i, "Key" + i);
        for (char c = 'A'; c <= 'Z'; c++)
            Add(c, c.ToString(), c.ToString(), c.ToString());
        Add(0x5B, "Win", "LWin", "LWIN", "LWin", "Win", "Windows", "LeftWindows");
        Add(0x5C, "RWin", "RWin", "RWIN", "RightWindows");
        Add(0x5D, "Menu", "Apps", "APPS", "Apps", "ContextMenu");
        for (int i = 0; i <= 9; i++)
            Add(0x60 + i, "Num " + i, "NumPad" + i, "NUMPAD" + i, "Numpad" + i, "Num" + i, "KP" + i, "NumPad " + i);
        Add(0x6A, "Num *", "Multiply", "MULTIPLY", "NumPadMultiply", "NumMultiply", "Num*", "NumpadStar");
        Add(0x6B, "Num +", "Add", "ADD", "NumPadAdd", "NumAdd", "NumPlus", "Num+");
        Add(0x6D, "Num -", "Subtract", "SUBTRACT", "NumPadSubtract", "NumSubtract", "NumMinus", "Num-");
        Add(0x6E, "Num .", "Decimal", "DECIMAL", "NumPadDecimal", "NumDecimal", "NumDel", "Num.");
        Add(0x6F, "Num /", "Divide", "DIVIDE", "NumPadDivide", "NumDivide", "Num/");
        // F13–F24 exist in Windows but not on ordinary keyboards; F24 doubles as Mod Hub's "disabled" key.
        for (int i = 1; i <= 24; i++)
            Add(0x6F + i, "F" + i, "F" + i, "F" + i);
        Add(0x90, "Num Lock", "NumLock", "NUMLOCK", "NumLock");
        Add(0x91, "Scroll Lock", "Scroll", "SCROLL", "ScrollLock");
        Add(0xA0, "LShift", "LShiftKey", "LSHIFT", "LShift", "LeftShift");
        Add(0xA1, "RShift", "RShiftKey", "RSHIFT", "RShift", "RightShift");
        Add(0xA2, "LCtrl", "LControlKey", "LCONTROL", "LControl", "LCtrl", "LeftCtrl", "LeftControl");
        Add(0xA3, "RCtrl", "RControlKey", "RCONTROL", "RControl", "RCtrl", "RightCtrl", "RightControl");
        Add(0xA4, "LAlt", "LMenu", "LMENU", "LAlt", "LeftAlt");
        Add(0xA5, "RAlt", "RMenu", "RMENU", "RAlt", "RightAlt", "AltGr");
        Add(0xBA, ";", "Oem1", "OEM_1", "OemSemicolon", "Semicolon");
        Add(0xBB, "=", "Oemplus", "OEM_PLUS", "OemPlus", "Equals");
        Add(0xBC, ",", "Oemcomma", "OEM_COMMA", "OemComma", "Comma");
        Add(0xBD, "-", "OemMinus", "OEM_MINUS", "OemMinus", "Minus");
        Add(0xBE, ".", "OemPeriod", "OEM_PERIOD", "OemPeriod", "Period");
        Add(0xBF, "/", "OemQuestion", "OEM_2", "Oem2", "Slash", "Question");
        Add(0xC0, "`", "Oemtilde", "OEM_3", "Oem3", "OemTilde", "Tilde", "Grave");
        Add(0xDB, "[", "OemOpenBrackets", "OEM_4", "Oem4", "LBracket", "OpenBracket");
        Add(0xDC, "\\", "OemPipe", "OEM_5", "Oem5", "Backslash", "Pipe");
        Add(0xDD, "]", "OemCloseBrackets", "OEM_6", "Oem6", "RBracket", "CloseBracket");
        Add(0xDE, "'", "OemQuotes", "OEM_7", "Oem7", "Quote", "Apostrophe");
        return list;
    }

    public static string Hex(int vk) => "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
}
