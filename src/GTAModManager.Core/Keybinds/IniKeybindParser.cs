using System.Text.RegularExpressions;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>The mod that owns the configuration file.</summary>
public sealed record ModRef(string Id, string Name);

/// <summary>Rules for recognizing key settings (shared by INI, JSON and XML).</summary>
public static partial class KeyParamRules
{
    private static readonly HashSet<string> Positive = new(StringComparer.OrdinalIgnoreCase)
    {
        "key",
        "hotkey",
        "hotkeys",
        "keybind",
        "keybinding",
        "binding",
        "bind",
        "shortcut",
        "modifier",
        "keycode",
        "vk",
        "keyboardkey",
    };

    // Device words: gamepad and mouse are not the keyboard; such settings are never taken.
    private static readonly HashSet<string> Device = new(StringComparer.OrdinalIgnoreCase)
    {
        "button",
        "btn",
        "controller",
        "pad",
        "gamepad",
        "joy",
        "joystick",
        "xinput",
        "stick",
        "trigger",
        "dpad",
        "axis",
        "mouse",
    };

    // Words for settings "next to a key" (delay, counter…) disqualify a name unless it explicitly ends in "Key".
    private static readonly HashSet<string> Negative = new(StringComparer.OrdinalIgnoreCase)
    {
        "count",
        "delay",
        "time",
        "speed",
        "duration",
        "repeat",
        "interval",
        "timeout",
        "sensitivity",
        "enable",
        "enabled",
        "disable",
        "disabled",
        "show",
        "use",
        "layout",
        "frame",
        "keyframe",
        "color",
        "colour",
        "volume",
        "id",
        "index",
        "hash",
        "path",
        "paths",
        "file",
        "files",
        "folder",
        "folders",
        "directory",
    };

