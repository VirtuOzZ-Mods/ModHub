using System.Text;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Mods;

/// <summary>
/// Hotkeys a package will bring, for the Controls tab of the package screen (read-only; they are changed after
/// installation on the Hotkeys page). Reads the .ini files the package adds to the game folder as loose files and
/// recognizes keys with the same rules as the Hotkeys page. Files inside archives are not read.
/// </summary>
public static class PackageControls
{
    private const long MaxIniSize = 512 * 1024;

    public static List<ControlGroup> FromOiv(OivPackage package)
    {
        var groups = new List<ControlGroup>();
        var mod = new ModRef("", package.Info.Name);
        foreach (var add in package.Ops.OfType<OivAdd>())
        {
            if (!add.Target.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                continue;
            string text;
            try
            {
                if (package.SourceSize(add.Source) > MaxIniSize)
                    continue;
                using var stream = package.OpenSource(add.Source);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or OivFormatException or InvalidDataException)
            {
                continue;
            }
            var group = FromIni(text, add.Target.Replace('/', '\\'), mod);
            if (group is not null)
                groups.Add(group);
        }
        return groups;
    }

    /// <summary>One group per settings file: "key — action" rows for every recognized binding.</summary>
    public static ControlGroup? FromIni(string text, string relPath, ModRef mod)
    {
        var entries = IniKeybindParser.Parse(text, relPath, mod).Where(e => !e.Chord.IsEmpty).ToList();
        if (entries.Count == 0)
            return null;
        var rows = entries
            .Select(e => new ControlRow(Tokens(e.Chord), e.Action))
            .ToList();
        return new ControlGroup(relPath, rows, []);
    }

    private static List<KeyToken> Tokens(KeyChord chord)
    {
        var tokens = new List<KeyToken>();
        foreach (var part in chord.ToString().Split(" + "))
        {
            if (tokens.Count > 0)
                tokens.Add(new KeyToken(KeyTokenKind.Joiner, "+"));
            tokens.Add(new KeyToken(KeyTokenKind.Key, part));
        }
        return tokens;
    }
}
