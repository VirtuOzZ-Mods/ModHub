using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

public enum ConflictKind
{
    Direct,
    Potential,
}

/// <summary>An overlap between two bindings and a clear explanation of why it was found.</summary>
public sealed record KeybindConflict(ConflictKind Kind, KeybindEntry A, KeybindEntry B, string Reason)
{
    public IEnumerable<int> Keys => A.Chord.Keys.Union(B.Chord.Keys);
}

/// <summary>State of one physical key for the virtual keyboard.</summary>
public enum KeyState
{
    Free,
    Game,
    Mod,
    Potential,
    Direct,
}

/// <summary>
/// Hotkey overlap detection. The same key alone does not prove a conflict:
/// Direct: the same shortcut for different owners in the same known context (or "everywhere");
/// Potential: the same shortcut with an unknown context, a nested shortcut (E and Shift+E, F5 and RCtrl+F5),
/// different modifier sides, or an overlap with the standard game controls (which may have been rebound).
/// Bindings within one mod are not compared: the author separates them by mode.
/// </summary>
public static class KeybindConflictAnalyzer
{
    public static List<KeybindConflict> Analyze(IReadOnlyList<KeybindEntry> entries)
    {
        var result = new List<KeybindConflict>();
        var active = entries.Where(e => !e.Chord.IsEmpty).ToList();
        for (int i = 0; i < active.Count; i++)
        for (int j = i + 1; j < active.Count; j++)
        {
            var a = active[i];
            var b = active[j];
            if (a.ModId == b.ModId)
                continue;
            if (Compare(a, b) is { } c)
                result.Add(c);
        }
        return result;
    }

    private static KeybindConflict? Compare(KeybindEntry a, KeybindEntry b)
    {
        var ka = a.Chord.Keys.ToHashSet();
        var kb = b.Chord.Keys.ToHashSet();
        bool sameKeys = ka.SetEquals(kb);
        bool sameMods = a.Chord.Mods == b.Chord.Mods;
        bool unconfirmed = a.Status == KeybindStatus.NeedsConfirmation || b.Status == KeybindStatus.NeedsConfirmation;
        bool game = a.IsGame || b.IsGame;

        if (sameKeys && sameMods)
        {
            bool sidesDiffer =
                a.Chord.Sides != KeySide.None && b.Chord.Sides != KeySide.None && a.Chord.Sides != b.Chord.Sides;
            if (sidesDiffer)
                return new(ConflictKind.Potential, a, b, L.T("core.keybinds.sameKeyButDifferentModifier"));
            if (unconfirmed)
                return new(ConflictKind.Potential, a, b, L.T("core.keybinds.matchesBindingHasNotYet"));
            if (game)
                return new(ConflictKind.Potential, a, b, L.T("core.keybinds.matchesDefaultGtaVControl"));
            if (ContextsOverlap(a.Context, b.Context) is true)
                return new(
                    ConflictKind.Direct,
                    a,
                    b,
                    a.Context == KeybindContext.Global || b.Context == KeybindContext.Global
                        ? L.T("core.keybinds.sameShortcutOneActionsWorks")
                        : L.T("core.keybinds.sameCombinationSameGameMode")
                );
            if (ContextsOverlap(a.Context, b.Context) is false)
                return null; // different known modes never overlap
            return new(ConflictKind.Potential, a, b, L.T("core.keybinds.sameShortcutUnknownWhichGame"));
        }

        // Nested shortcuts: E and Shift+E, F5 and RCtrl+F5, G and G+6.
        bool aInsideB = ka.IsSubsetOf(kb) && (a.Chord.Mods & b.Chord.Mods) == a.Chord.Mods;
        bool bInsideA = kb.IsSubsetOf(ka) && (a.Chord.Mods & b.Chord.Mods) == b.Chord.Mods;
        if ((aInsideB || bInsideA) && ContextsOverlap(a.Context, b.Context) is not false)
        {
            var (small, big) = aInsideB ? (a, b) : (b, a);
            return new(
                ConflictKind.Potential,
                a,
                b,
                L.T("core.keybinds.includesPressingShortcutMayAlso", big.Chord, small.Chord, small.Chord)
            );
        }
        return null;
    }

    /// <summary>true: the modes definitely overlap, false: definitely not, null: unknown.</summary>
    private static bool? ContextsOverlap(KeybindContext a, KeybindContext b)
    {
        if (a == KeybindContext.Global || b == KeybindContext.Global)
            return true;
        if (a == KeybindContext.Unknown || b == KeybindContext.Unknown)
            return null;
        return a == b;
    }

    /// <summary>State of each key: direct conflict > possible conflict > mod > game > free.</summary>
    public static Dictionary<int, KeyState> KeyStates(
        IReadOnlyList<KeybindEntry> entries,
        IReadOnlyList<KeybindConflict> conflicts
    )
    {
        var states = new Dictionary<int, KeyState>();
        void Raise(int vk, KeyState s)
        {
            if (!states.TryGetValue(vk, out var cur) || s > cur)
                states[vk] = s;
        }
        foreach (var e in entries)
        {
            foreach (var vk in e.Chord.Keys)
                Raise(Base(vk), e.IsGame ? KeyState.Game : KeyState.Mod);
        }
        foreach (var c in conflicts)
        foreach (var vk in c.A.Chord.Keys.Intersect(c.B.Chord.Keys))
            Raise(Base(vk), c.Kind == ConflictKind.Direct ? KeyState.Direct : KeyState.Potential);
        return states;
    }

    /// <summary>LCtrl/RCtrl and similar show on their own keys; a generic Ctrl shows on the left one.</summary>
    public static int Base(int vk) =>
        vk switch
        {
            0x10 => 0xA0,
            0x11 => 0xA2,
            0x12 => 0xA4,
            _ => vk,
        };

    /// <summary>
    /// Shortcuts that are free according to known data, for suggestions. Not a guarantee: keys may be used by
    /// mods with controls handled inside an ASI/DLL, or by other programs.
    /// </summary>
    public static List<KeyChord> SuggestFree(IReadOnlyList<KeybindEntry> entries, bool allowModifiers, int count = 8)
    {
        var used = entries.Select(e => (Keys: string.Join(",", e.Chord.Keys.Order()), e.Chord.Mods)).ToHashSet();
        var candidates = new List<KeyChord>();
        for (int f = 0x74; f <= 0x7B; f++)
            candidates.Add(new KeyChord(f)); // F5–F12
        for (int n = 0x60; n <= 0x69; n++)
            candidates.Add(new KeyChord(n)); // Num 0–9
        foreach (var k in new[] { 0x6A, 0x6B, 0x6D, 0x6F, 0x2D, 0x24, 0x21, 0x22, 0x23 })
            candidates.Add(new KeyChord(k));
        if (allowModifiers)
            for (int f = 0x70; f <= 0x7B; f++)
            {
                candidates.Add(new KeyChord(f, KeyMods.Ctrl));
                candidates.Add(new KeyChord(f, KeyMods.Shift));
            }
        return candidates
            .Where(c => !used.Contains((string.Join(",", c.Keys.Order()), c.Mods)))
            .Where(c => c.Mods != KeyMods.None || !entries.Any(e => e.Chord.Keys.Contains(c.Vk))) // without a modifier, the key is not taken at all
            .Take(count)
            .ToList();
    }
}
