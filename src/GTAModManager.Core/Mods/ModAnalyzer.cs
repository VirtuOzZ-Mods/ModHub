using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Mods;

public sealed record ModPart(string Kind, string Source, string Target);

/// <summary>
/// An optional component next to an OIV package (for example an optional ASI module): installed after the OIV
/// as a separate transaction. Optional and Note come from the README in the same folder (as the author instructs).
/// </summary>
public sealed record ExtraComponent(string Source, string Target, bool Optional, string Note)
{
    /// <summary>A matching .ini next to the .asi (installed together if not present in the game yet).</summary>
    public string? IniSource { get; init; }
}

public sealed class ModAnalysis
{
    public required string Title { get; init; }
    public string Description { get; set; } = "";

    /// <summary>What was found: "Add-on DLC", "ASI plugin", "Scripts", "OIV package inside the archive".</summary>
    public List<string> Kinds { get; } = [];
    public List<ModPart> Parts { get; } = [];
    public List<OivOp> Ops { get; } = [];

    /// <summary>Paths of .oiv files inside the archive that match the selected game (then the OIV is installed instead of the files).</summary>
    public List<string> NestedOiv { get; } = [];

    /// <summary>Components the author suggests installing after the OIV (ASI files outside the manual-install folder).</summary>
    public List<ExtraComponent> Extras { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Errors { get; } = [];

    /// <summary>What each add-on DLC pack in the mod contains (vehicles, engine sounds…), recognized without a README.</summary>
    public List<Dlc.DlcContent> DlcPacks { get; } = [];

    /// <summary>MP character clothing without a gender tag: installation waits for a choice (PedAssetChoices).</summary>
    public bool NeedsGender { get; set; }

    /// <summary>"Pick one" variant groups (Version 1/2, ON/OFF…) found in the archive.</summary>
    public List<Instructions.VariantGroup> Variants { get; } = [];

    /// <summary>Groups without a choice yet: installation waits for it (InstallChoices).</summary>
    public List<Instructions.VariantGroup> PendingChoices { get; } = [];

    /// <summary>The choice made: group key → variant key.</summary>
    public Dictionary<string, string> Chosen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Summary => Kinds.Count == 0 ? L.T("core.mods.notRecognized") : string.Join(" + ", Kinds.Distinct());
}

/// <summary>
/// Detects a regular mod (ZIP/RAR/7Z) by strict rules, without guessing:
/// dlc.rpf → add-on DLC in mods\update\x64\dlcpacks\&lt;name&gt;\ + an entry in dlclist.xml;
/// *.asi (+ matching .ini) → game root; scripts\ folder → the game's scripts\; *.oiv inside → installed as an OIV.
/// Legacy/Enhanced variants are chosen by folder names. Everything else is not installed and is listed.
/// </summary>
public static partial class ModAnalyzer
{
    private static readonly string[] EnhancedWords = ["enhanced", "gen9", "gen 9", "e&e", "expanded"];
    private static readonly string[] LegacyWords = ["legacy", "gen8", "gen 8", "classic", "original version"];
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "dlc",
        "dlcpacks",
        "x64",
        "update",
        "mods",
        "addon",
        "add-on",
        "files",
        "install",
        "main",
        "enhanced",
        "legacy",
        "gen8",
        "gen9",
    };
    private static readonly HashSet<string> NeverInstall = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".bat",
        ".cmd",
        ".ps1",
        ".vbs",
        ".js",
        ".msi",
        ".reg",
        ".lnk",
        ".scr",
        ".com",
    };

    public static ModAnalysis Analyze(ModArchive archive, GameInstall game)
    {
        var a = new ModAnalysis { Title = archive.Title };
        var excluded = archive
            .Files.Where(f =>
                f.Split('\\')[..^1]
                    .Any(d =>
                        new[]
                        {
                            "backup",
                            "backups",
                            "src",
                            "source",
                            "sources",
                            "optional",
                            "extras",
                            "old versions",
                        }.Contains(d, StringComparer.OrdinalIgnoreCase)
                    )
            )
            .ToArray();
        var files = archive.Files.Except(excluded, StringComparer.OrdinalIgnoreCase).ToList();
        if (excluded.Length > 0)
            a.Notes.Add(L.T("core.mods.backupsSourceFilesOptionalComponents", excluded.Length));
        a.Description = ReadReadme(archive, files);
        foreach (var s in archive.Skipped)
            a.Notes.Add(L.T("core.mods.skippedUnsafePathFileToo", s));

        // The mod is explicitly tagged for another game version ([Legacy], Enhanced Edition…): do not install, it could break the game.
        if (EditionLabel.Mismatch(a.Title, game.Edition, Path.GetFileName(archive.FilePath)) is { } wrongEdition)
        {
            a.Kinds.Add("Мод для другой версии игры");
            a.Errors.Add(wrongEdition);
            return a;
        }

        // An add-on car usually ships beside other ways to install the same car (Replace, FiveM, the Back Up of the
        // originals) and optional extras (alternative handling, livery templates). With a dlc.rpf present the add-on
        // is what gets installed; those folders are neither variants to choose from nor files to install.
        var besidePack = BesideAddonPack(files);
        if (besidePack.Count > 0)
        {
            files = files.Except(besidePack, StringComparer.OrdinalIgnoreCase).ToList();
            a.Notes.Add(
                L.T(
                    "core.mods.besideAddonPackSkipped",
                    string.Join(
                        ", ",
                        besidePack
                            .Select(f => f.Split('\\').First(s => BesidePackFolder().IsMatch(s)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                    )
                )
            );
        }

        // 0. Variants inside the archive (Version 1/2, Silent, ON/OFF): only the chosen one is installed; without a choice we wait for it.
        files = files.Where(f => !Instructions.Variants.InBackupFolder(f)).ToList();
        var groups = Instructions.Variants.Find(files);
        if (groups.Count > 0)
        {
            a.Variants.AddRange(groups);
            var saved = Instructions.InstallChoices.Get(archive.FilePath);
            foreach (var g in groups)
                if (
                    saved.TryGetValue(g.Key, out var c)
                    && g.Options.Any(o => o.Key.Equals(c, StringComparison.OrdinalIgnoreCase))
                )
                    a.Chosen[g.Key] = c;
                else
                    a.PendingChoices.Add(g);
            if (a.PendingChoices.Count > 0)
            {
                a.Kinds.Add("Нужен выбор варианта");
                a.Errors.Add(
                    L.T(
                        "core.mods.archiveContainsSeveralVariantsChoose",
                        string.Join(
                            "; ",
                            a.PendingChoices.Select(g =>
                                (g.Title.Length > 0 ? g.Title + ": " : "")
                                + string.Join(" / ", g.Options.Select(o => o.Label))
                            )
                        )
                    )
                );
                return a;
            }
            files = Instructions.Variants.Apply(files, groups, a.Chosen);
            foreach (var g in groups)
                a.Notes.Add(
                    L.T(
                        "core.mods.chosenVariant",
                        g.Title.Length > 0 ? g.Title : L.T("core.common.modVariant"),
                        g.Options.First(o => o.Key.Equals(a.Chosen[g.Key], StringComparison.OrdinalIgnoreCase)).Label
                    )
                );
        }

        // 1. An OIV inside the archive is installed as a whole package.
        var allOiv = PickEdition(
            files.Where(f => f.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase)).ToList(),
            game.Edition,
            byFileName: true
        );
        // Uninstaller packages ("…_Uninstall.oiv" and Russian equivalents) are not installed by default.
        static bool IsUninstaller(string f) =>
            Path.GetFileName(f).Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(f).Contains("удален", StringComparison.OrdinalIgnoreCase);
        var oivs = allOiv.Where(f => !IsUninstaller(f)).ToList();
        if (oivs.Count > 0)
        {
            a.Kinds.Add("OIV-пакет внутри архива");
            foreach (var oiv in oivs)
                if (
                    EditionLabel.Mismatch(Path.GetFileNameWithoutExtension(oiv), game.Edition, Path.GetFileName(oiv)) is
                    { } wrong
                )
                {
                    a.Errors.Add(wrong);
                    return a;
                }
            a.NestedOiv.AddRange(oivs);
            if (oivs.Count > 1)
                a.Notes.Add(
                    L.T(
                        "core.mods.archiveContainsMultipleOivPackages",
                        string.Join(", ", oivs.Select(Path.GetFileName))
                    )
                );
            a.Extras.AddRange(FindCompanions(archive, files, game));
            foreach (var x in a.Extras)
                a.Notes.Add(
                    global::GTAModManager
                        .Core.Localization.L.T(
                            "core.mods.installedAfterOiv",
                            (x.Optional ? L.T("core.mods.additionalOptional") : L.T("core.mods.advanced")),
                            Path.GetFileName(x.Source),
                            x.Note
                        )
                        .TrimEnd()
                );
            return a;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 2. Add-on DLC.
        var dlcs = PickEdition(
            files.Where(f => Path.GetFileName(f).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase)).ToList(),
            game.Edition
        );
        var packNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dlc in dlcs)
        {
            var name = PackName(archive, dlc);
            if (name is null)
            {
                a.Errors.Add(L.T("core.mods.couldNotDetermineDlcPack", dlc));
                continue;
            }
            if (packNames.TryGetValue(name, out var other))
            {
                a.Errors.Add(L.T("core.mods.twoVariantsDlcLeaveOne", name, other, dlc));
                continue;
            }
            var original = Path.Combine(game.Root, "update", "x64", "dlcpacks", name, "dlc.rpf");
            if (File.Exists(original) && !GameInstall.IsPlainRpf(original))
            {
                a.Errors.Add(L.T("core.mods.dlcNameMatchesOfficialRockstar", name));
                continue;
            }
            packNames[name] = dlc;
            used.Add(dlc);
            a.Parts.Add(new("Add-on DLC", dlc, $@"mods\update\x64\dlcpacks\{name}\dlc.rpf"));
            a.Ops.Add(new OivAdd(dlc, $@"update\x64\dlcpacks\{name}\dlc.rpf"));
            if (InspectPack(archive, dlc, name) is { } content)
            {
                a.DlcPacks.Add(content);
                if (DescribePack(content) is { } note)
                    a.Notes.Add(note);
            }
        }
        if (packNames.Count > 0)
        {
            var kinds = a.DlcPacks.Select(p => p.Kind).Where(k => k != Dlc.DlcPackKind.Unknown).Distinct().ToList();
            if (kinds.Count > 0 && a.DlcPacks.Count == packNames.Count)
                a.Kinds.AddRange(kinds.Select(KindLabel));
            else
                a.Kinds.Add(packNames.Count == 1 ? "Add-on DLC" : $"Add-on DLC ×{packNames.Count}");
            a.Ops.Add(
                new OivArchive(
                    @"update\update.rpf",
                    false,
                    "RPF7",
                    [
                        new OivXml(
                            @"common\data\dlclist.xml",
                            packNames
                                .Keys.Select(n => new OivXmlOp(
                                    OivXmlKind.Add,
                                    "/SMandatoryPacksData/Paths",
                                    "Last",
                                    $"<Item>dlcpacks:/{n}/</Item>"
                                ))
                                .ToList()
                        ),
                    ]
                )
            );
        }

        // 3. scripts\ folder → the game's scripts\ (inner structure preserved).
        bool AsiOrConfig(string file) =>
            files.Any(asi =>
                asi.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    Path.GetDirectoryName(asi),
                    Path.GetDirectoryName(file),
                    StringComparison.OrdinalIgnoreCase
                )
                && Path.GetFileNameWithoutExtension(asi)
                    .Equals(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)
            );
        var scriptFiles = PickEdition(
            files.Where(f => !used.Contains(f) && ScriptsIndex(f) >= 0 && !AsiOrConfig(f)).ToList(),
            game.Edition
        );
        foreach (var f in scriptFiles)
        {
            if (NeverInstall.Contains(Path.GetExtension(f)))
                continue;
            var parts = f.Split('\\');
            var target = "scripts\\" + string.Join('\\', parts[(ScriptsIndex(f) + 1)..]);
            used.Add(f);
            a.Parts.Add(new("Скрипт", f, target));
            a.Ops.Add(new OivAdd(f, target));
        }
        // 3a. A ScriptHookVDotNet mod in its own folder (with Tracks\, Settings\…) → scripts\<folder>\ as a whole.
        var scriptFolders = FindScriptFolders(archive, files.Where(f => !used.Contains(f)).ToList(), game.Edition);
        foreach (var folder in scriptFolders)
            foreach (var f in folder.Files)
            {
                var target = folder.Target(f);
                used.Add(f);
                a.Parts.Add(new("Скрипт", f, target));
                a.Ops.Add(new OivAdd(f, target));
            }
        if (scriptFiles.Count > 0 || scriptFolders.Count > 0)
            a.Kinds.Add("Скрипты");

        // 4. ASI plugins and their .ini files (to the game root).
        var asis = PickEdition(
            files.Where(f => !used.Contains(f) && f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).ToList(),
            game.Edition
        );
        foreach (var asi in asis)
        {
            var name = Path.GetFileName(asi);
            if (game.WhyProtected(name) is { } why)
            {
                a.Errors.Add($"ASI {name}: {why}.");
                continue;
            }
            used.Add(asi);
            a.Parts.Add(new("ASI-плагин", asi, name));
            a.Ops.Add(new OivAdd(asi, name));
            var dir = Path.GetDirectoryName(asi) ?? "";
            foreach (
                var cfg in files.Where(f =>
                    !used.Contains(f)
                    && string.Equals(Path.GetDirectoryName(f) ?? "", dir, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileNameWithoutExtension(f)
                        .Equals(Path.GetFileNameWithoutExtension(asi), StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(f).ToLowerInvariant() is ".ini" or ".xml" or ".json" or ".cfg"
                )
            )
            {
                used.Add(cfg);
                a.Parts.Add(new("Настройки ASI", cfg, Path.GetFileName(cfg)));
                a.Ops.Add(new OivAdd(cfg, Path.GetFileName(cfg)));
            }
        }
        if (asis.Count > 0)
            a.Kinds.Add(asis.Count == 1 ? "ASI-плагин" : global::System.String.Format("ASI-плагины ×{0}", asis.Count));

        // 4a. Fixed recipe for Menyoo (trainer menu with no equivalent): the whole menyooStuff folder goes to the game root next to Menyoo.asi.
        //     The user's own menyooConfig.ini (settings and keys) is never overwritten.
        if (asis.Any(f => Path.GetFileName(f).Equals("Menyoo.asi", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var f in files.Where(f => !used.Contains(f)))
            {
                var parts = f.Split('\\');
                int i = Array.FindIndex(parts, p => p.Equals("menyooStuff", StringComparison.OrdinalIgnoreCase));
                if (i < 0 || i == parts.Length - 1)
                    continue;
                var target = string.Join('\\', parts[i..]);
                used.Add(f);
                if (
                    target.Equals(@"menyooStuff\menyooConfig.ini", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(game.Root, target))
                )
                    continue;
                a.Parts.Add(new("Menyoo", f, target));
                a.Ops.Add(new OivAdd(f, target));
            }
        }

        // 5. Everything else is not installed automatically. Variant files for another game version are not "unrecognized".
        var otherEdition = files
            .Where(f => IsOtherEdition(f, game.Edition))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (otherEdition.Count > 0 && used.Count > 0)
            a.Notes.Add(
                $"Выбран вариант для {game.Edition}; файлы для другой версии игры пропущены ({otherEdition.Count})."
            );
        var rest = files.Where(f => !used.Contains(f) && !IsDoc(f) && !otherEdition.Contains(f)).ToList();
        var dangerous = rest.Where(f => NeverInstall.Contains(Path.GetExtension(f))).ToList();
        if (dangerous.Count > 0)
            a.Notes.Add(
                L.T(
                    "core.mods.windowsProgramsScriptsNotRun",
                    string.Join(", ", dangerous.Take(5).Select(Path.GetFileName))
                )
            );
        var leftovers = rest.Except(dangerous).ToList();
        var resources = leftovers
            .Where(f =>
                Path.GetExtension(f).ToLowerInvariant()
                    is ".yft"
                        or ".ytd"
                        or ".ydr"
                        or ".ydd"
                        or ".ymt"
                        or ".meta"
                        or ".awc"
                        or ".ycd"
            )
            .ToList();
        // Character and clothing replacements: per the author's instructions or by standard MP clothing slot names.
        if (
            resources.Count > 0
            && a.Ops.Count == 0
            && a.NestedOiv.Count == 0
            && Peds.PedAssets.Plan(
                archive,
                leftovers,
                Peds.PedAssets.Docs(archive, files),
                Peds.PedAssetChoices.Get(archive.FilePath)
            )
                is { } ped
        )
        {
            a.Kinds.Add(ped.Kind);
            a.NeedsGender = ped.NeedsGender;
            a.Errors.AddRange(ped.Errors);
            // Slots already taken by other mods (per History): the new mod will replace their files.
            if (Library.AppSettings.Load().OpenLibrary() is { } library && ped.Files.Count > 0)
            {
                var occupied = Peds.PedAccessories.Occupied(
                    library,
                    game.Edition,
                    ped.Files.Select(f =>
                        "mods\\" + string.Join("\\", new[] { f.Outer, f.Nested, f.Inner }.Where(x => x.Length > 0))
                    )
                );
                foreach (var byMod in occupied.GroupBy(o => o.Value))
                    a.Notes.Add(
                        L.T(
                            "core.mods.alreadyUsedByModMod",
                            byMod.Key,
                            string.Join(
                                ", ",
                                byMod
                                    .Select(o => Peds.PedAccessories.Describe(o.Key)?.Slot ?? Path.GetFileName(o.Key))
                                    .Distinct()
                                    .Take(6)
                            )
                        )
                    );
            }
            a.Notes.AddRange(ped.Notes);
            foreach (var f in ped.Files)
            {
                a.Parts.Add(
                    new(
                        f.What,
                        f.Source,
                        string.Join(" | ", new[] { f.Outer, f.Nested, f.Inner }.Where(x => x.Length > 0))
                    )
                );
                used.Add(f.Source);
            }
            if (ped.Files.Count > 0 && ped.Errors.Count == 0)
                a.Ops.AddRange(ped.Ops());
            resources = []; // character replacements were handled by PedAssets: its explanation is already in Notes/Errors
            // Character files (including those waiting for a gender choice) and FiveM service files were explained above.
            leftovers = leftovers
                .Where(r =>
                    !used.Contains(r)
                    && !Peds.PedAssets.IsAsset(r)
                    && Path.GetExtension(r).ToLowerInvariant() is not (".meta" or ".lua")
                )
                .ToList();
        }
        // 6. The author's readme instructions: whatever the rules above did not cover (sounds in an .awc bank, files inside game archives).
        var explained = leftovers.Where(f => !used.Contains(f)).ToList();
        if (
            explained.Count > 0
            && Instructions.ReadmeInstructions.Plan(explained, Peds.PedAssets.Docs(archive, archive.Files.ToList()))
                is { } instr
        )
        {
            a.Kinds.Add(instr.Kind);
            a.Errors.AddRange(instr.Errors);
            a.Notes.AddRange(instr.Notes);
            a.Parts.AddRange(instr.Parts);
            if (instr.Errors.Count == 0)
                a.Ops.AddRange(instr.Ops);
            used.UnionWith(instr.Used);
            leftovers = leftovers.Where(f => !instr.Used.Contains(f)).ToList();
            resources = resources.Where(f => !instr.Used.Contains(f)).ToList();
        }
        // Listening samples (Samples, Preview) are not install files.
        leftovers = leftovers.Where(f => !Instructions.ReadmeInstructions.InPreviewFolder(f)).ToList();
        resources = resources.Where(f => !Instructions.ReadmeInstructions.InPreviewFolder(f)).ToList();
        if (resources.Count > 0)
            a.Notes.Add(L.T("core.mods.modelTextureReplacementFilesOutside", resources.Count));
        var unknown = leftovers.Except(resources).ToList();
        if (unknown.Count > 0)
            a.Notes.Add(
                L.T(
                    "core.mods.notRecognizedWillNotInstalled",
                    unknown.Count,
                    string.Join(", ", unknown.Take(6).Select(Path.GetFileName)),
                    (unknown.Count > 6 ? " …" : "")
                )
            );

        foreach (
            var duplicate in a.Parts.GroupBy(p => p.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
        )
            a.Errors.Add(L.T("core.mods.severalVariantsTargetSelectRequired", duplicate.Key));
        // Installable content exists only in the other game version's folder (MPCharactersOverhaul: only "Legacy\…" when installing on Enhanced).
        if (a.Ops.Count == 0 && a.Errors.Count == 0 && otherEdition.Any(f => !IsDoc(f)))
            a.Errors.Add(
                L.T(
                    "core.mods.archiveOnlyContainsFilesGta",
                    game.Edition == GameEdition.Enhanced ? "Legacy" : "Enhanced",
                    game.Edition
                )
            );
        if (a.Ops.Count == 0 && a.Errors.Count == 0)
            a.Errors.Add(
                "Не найдено ничего, что можно установить автоматически (dlc.rpf, .asi, папка scripts или .oiv). "
                    + "Если это мод-замена, нужен ручной путь установки по инструкции автора."
            );
        return a;
    }

    [GeneratedRegex(
        @"^(replace|replacement|replace version|back ?up|backups?|original files|fivem|fivem version|different handlings?|alt(ernative)? handlings?|handlings|livery templates?|templates?)$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex BesidePackFolder();

    /// <summary>
    /// Files in Replace / Back Up / FiveM / alternative handling / template folders of a mod that has an add-on
    /// dlc.rpf outside them. Empty when the mod has no add-on pack (then such folders keep their usual meaning).
    /// </summary>
    private static List<string> BesideAddonPack(List<string> files)
    {
        static int Beside(string f)
        {
            var dirs = f.Split('\\')[..^1];
            return Array.FindIndex(dirs, d => BesidePackFolder().IsMatch(d.Trim()));
        }
        bool hasPack = files.Any(f =>
            Path.GetFileName(f).Equals("dlc.rpf", StringComparison.OrdinalIgnoreCase) && Beside(f) < 0
        );
        return hasPack ? files.Where(f => Beside(f) >= 0).ToList() : [];
    }

    /// <summary>Looks inside a dlc.rpf of the mod; null when it cannot be read (encrypted table, damaged or too large).</summary>
    public static Dlc.DlcContent? InspectPack(ModArchive archive, string dlcPath, string pack)
    {
        try
        {
            using var source = archive.OpenSource(dlcPath);
            if (source.CanSeek)
                return Dlc.DlcInspector.Inspect(source, pack);
            if (archive.SourceSize(dlcPath) > 1L << 30)
                return null;
            using var copy = new MemoryStream();
            source.CopyTo(copy);
            copy.Position = 0;
            return Dlc.DlcInspector.Inspect(copy, pack);
        }
        catch (Exception ex)
            when (ex
                    is RpfException
                        or OivFormatException
                        or IOException
                        or InvalidDataException
                        or NotSupportedException
            )
        {
            return null;
        }
    }

    /// <summary>A pack kind for the analysis summary and the My Mods sections.</summary>
    public static string KindLabel(Dlc.DlcPackKind kind) =>
        kind switch
        {
            Dlc.DlcPackKind.Vehicles => L.T("core.mods.kindAddonVehicle"),
            Dlc.DlcPackKind.EngineSounds => L.T("core.mods.kindEngineSound"),
            Dlc.DlcPackKind.Peds => L.T("core.mods.kindAddonPed"),
            Dlc.DlcPackKind.Map => L.T("core.mods.kindAddonMap"),
            _ => "Add-on DLC",
        };

    private static string? DescribePack(Dlc.DlcContent content) =>
        content.Kind switch
        {
            Dlc.DlcPackKind.Vehicles => L.T(
                "core.mods.packVehicles",
                content.Pack,
                string.Join(", ", content.Vehicles.Select(v => v.Model).Take(12))
            ),
            Dlc.DlcPackKind.EngineSounds => L.T(
                "core.mods.packEngineSounds",
                content.Pack,
                string.Join(", ", content.EngineSounds.Select(s => s.Name).Take(12))
            ),
            _ => null,
        };

    /// <summary>If files are split into Legacy/Enhanced folders, keeps the variant for the selected version.</summary>
    public static List<string> PickEdition(List<string> paths, GameEdition edition, bool byFileName = false)
    {
        var ours = edition == GameEdition.Enhanced ? EnhancedWords : LegacyWords;
        var theirs = edition == GameEdition.Enhanced ? LegacyWords : EnhancedWords;
        // For packages (Legacy.oiv / Enhanced.oiv) the file name itself also names the game version.
        bool Has(string p, string[] words)
        {
            var segs = p.Split('\\');
            var scope = byFileName ? segs[..^1].Append(Path.GetFileNameWithoutExtension(segs[^1])) : segs[..^1];
            return scope.Any(seg => words.Any(w => seg.Contains(w, StringComparison.OrdinalIgnoreCase)));
        }
        var mine = paths.Where(p => Has(p, ours) && !Has(p, theirs)).ToList();
        var neutral = paths.Where(p => !Has(p, ours) && !Has(p, theirs)).ToList();
        // There is an explicit variant for our version: take it plus neutral files; drop the other variant.
        return mine.Count > 0 ? mine.Concat(neutral).ToList() : neutral;
    }

    /// <summary>The file sits in a variant folder for the other game version (Legacy when installing on Enhanced and vice versa).</summary>
    private static bool IsOtherEdition(string path, GameEdition edition)
    {
        var ours = edition == GameEdition.Enhanced ? EnhancedWords : LegacyWords;
        var theirs = edition == GameEdition.Enhanced ? LegacyWords : EnhancedWords;
        var dirs = path.Split('\\')[..^1];
        return dirs.Any(seg => theirs.Any(w => seg.Contains(w, StringComparison.OrdinalIgnoreCase)))
            && !dirs.Any(seg => ours.Any(w => seg.Contains(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static int ScriptsIndex(string path)
    {
        var parts = path.Split('\\');
        for (int i = 0; i < parts.Length - 1; i++)
            if (parts[i].Equals("scripts", StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static bool IsDoc(string f) =>
        Path.GetExtension(f).ToLowerInvariant()
            is ".txt"
                or ".md"
                or ".nfo"
                or ".pdf"
                or ".url"
                or ".png"
                or ".jpg"
                or ".jpeg"
                or ".gif"
                or ".webp"
                or ".html"
                or ".htm"
                or ".rtf"
                or ".docx";

    /// <summary>Pack name: the folder containing dlc.rpf if meaningful; otherwise nameHash/deviceName from setup2.xml inside the pack.</summary>
    private static string? PackName(ModArchive archive, string dlcPath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(dlcPath) ?? "");
        if (!string.IsNullOrEmpty(parent) && !GenericFolders.Contains(parent) && !parent.Contains(' ') && Valid(parent))
            return parent;
        try
        {
            var bytes = archive.Read(dlcPath);
            using var rpf = Rpf7Archive.Open(new MemoryStream(bytes), leaveOpen: false);
            if (rpf.Find("setup2.xml") is not RpfBinaryFile setup || setup.IsEncrypted != 0)
                return Valid(parent) ? parent : null;
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(Encoding.UTF8.GetString(rpf.ReadContent(setup)).TrimStart('﻿'));
            var hash = doc.SelectSingleNode("//nameHash")?.InnerText.Trim();
            if (Valid(hash))
                return hash;
            var device = doc.SelectSingleNode("//deviceName")?.InnerText.Trim();
            if (
                device is not null
                && device.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase)
                && Valid(device[4..])
            )
                return device[4..];
        }
        catch (Exception ex) when (ex is RpfException or XmlException or OivFormatException or IOException) { }
        return !string.IsNullOrEmpty(parent) && Valid(parent) && !GenericFolders.Contains(parent) ? parent : null;
    }

    private static bool Valid(string? name) =>
        name is { Length: > 0 and <= 64 } && PackRegex().IsMatch(name) && !name.Contains("..");

    [GeneratedRegex(@"^[A-Za-z0-9_\-. ]+$")]
    private static partial Regex PackRegex();

    [GeneratedRegex(
        @"(?i)\b(optional|not required|if you (want|need)|you can skip)\b|необязат|по желанию|если хотите|если нужн"
    )]
    private static partial Regex OptionalWords();

    [GeneratedRegex(@"(?i)manual|ручн|copy.?manually|without.?oiv")]
    private static partial Regex ManualFolder();

    /// <summary>
    /// ASI files next to an OIV package: outside the manual-install folder (which duplicates the OIV content), taking
    /// Legacy/Enhanced folders into account. Required flag and description come from the README in the same folder (or the general README by file name).
    /// </summary>
    private static List<ExtraComponent> FindCompanions(ModArchive archive, List<string> files, GameInstall game)
    {
        var result = new List<ExtraComponent>();
        // The general analysis drops optional/extras folders; this is exactly where add-ons are looked for. Sources and backups are not.
        var pool = archive
            .Files.Where(f =>
                !f.Split('\\')[..^1]
                    .Any(d =>
                        d.Equals("backup", StringComparison.OrdinalIgnoreCase)
                        || d.Equals("backups", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("src", StringComparison.OrdinalIgnoreCase)
                        || d.StartsWith("source", StringComparison.OrdinalIgnoreCase)
                        || d.Equals("old versions", StringComparison.OrdinalIgnoreCase)
                    )
            )
            .ToList();
        var asis = PickEdition(
            pool.Where(f =>
                    f.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)
                    && !f.Split('\\')[..^1].Any(d => ManualFolder().IsMatch(d))
                )
                .ToList(),
            game.Edition
        );
        foreach (var asi in asis)
        {
            var dir = Path.GetDirectoryName(asi) ?? "";
            var name = Path.GetFileName(asi);
            var docs = pool.Where(f =>
                    (Path.GetDirectoryName(f) ?? "").Equals(dir, StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".nfo"
                )
                .OrderBy(f =>
                    System.Text.RegularExpressions.Regex.IsMatch(
                        Path.GetFileNameWithoutExtension(f),
                        @"(?i)(^|[_\-. ])(ru|rus|russian)$"
                    )
                        ? 0
                        : 1
                ) // Russian README first
                .ToList();
            string text = string.Join("\n", docs.Select(d => ReadSmallText(archive, d)));
            // No README in the folder: look for a mention of the file in any archive documentation.
            if (text.Length == 0)
                text = string.Join(
                    "\n",
                    pool.Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md")
                        .Select(d => ReadSmallText(archive, d))
                        .Where(t =>
                            t.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                        )
                );
            bool optional =
                OptionalWords().IsMatch(text)
                || dir.Contains("optional", StringComparison.OrdinalIgnoreCase)
                || dir.Contains("extra", StringComparison.OrdinalIgnoreCase);
            // Description: the first meaningful line mentioning the file (or the first line of the folder README).
            var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 20).ToList();
            var note = lines.FirstOrDefault(l =>
                l.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)
                && !l.StartsWith("N.O.S", StringComparison.Ordinal)
            )
                ?? lines.Skip(1).FirstOrDefault()
                ?? "";
            if (note.Length > 220)
                note = note[..217] + "…";
            var ini = pool.FirstOrDefault(f =>
                f.Equals(
                    Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + ".ini"),
                    StringComparison.OrdinalIgnoreCase
                )
            );
            result.Add(new ExtraComponent(asi, name, optional, note) { IniSource = ini });
        }
        return result;
    }

    private static string ReadSmallText(ModArchive archive, string path)
    {
        try
        {
            if (archive.SourceSize(path) > 256 * 1024)
                return "";
            using var reader = new StreamReader(archive.OpenSource(path), Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return "";
        }
    }

    private static string ReadReadme(ModArchive archive, List<string> files)
    {
        var readme = files
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".txt" or ".md" or ".nfo")
            .OrderByDescending(f =>
                Path.GetFileName(f).Contains("readme", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).Contains("инструкц", StringComparison.OrdinalIgnoreCase)
            )
            .ThenBy(f => f.Count(c => c == '\\'))
            .FirstOrDefault();
        if (readme is null || archive.SourceSize(readme) > 1 << 20)
            return "";
        try
        {
            var text = Encoding.UTF8.GetString(archive.Read(readme)).TrimStart('﻿');
            if (text.Contains('�'))
            {
                // Older Russian READMEs are often in Windows-1251.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding(1251).GetString(archive.Read(readme));
            }
            return $"{Path.GetFileName(readme)}:\n\n" + (text.Length > 6000 ? text[..6000] + "\n…" : text);
        }
        catch (Exception ex)
            when (ex is IOException or OivFormatException or ArgumentException or NotSupportedException)
        {
            return "";
        }
    }
}
