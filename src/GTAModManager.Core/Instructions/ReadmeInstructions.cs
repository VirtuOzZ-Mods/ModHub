using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Instructions;

/// <summary>A game path from the instructions: a chain of archives (the first relative to the game root) and a folder inside the last one.</summary>
public sealed record GamePath(IReadOnlyList<string> Archives, string Dir)
{
    public string Display => string.Join("\\", Archives.Append(Dir).Where(p => p.Length > 0));
}

public sealed class InstructionPlan
{
    public string Kind { get; set; } = "";
    public List<ModPart> Parts { get; } = [];
    public List<OivOp> Ops { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>Archive files covered by the instructions.</summary>
    public HashSet<string> Used { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Generic parser for the author's readme instructions. Understands:
/// 1) "verb — what — where" steps: place/put/copy/paste/drag/move/replace/insert (and Russian equivalents),
///    quoted names, file names with extensions, "folder X", "contents of X / all files from X",
///    a game path with any depth of nested .rpf (on the same line or the following ones), PART/Step N sections;
/// 2) the OpenIV audio recipe: export an .awc bank to .oac, drop in .wav files, import back,
///    which becomes a replacement of individual sounds inside the bank.
/// Paths are never guessed: a folder or archive missing in the game stops the installation (MissingGamePathException).
/// </summary>
public static partial class ReadmeInstructions
{
    [GeneratedRegex(
        @"\b(place|put|paste|copy|drag|drop|move|replace|insert|помест\w*|полож\w*|скопир\w*|копир\w*|вставь\w*|встав\w*|перемест\w*|перетащ\w*|замени\w*|замен\w*|закин\w*)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex VerbRx();

    [GeneratedRegex(
        @"\ball\s+(?:the\s+)?(?:\.?\w+\s+)?(?:files\s+)?(?:from|in|contained\s+in)\b|\bcontents?\s+of\b|содержим\w*|\bвс[её]\s+из\b|\bвсе\s+файлы\s+из\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ContentsRx();

    [GeneratedRegex("\"([^\"]+)\"|«([^»]+)»|“([^”]+)”")]
    private static partial Regex QuotedRx();

    // A game file name without quotes: sports_saloon_4_jp_flat4.awc, player_two.yft, DUMP_VALVE.wav.
    [GeneratedRegex(
        @"(?<![\w\\/.])[\w\-]+(?:\.[\w\-]+)*\.(?:awc|ytd|ydd|yft|ymt|ydr|ybn|ymap|ytyp|ycd|ypt|ynv|yvr|meta|xml|dat|rpf|gxt2|rel|nametable|wav|asi|dll|ini|lua|cs|vb|oiv)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex FileTokenRx();

    // "the \"player_two\" folder", "folder \"X\"" and the Russian equivalent.
    [GeneratedRegex(@"\s+(folder|папк\w*|directory|dir)$", RegexOptions.IgnoreCase)]
    private static partial Regex FolderSuffixRx();

    [GeneratedRegex(@"^\s*(?:part|step|часть|шаг)\s*(?<n>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SectionRx();

    // Start of a game path: the game root or a known folder/archive followed by a separator.
    [GeneratedRegex(
        @"(?:[A-Za-z]:[\\/])?(?:[^\s""«»\\/]*[\\/])*?(?:grand theft auto v(?: enhanced| legacy)?|gta ?v|gtav|gta5|\(mods\)|mods|x64[a-z]?\.rpf|x64|update|common|scripts)[\\/][^\s""«»,;]*",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PathRx();

    [GeneratedRegex(@"\.oac\b|\bop(?:e)?n\s*-?formats?\b", RegexOptions.IgnoreCase)]
    private static partial Regex OacRx();

    [GeneratedRegex(@"(?<![\w\\/.])[\w\-]+\.awc\b", RegexOptions.IgnoreCase)]
    private static partial Regex AwcNameRx();

    [GeneratedRegex(
        @"^(samples?|previews?|demo|examples?|audio previews?|прослушивани\w*|пример\w*)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex PreviewFolderRx();

    /// <summary>The file sits in a folder of listening samples (Samples, Preview); these are not install files.</summary>
    public static bool InPreviewFolder(string path) => path.Split('\\')[..^1].Any(s => PreviewFolderRx().IsMatch(s));

    /// <summary>"Grand Theft Auto V\mods\update\x64\dlcpacks\x\dlc.rpf\x64\audio\sfx\dlc_ja16\" → archives + folder.</summary>
    public static GamePath? ParsePath(string raw)
    {
        var p = raw.Trim().Trim('"', '\'', '«', '»', '“', '”', '.', ',', ';', ':', ' ', ')').Replace('/', '\\');
        var parts = p.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 0 && parts[0].EndsWith(':'))
            parts.RemoveAt(0); // C:
        // Everything up to the game root is dropped ("…\Steam\steamapps\common\Grand Theft Auto V\").
        int root = parts.FindLastIndex(s =>
            Regex.IsMatch(s, @"^(grand theft auto v( enhanced| legacy)?|gta ?v|gtav|gta5)$", RegexOptions.IgnoreCase)
        );
        if (root >= 0)
            parts = parts[(root + 1)..];
        parts = parts.Where(s => !s.Equals("(mods)", StringComparison.OrdinalIgnoreCase)).ToList();
        while (parts.Count > 0 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(0);
        if (
            parts.Count == 0
            || parts.Any(s => s is "." or ".." || s.Contains(':') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        )
            return null;
        var archives = new List<string>();
        int start = 0;
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            {
                archives.Add(string.Join('\\', parts[start..(i + 1)]));
                start = i + 1;
            }
        return new GamePath(archives, string.Join('\\', parts[start..]));
    }

    public static InstructionPlan? Plan(IReadOnlyList<string> files, string docs)
    {
        if (string.IsNullOrWhiteSpace(docs) || files.Count == 0)
            return null;
        var lines = docs.Replace("\r", "").Split('\n');
        return AwcRecipe(files, docs, lines) ?? CopySteps(files, lines);
    }

    // ---------- OpenIV recipe for audio banks ----------

    private static InstructionPlan? AwcRecipe(IReadOnlyList<string> files, string docs, string[] lines)
    {
        if (!OacRx().IsMatch(docs))
            return null;
        var awc = AwcNameRx().Match(docs);
        if (!awc.Success)
            return null;
        var wavs = files
            .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !InPreviewFolder(f))
            .ToList();
        if (wavs.Count == 0)
            return null;
        // Archive containing the bank: the .rpf path from the instructions (the first one naming an archive rather than an extraction folder).
        GamePath? target = null;
        foreach (var line in lines)
        foreach (Match m in PathRx().Matches(line))
            if (target is null && ParsePath(m.Value) is { Archives.Count: > 0 } path)
                target = path;
        if (target is null)
            return null;
        // The same sound name in two folders (without a variant choice) is ambiguous.
        var duplicate = wavs.GroupBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        var plan = new InstructionPlan { Kind = "Звуки: замена в банке " + awc.Value };
        if (duplicate is not null)
        {
            plan.Errors.Add(
                L.T(
                    "core.instructions.soundExistsSeveralFoldersSo",
                    duplicate.Key,
                    string.Join(", ", duplicate.Select(Path.GetDirectoryName))
                )
            );
            return plan;
        }
        var awcPath = target.Dir.Length == 0 ? awc.Value : target.Dir + "\\" + awc.Value;
        OivOp op = new OivAwc(
            awcPath,
            wavs.Select(f => new OivAwcSound(f, Path.GetFileNameWithoutExtension(f))).ToList()
        );
        plan.Ops.Add(Wrap(target.Archives, op));
        foreach (var f in wavs)
        {
            plan.Used.Add(f);
            plan.Parts.Add(
                new ModPart("Звук", f, target.Display + "\\" + awc.Value + " | " + Path.GetFileNameWithoutExtension(f))
            );
        }
        return plan;
    }

    // ---------- "What → where" steps ----------

    private static InstructionPlan? CopySteps(IReadOnlyList<string> files, string[] lines)
    {
        var plan = new InstructionPlan { Kind = "Установка по инструкции автора" };
        string? section = null;
        var targets = new Dictionary<string, (GamePath Path, List<(string Source, string Inner)> Files, string Hint)>(
            StringComparer.OrdinalIgnoreCase
        );
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (SectionRx().Match(line) is { Success: true } sec)
                section = "part " + sec.Groups["n"].Value;
            if (!VerbRx().IsMatch(line))
                continue;

            // Where: a path on this line or on the next few lines.
            Match? where = null;
            int whereLine = -1;
            for (int j = i; j < Math.Min(lines.Length, i + 4) && where is null; j++)
                foreach (Match m in PathRx().Matches(lines[j]))
                    if (ParsePath(m.Value) is not null)
                    {
                        where = m;
                        whereLine = j;
                        break;
                    }
            if (where is null || ParsePath(where.Value) is not { } dest)
                continue;

            // What: quoted names and file names, except those that are part of the path itself.
            var objectText = whereLine == i ? line[..where.Index] + line[(where.Index + where.Length)..] : line;
            var names = QuotedRx()
                .Matches(objectText)
                .Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value.Trim())
                .Concat(FileTokenRx().Matches(QuotedRx().Replace(objectText, " ")).Select(m => m.Value))
                .Where(n => n.Length > 0 && ParsePath(n) is not { Archives.Count: > 0 })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count == 0)
                continue;
            bool contents = ContentsRx().IsMatch(objectText);
            foreach (var name in names)
            {
                bool folder =
                    contents
                    || FolderSuffixRx().IsMatch(name)
                    || !Path.HasExtension(name)
                    || Regex.IsMatch(
                        objectText,
                        Regex.Escape(name) + @"[""»”]?\s+(folder|папк)",
                        RegexOptions.IgnoreCase
                    );
                var clean = FolderSuffixRx().Replace(name, "").Trim();
                var found = Match(files, clean, folder, section);
                if (contents)
                    found = found
                        .Select(f => (f.Source, f.Rel.Contains('\\') ? f.Rel[(f.Rel.IndexOf('\\') + 1)..] : f.Rel))
                        .ToList();
                foreach (var (source, rel) in found)
                {
                    if (plan.Used.Contains(source))
                        continue;
                    var key = dest.Display;
                    if (!targets.TryGetValue(key, out var t))
                        targets[key] = t = (dest, [], Path.GetFileName(source));
                    var inner = dest.Dir.Length == 0 ? rel : dest.Dir + "\\" + rel;
                    t.Files.Add((source, inner));
                    plan.Used.Add(source);
                    plan.Parts.Add(
                        new ModPart(
                            (section is null ? "" : section.ToUpperInvariant() + ": ") + clean,
                            source,
                            key + "\\" + rel
                        )
                    );
                }
            }
        }
        if (plan.Used.Count == 0)
            return null;
        foreach (var (_, (path, list, hint)) in targets)
        {
            if (path.Archives.Count == 0)
            {
                // Loose files in the game folder (scripts\…, menyooStuff\…).
                plan.Ops.AddRange(list.Select(f => (OivOp)new OivAdd(f.Source, f.Inner)));
                continue;
            }
            // The folder inside the archive must already exist; otherwise the instructions are outdated, so we do not create it silently.
            var ops = new List<OivOp>();
            if (path.Dir.Length > 0)
                ops.Add(new OivRequire(path.Dir, hint));
            ops.AddRange(list.Select(f => (OivOp)new OivAdd(f.Source, f.Inner)));
            plan.Ops.Add(Wrap(path.Archives, ops.ToArray()));
        }
        return plan;
    }

    private static OivOp Wrap(IReadOnlyList<string> archives, params OivOp[] inner)
    {
        OivOp op = new OivArchive(archives[^1], false, "RPF7", inner);
        for (int i = archives.Count - 2; i >= 0; i--)
            op = new OivArchive(archives[i], false, "RPF7", [op]);
        return op;
    }

    /// <summary>Files/folder with this name; on multiple matches, those inside the current section folder (PART N).</summary>
    private static List<(string Source, string Rel)> Match(
        IReadOnlyList<string> files,
        string name,
        bool folder,
        string? section
    )
    {
        var result = new List<(string, string)>();
        foreach (var f in files)
        {
            var parts = f.Split('\\');
            if (folder)
            {
                var i = Array.FindIndex(parts, p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0 && i < parts.Length - 1)
                    result.Add((f, string.Join('\\', parts[i..])));
            }
            else if (parts[^1].Equals(name, StringComparison.OrdinalIgnoreCase))
                result.Add((f, parts[^1]));
        }
        if (section is not null)
        {
            var inSection = result
                .Where(r =>
                    r.Item1.Split('\\')
                        .Any(p =>
                            Regex.Replace(p, @"\s+", " ").Trim().Equals(section, StringComparison.OrdinalIgnoreCase)
                        )
                )
                .ToList();
            if (inSection.Count > 0 || result.Any(r => Regex.IsMatch(r.Item1, @"(?i)(^|\\)part\s*\d+\\")))
                return inSection;
        }
        return result;
    }
}
