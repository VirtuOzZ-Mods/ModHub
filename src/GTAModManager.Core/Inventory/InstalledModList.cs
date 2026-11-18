using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Mods;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Inventory;

/// <summary>My Mods is an installation registry, not a disk inventory or an integrity scan.</summary>
public static class InstalledModList
{
    /// <summary>Canonical group names of Mod Hub's own components (stable keys for labels and mod lists).</summary>
    public const string BaseName = "База для моддинга";
    public const string PedSelectorName = "Ped Selector";

    /// <summary>Engine sound changes of stock cars and of cars that have no card of their own.</summary>
    public const string EngineSoundsName = "Звук двигателя";
    const string PedSelectorPrefix = "GMMPedSelector";

    // Menu operations that do not touch the menu's own files (connecting an existing pack edits only
    // update.rpf), recognized by their title in any language, including titles from before "GMM" was dropped.
    static readonly Lazy<HashSet<string>> PedSelectorTitles = new(() =>
        new[]
        {
            "core.peds.installingGmmPedSelector",
            "core.peds.connectingPedSelectorPack",
            "core.peds.updatingGmmPedSelectorList",
        }
            .SelectMany(k => Localization.LocalizationService.Current.AllValues(k))
            .Append("Установка GMM PED Selector")
            .Append("Подключение пака Ped Selector")
            .Append("Обновление списка GMM PED Selector")
            .ToHashSet(StringComparer.Ordinal)
    );