    [GeneratedRegex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+")]
    private static partial Regex Words();

    public static List<string> Tokens(string name) =>
        Words().Matches(name.Replace('_', ' ').Replace('-', ' ')).Select(m => m.Value.ToLowerInvariant()).ToList();

    /// <summary>
    /// Whether a setting name looks like a key. "Control"/"Toggle"/"Activation" without the word key is only a weak hint:
    /// such settings are accepted only with a strict key-name value.
    /// </summary>
    public static (bool candidate, bool weak) Classify(string name)
    {
        var t = Tokens(name);
        if (t.Count == 0 || t.Any(Device.Contains))
            return (false, false);
        // "CarColorKey", "MenuKey2": an explicit trailing "Key" outweighs the other words.
        var words = t.Where(x => !x.All(char.IsDigit)).ToList();
        if (words.Count > 0 && words[^1] is "key" or "hotkey" or "keybind" or "keybinding" or "binding" or "shortcut")
            return (true, false);
        if (t.Any(Negative.Contains))
            return (false, false);
        if (t.Any(Positive.Contains))
            return (true, false);
        if (t.Any(x => x is "control" or "toggle" or "activation" or "activate" or "menu" or "open" or "клавиша"))
            return (true, true);
        return (false, false);
    }

    public static bool IsModifierParam(string name) => Tokens(name).Any(t => t is "modifier" or "mod" or "modkey");

    public static KeybindContext ContextFrom(string section, string name)
    {
        var t = Tokens(section + " " + name);
        if (t.Any(x => x is "global"))
            return KeybindContext.Global;
        if (t.Any(x => x is "vehicle" or "vehicles" or "car" or "drive" or "driving" or "cruise"))
            return KeybindContext.Vehicle;
        if (t.Any(x => x is "foot" or "onfoot" or "ped" or "walk" or "walking"))
            return KeybindContext.OnFoot;
        if (t.Any(x => x is "menu" or "ui" or "interface"))
            return KeybindContext.Menu;
        return KeybindContext.Unknown;
    }

    /// <summary>"MenuKeyUp" → "Menu Key Up" without the filler words Key/Binding, capitalized.</summary>
    public static string Humanize(string name)
    {
        var words = Words()
            .Matches(name.Replace('_', ' '))
            .Select(m => m.Value)
            .Where(w =>
                !w.Equals("key", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("binding", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("hotkey", StringComparison.OrdinalIgnoreCase)
                && !w.Equals("keybind", StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (words.Count == 0)
            return name;
        var s = string.Join(' ', words);
        return char.ToUpperInvariant(s[0]) + s[1..];
    }
}

/// <summary>
/// INI hotkeys. Supported: single values (names, VK_, 0x, decimal code, the ReShade quadruple),
/// "XKey + XModifier" pairs, and "X1 + X2" pairs (two keys pressed together, as in TrainerV).
/// A decimal code without confirmation (an author comment such as "Default F3") is marked as needing confirmation.
/// </summary>
public static partial class IniKeybindParser
{
    [GeneratedRegex(@"default\s*[:=]?\s*(?<k>[^,;/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DefaultInComment();

    [GeneratedRegex(@"^(?<stem>.*[A-Za-z])(?<n>\d)$")]
    private static partial Regex NumberedName();

    public static List<KeybindEntry> Parse(string text, string relPath, ModRef mod)
    {
        var doc = IniDocument.Parse(text);
        var result = new List<KeybindEntry>();

        // An empty value on a "strict" key name somewhere in the file means the author uses empty as "unassigned"
        // for this mod (like TrainerV); then it is safe to keep writing an empty value to disable a key.
        bool fileHasEmptyEvidence = doc.Values.Any(v =>
        {
            var (c, weak) = KeyParamRules.Classify(v.Key);
            return c && !weak && v.Value.Trim().Length == 0;
        });

        foreach (var section in doc.Values.GroupBy(v => v.Section))
        {
            var values = section.ToList();

            // Candidates in this section.
            var parsed = new Dictionary<IniValue, Candidate>();
            foreach (var v in values)
            {
                var (cand, weak) = KeyParamRules.Classify(v.Key);
                if (!cand)
                    continue;
                if (KeyNotation.IsUnassigned(v.Value))
                {
                    if (weak)
                        continue;
                    var dr = DisabledResult(v.Value, fileHasEmptyEvidence);
                    if (dr is not null)
                        parsed[v] = new Candidate(v, dr, true, null);
                    continue;
                }
                var r = KeyNotation.ParseValue(v.Value);
                if (r is null)
                    continue;
                if (KeyNotation.IsDisabledMarker(r.Chord))
                {
                    // Switched off by Mod Hub with the unpressable F24: shown as disabled, written back in the same syntax.
                    if (!weak)
                        parsed[v] = new Candidate(v, r with { Chord = default }, true, null);
                    continue;
                }
                var (defaultChord, corroborated) = CommentDefault(v.Comment, r);
                // "Control=1" is not a key: a weak name needs a strict key name or confirmation from an author comment.
                if (weak && !(r.Syntax == ValueSyntax.Names && r.Strict) && !corroborated)
                    continue;
                parsed[v] = new Candidate(v, r, r.Strict || corroborated, defaultChord);
            }
            // Repeats are counted only among recognized lines (reference text like "GodKeyOn2=XX …" does not count).
            var duplicates = parsed
                .Keys.GroupBy(v => v.Key, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var used = new HashSet<IniValue>();

            // 1. «XKey» + «XModifier».
            foreach (var modParam in parsed.Values.Where(c => KeyParamRules.IsModifierParam(c.Value.Key)).ToList())
            {
                var stem = StemOf(modParam.Value.Key, "modifier", "mod");
                var keyParam = parsed.Values.FirstOrDefault(c =>
                    c != modParam
                    && !KeyParamRules.IsModifierParam(c.Value.Key)
                    && StemOf(c.Value.Key, "key", "hotkey").Equals(stem, StringComparison.OrdinalIgnoreCase)
                );
                if (keyParam is null)
                    continue;
                bool none = modParam.Value.Value.Equals("None", StringComparison.OrdinalIgnoreCase);
                var modInfo = none ? null : KeyNames.ParseModifier(modParam.Value.Value);
                if (!none && modInfo is null && !keyParam.Result.Chord.IsEmpty)
                    continue;
                // If the key itself is disabled, the whole shortcut is empty and the modifier does not matter.
                var chord = keyParam.Result.Chord.IsEmpty
                    ? default
                    : keyParam.Result.Chord with
                    {
                        Mods = keyParam.Result.Chord.Mods | (modInfo?.mod ?? KeyMods.None),
                        Sides = keyParam.Result.Chord.Sides | (modInfo?.side ?? KeySide.None),
                    };
                result.Add(
                    Make(
                        relPath,
                        mod,
                        section.Key,
                        [keyParam with { Role = ParamRole.Key }, modParam with { Role = ParamRole.Modifier }],
                        chord,
                        duplicates,
                        fileHasEmptyEvidence
                    )
                );
                used.Add(modParam.Value);
                used.Add(keyParam.Value);
            }

            // 2. "X1" + "X2", "X3" + "X4"…: two keys together (the first is usually a modifier), as in TrainerV.
            var numbered = parsed
                .Values.Where(c =>
                    !used.Contains(c.Value) && !duplicates.Contains(c.Value.Key) && NumberedName().IsMatch(c.Value.Key)
                )
                .Select(c => (c, m: NumberedName().Match(c.Value.Key)))
                .GroupBy(x => x.m.Groups["stem"].Value, StringComparer.OrdinalIgnoreCase);
            foreach (var g in numbered)
            {
                var byNumber = g.ToDictionary(x => int.Parse(x.m.Groups["n"].Value), x => x.c);
                foreach (var n in byNumber.Keys.Where(n => n % 2 == 1).Order())
                {
                    if (!byNumber.TryGetValue(n, out var first) || !byNumber.TryGetValue(n + 1, out var second))
                        continue;
                    if (
                        first.Result.Chord.Mods != 0
                        || second.Result.Chord.Mods != 0
                        || first.Result.Chord.Second != 0
                        || second.Result.Chord.Second != 0
                    )
                        continue;
                    var chord = KeyNotation.FromKeys([first.Result.Chord.Vk, second.Result.Chord.Vk]);
                    if (chord is null)
                        continue;
                    result.Add(
                        Make(
                            relPath,
                            mod,
                            section.Key,
                            [first with { Role = ParamRole.First }, second with { Role = ParamRole.Second }],
                            chord.Value,
                            duplicates,
                            fileHasEmptyEvidence
                        )
                    );
                    used.Add(first.Value);
                    used.Add(second.Value);
                }
            }

            // 3. Everything else: single values.
            foreach (
                var c in parsed.Values.Where(c =>
                    !used.Contains(c.Value) && !KeyParamRules.IsModifierParam(c.Value.Key)
                )
            )
                result.Add(
                    Make(
                        relPath,
                        mod,
                        section.Key,
                        [c with { Role = ParamRole.Whole }],
                        c.Result.Chord,
                        duplicates,
                        fileHasEmptyEvidence
                    )
                );
        }
        return result;
    }

    private sealed record Candidate(
        IniValue Value,
        NotationResult Result,
        bool Strict,
        KeyChord? Default,
        ParamRole Role = ParamRole.Whole
    );

    private static string StemOf(string name, params string[] suffixes)
    {
        foreach (var s in suffixes)
            if (name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
                return name[..^s.Length];
        return name;
    }

    /// <summary>An author comment like "… Default F3": the default value and confirmation of a decimal code.</summary>
    private static (KeyChord? chord, bool corroborates) CommentDefault(string? comment, NotationResult r)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return (null, false);
        var m = DefaultInComment().Match(comment);
        if (!m.Success)
            return (null, false);
        var words = m.Groups["k"].Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // "Num 8", "RCTRL", "Pageup", "F3": try two words, then one.
        foreach (var candidate in new[] { string.Join(' ', words.Take(2)), words.FirstOrDefault() ?? "" })
        {
            if (KeyNames.Find(candidate) is { } k)
            {
                var chord = new KeyChord(k.Vk);
                return (chord, r.Chord.Vk == k.Vk && r.Chord.Mods == 0 && r.Chord.Second == 0);
            }
        }
        return (null, false);
    }

    /// <summary>
    /// The value explicitly means "unassigned" (empty, "None", a zero ReShade quadruple): build
    /// an empty shortcut for it, with the syntax inferred from the value itself. null if the token is ambiguous
    /// (for example "-1" or "off"): such lines are not shown, to avoid guessing how to write them back later.
    /// </summary>
    private static NotationResult? DisabledResult(string raw, bool fileHasEmptyEvidence)
    {
        var t = raw.Trim();
        if (t == "0,0,0,0")
            return new NotationResult(default, ValueSyntax.ReShadeTuple, new NotationStyle(), true);
        if (t.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new NotationResult(default, ValueSyntax.Names, new NotationStyle(FormsNames: true), true);
        if (t.Length == 0 && fileHasEmptyEvidence || t == "0")
            return new NotationResult(default, ValueSyntax.Decimal, new NotationStyle(), true);
        return null;
    }

    private static KeybindEntry Make(
        string rel,
        ModRef mod,
        string section,
        List<Candidate> parts,
        KeyChord chord,
        HashSet<string> duplicates,
        bool fileHasEmptyEvidence
    )
    {
        bool strict = parts.All(p => p.Strict);
        bool duplicate = parts.Any(p => duplicates.Contains(p.Value.Key));
        var first = parts[0];
        string? note =
            duplicate ? "Параметр задан в секции несколько раз — неясно, какое значение использует мод."
            : !strict ? parts.Select(p => p.Result.Note).FirstOrDefault(n => n is not null)
            : null;
        // Action name: from the author's comment ("//Enter Menu, Default F3"), otherwise from the setting name.
        var commentAction = first.Value.Comment is { Length: > 0 } c
            ? Regex.Replace(c, @"[,;]?\s*default.*$", "", RegexOptions.IgnoreCase).Trim()
            : "";
        var stem =
            parts.Count > 1
                ? Regex.Replace(first.Value.Key, @"(Modifier|Mod|[12])$", "", RegexOptions.IgnoreCase)
                : first.Value.Key;
        var action = commentAction.Length is > 2 and < 60 ? commentAction : KeyParamRules.Humanize(stem);

        var paramRefs = parts
            .Select(p => new ParamRef(
                p.Value.Key,
                p.Value.Line,
                p.Value.Value,
                p.Role,
                p.Result.Syntax,
                p.Result.Style
            ))
            .ToList();
        KeyChord? def = parts.Count == 1 ? first.Default : null;
        bool disabled = chord.IsEmpty;
        var status =
            disabled ? KeybindStatus.Disabled
            : duplicate || !strict ? KeybindStatus.NeedsConfirmation
            : KeybindStatus.Recognized;
        // Every syntax has a way to write "unassigned" (KeyNotation.FormatDisableToken: the mod's own empty value,
        // a zero code, or the unpressable F24). Confidence that it is a key at all is checked through Editable.
        bool canDisable = !disabled;
        return new KeybindEntry
        {
            Id = $"{rel}|{section}|{string.Join("+", parts.Select(p => p.Value.Key))}",
            ModId = mod.Id,
            ModName = mod.Name,
            Action = action,
            Chord = chord,
            Format = KeybindFormat.Ini,
            File = rel,
            Section = section,
            Params = paramRefs,
            Status = status,
            StatusNote = note,
            Context = KeyParamRules.ContextFrom(section, first.Value.Key),
            Editable = status is KeybindStatus.Recognized or KeybindStatus.Disabled,
            EditBlockReason =
                status is KeybindStatus.Recognized or KeybindStatus.Disabled ? null
                : duplicate ? note
                : L.T("core.keybinds.firstConfirmKey"),
            CanDisable = canDisable,
            Default = def,
            DefaultSource = def is null ? null : L.T("core.keybinds.authorSCommentFile"),
        };
    }
}