    public static InventoryReport Read(
        GameInstall game,
        ModLabels labels,
        BackupLibrary? library = null,
        string? stateDirectory = null
    )
    {
        var suites = new NveStore(game, stateDirectory).Active();
        var groups = new Dictionary<string, ModGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var suite in suites)
            groups[suite.ProductTitle] = new()
            {
                Name = suite.ProductTitle,
                Kind = "Установлено через Mod Hub",
                Origin = ModOrigin.ModHub,
                Recognized = true,
                Category = ModCategories.ForSuite(suite.ProductTitle),
            };
        var records = library?.Records(game.Edition.ToString()).ToList() ?? [];
        var undone = records.Where(r => r.Undone).Select(r => r.Id).ToHashSet();
        var journals = game.CreateEngine()
            .History()
            .Where(j =>
                j.State == TxState.Completed
                && !undone.Contains(j.Id)
                && j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase)
                && j.Title is { Length: > 0 }
                && !j.Title.StartsWith("Отмена:", StringComparison.Ordinal)
                && !j.Title.StartsWith("Правки архивов игры:", StringComparison.Ordinal)
                // Hotkey changes belong to the Hotkeys page; the edited file stays with the mod that installed it.
                && !ModNames.IsHotkeyEdit(j.Title)
            )
            .OrderBy(j => j.Id)
            .ToList();
        var operations = new Dictionary<string, List<(string Id, string Title)>>(StringComparer.OrdinalIgnoreCase);
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // An engine sound change rewrites the car's dlc.rpf but does not take the car's files over.
        var soundChanges = journals
            .Select(j => (j, change: Dlc.EngineSounds.Parse(j.Title!)))
            .Where(x => x.change is not null)
            .ToDictionary(x => x.j.Id, x => x.change!.Value);
        foreach (var j in journals.Where(j => !soundChanges.ContainsKey(j.Id)))
        foreach (var op in j.Ops)
            owner[op.Rel] = j.Id;
        var groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var assignments = new List<(string Group, Dlc.SoundAssignment Change)>();
        foreach (var journal in journals)
        {
            if (soundChanges.TryGetValue(journal.Id, out var change))
            {
                // Belongs to the card of the mod that installed the car's pack and is undone together with it.
                // Stock cars (update.rpf) and cars installed without Mod Hub share one "Engine sounds" card.
                string? carGroup = journal
                    .Ops.Where(o => o.Rel.Contains(@"\dlcpacks\", StringComparison.OrdinalIgnoreCase))
                    .Select(o => owner.GetValueOrDefault(o.Rel))
                    .OfType<string>()
                    .Select(id => groupOf.GetValueOrDefault(id))
                    .FirstOrDefault(g => g is not null);
                string target = carGroup ?? EngineSoundsName;
                if (carGroup is null && !groups.ContainsKey(EngineSoundsName))
                    groups[EngineSoundsName] = new()
                    {
                        Name = EngineSoundsName,
                        Kind = "Установлено через Mod Hub",
                        Origin = ModOrigin.ModHub,
                        Recognized = true,
                        Builtin = true,
                    };
                operations.TryAdd(target, []);
                operations[target].Add((journal.Id, journal.Title!));
                assignments.Add((target, new(journal.Id, change.Model, change.Sound)));
                continue;
            }
            // Suite state is authoritative: all OIV parts, options and runtime replacements share its card.
            string? suiteName = new[]
            {
                NveBundle.LcppHdTitle,
                NveBundle.LcppTitle,
                "NaturalVision Evolved",
                "NaturalVision Enhanced",
            }.FirstOrDefault(name => journal.Title!.StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (
                suiteName is not null
                && suites.Any(s => s.ProductTitle.Equals(suiteName, StringComparison.OrdinalIgnoreCase))
            )
                continue;
            string name = GroupName(journal, suiteName);
            groupOf[journal.Id] = name;
            // Every installation of the mod is undone on "Remove", including ones whose files a later update replaced.
            operations.TryAdd(name, []);
            operations[name].Add((journal.Id, journal.Title!));
            var files = journal
                .Ops.Where(o =>
                    o.PostHash is not null
                    && owner.GetValueOrDefault(o.Rel) == journal.Id
                    && !o.Rel.StartsWith("ModHubState\\", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            if (files.Count == 0)
                continue;
            var anchors = files
                .Where(f =>
                    Path.GetExtension(f.Rel).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(f.Rel).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(f.Rel).Equals(".rpf", StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
            // A few existence probes hide removed installs; do not enumerate, open or hash their contents.
            if (!(anchors.Count > 0 ? anchors : files).Any(f => File.Exists(Path.Combine(game.Root, f.Rel))))
                continue;
            string title = journal.Title!;
            bool builtin = name is BaseName or PedSelectorName;
            if (!groups.TryGetValue(name, out var group))
                groups[name] = group = new()
                {
                    Name = name,
                    Kind = name == BaseName ? name : "Установлено через Mod Hub",
                    Origin = ModOrigin.ModHub,
                    Recognized = true,
                    Builtin = builtin,
                    // Built-in components have no mod page of their own.
                    Url = builtin ? null : labels.UrlFor(name) ?? labels.UrlFor(ModNames.Pretty(title)),
                };
            // Preserve installation fingerprints for export, without reading the installed files.
            group.Files.AddRange(files.Select(f => new ModFileItem(f.Rel, 0, f.PostHash!, ModOrigin.ModHub)));
        }
        foreach (var group in groups.Values)
            if (operations.TryGetValue(group.Name, out var ids))
                foreach (var (id, title) in ids)
                {
                    group.Operations.Add(id);
                    group.OperationTitles[id] = title;
                }
        foreach (var (name, change) in assignments)
            if (groups.TryGetValue(name, out var group))
                group.SoundAssignments.Add(change);
        var entries = records
            .Where(r => !r.Undone)
            .ToDictionary(
                r => r.Id,
                r => r.Archives.SelectMany(a => a.Entries.Select(e => a.Rel + "\\" + e.Path)).ToList(),
                StringComparer.OrdinalIgnoreCase
            );
        foreach (var group in groups.Values.Where(g => !suites.Any(s => s.ProductTitle == g.Name)))
        {
            if (group.Name == EngineSoundsName)
            {
                group.Category = ModCategory.Sounds;
                continue;
            }
            foreach (var file in group.Files.Select(f => f.Rel).Where(IsPackFile))
            {
                string pack = file.Split('\\')[^2];
                if (Dlc.EngineSounds.Inspect(Path.Combine(game.Root, file), pack) is { } content)
                    group.Packs.Add(new(pack, file, content));
            }
            var inner = group.Operations.SelectMany(id => entries.GetValueOrDefault(id) ?? []);
            group.Category = ModCategories.Classify(
                group.Name,
                group.Files.Select(f => f.Rel).Concat(inner),
                group.Packs.Select(p => p.Content)
            );
        }
        return new(groups.Values.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), [], []);
    }

    /// <summary>An add-on pack archive: mods\update\x64\dlcpacks\&lt;pack&gt;\dlc.rpf or its onigiri copy.</summary>
    static bool IsPackFile(string rel) =>
        rel.EndsWith(@"\dlc.rpf", StringComparison.OrdinalIgnoreCase)
        && (
            rel.StartsWith(@"mods\update\x64\dlcpacks\", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(@"onigiri\dlcpacks\", StringComparison.OrdinalIgnoreCase)
        )
        && rel.Count(c => c == '\\') is 5 or 3;

    static string GroupName(TxJournal journal, string? suiteName)
    {
        string title = journal.Title!;
        // Every operation on the menu's own files (ASI, INI, model list, looks folder) belongs to one card:
        // installing the menu, connecting an existing pack and list updates alike. Add-on ped packs keep theirs.
        bool pedSelector =
            PedSelectorTitles.Value.Contains(journal.Title!)
            || journal.Ops.Any(o =>
                o.Rel.StartsWith(PedSelectorPrefix, StringComparison.OrdinalIgnoreCase)
                || o.Rel.Equals(@"GMMAddonPeds\selector-owner.txt", StringComparison.OrdinalIgnoreCase)
            );
        string name = suiteName
            ?? (
                pedSelector ? PedSelectorName
                : journal.Ops.Any(o =>
                    o.Rel.Equals(@"scripts\N.O.S - Nitro Overdrive System.dll", StringComparison.OrdinalIgnoreCase)
                    || o.Rel.Equals("NOSUnderground.Graphics.asi", StringComparison.OrdinalIgnoreCase)
                )
                    ? "N.O.S - Nitro Overdrive System"
                : journal.Ops.Any(o =>
                    o.Rel.Equals(@"scripts\T&P Recorder.dll", StringComparison.OrdinalIgnoreCase)
                    || o.Rel.StartsWith("T&P Recorder.VehicleLimit", StringComparison.OrdinalIgnoreCase)
                )
                    ? "Traffic and Player Recorder"
                : title.StartsWith("База для моддинга:", StringComparison.Ordinal)
                || title == "Компоненты GameConfig"
                || title.StartsWith("GameConfig", StringComparison.OrdinalIgnoreCase)
                    ? BaseName
                : ModNames.Pretty(title)
            );
        return name;
    }
}
