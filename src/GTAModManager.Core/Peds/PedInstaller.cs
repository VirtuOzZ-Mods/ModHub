using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using GTAModManager.Core.Dlc;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;

namespace GTAModManager.Core.Peds;

/// <summary>
/// Prepares one ordinary OIV transaction; never commits game files itself.
/// The pack has the name and layout of the classic Ped Selector pack (pedselector / o_pedselector / appdata.xml),
/// so a user's existing pedselector\dlc.rpf can simply be dropped in. Unlike the original, the ped list lives
/// inside the pack (common\data\peds.meta), never in Documents: ModHub keeps everything in the game folder.
/// </summary>
public sealed class PedInstaller(GameInstall game, string? userModsDirectory = null)
{
    public const string PackName = "pedselector";
    public const string PackPath = @"mods\update\x64\dlcpacks\pedselector\dlc.rpf";

    /// <summary>Former ModHub pack name: on the next operation the pack is migrated to pedselector.</summary>
    public const string LegacyPackName = "gmm_peds";
    public const string LegacyPackPath = @"mods\update\x64\dlcpacks\gmm_peds\dlc.rpf";
    private const string Device = "o_pedselector";
    private const string DatFile = "appdata.xml";
    public const string CatalogPath = @"GMMAddonPeds\catalog.json";
    public const string ModelsPath = "GMMPedSelector.models.txt";
    public const string MarkerPath = "gmm-owner.txt";
    public const string Marker = "GMM AddonPeds owned DLC v1\n";
    private const string MetadataPath = @"common\data\peds.meta";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public PedCatalog ListManaged()
    {
        using var gate = GameOperationGate.Acquire(game);
        return ReadCatalog();
    }

    /// <summary>Selector rows only: existing external registrations are never rewritten.</summary>
    public string BuildSelectorModels(IEnumerable<PedEntry>? externalEntries = null)
    {
        using var gate = GameOperationGate.Acquire(game);
        if (game.Edition != GameEdition.Legacy)
            throw new ArgumentException(L.T("core.peds.gmmAddonpedsSupportsGtaV"));
        var catalog = ReadCatalog();
        using var pack = OpenPack(catalog);
        var metadata = pack.Metadata;
        return ModelText(
            catalog
                .Entries.Concat(
                    metadata
                        .Root!.Element("InitDatas")!
                        .Elements("Item")
                        .Where(i => !string.IsNullOrWhiteSpace(i.Element("Name")?.Value))
                        .Select(i => new PedEntry
                        {
                            SpawnName = i.Element("Name")!.Value,
                            DisplayName = i.Element("Name")!.Value,
                        })
                )
                .Concat(ExternalEntries(externalEntries))
        );
    }

    public InstallPlan PrepareInstall(
        PedArchiveAnalysis analysis,
        IReadOnlyList<PedSelection> selections,
        IEnumerable<PedEntry>? externalEntries = null,
        bool preview = false
    ) => Prepare(L.T("core.peds.installingGmmAddonpeds"), () =>
        {
            if (selections.Count == 0)
                throw new ArgumentException(L.T("core.peds.selectAtLeastOnePed"));
            var catalog = ReadCatalog();
            using var pack = OpenPack(catalog);
            var metadata = pack.Metadata;
            var external = ExternalEntries(externalEntries);
            var occupied = metadata
                .Root!.Element("InitDatas")!
                .Elements("Item")
                .Select(e => e.Element("Name")?.Value)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .Concat(catalog.Entries.Select(e => e.SpawnName))
                .Concat(external.Select(e => e.SpawnName))
                .ToList();
            var names = occupied.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var hashes = occupied.Select(PedMetadata.NameHash).ToHashSet();
            var chosenIds = new HashSet<string>(StringComparer.Ordinal);
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var archive = ModArchive.Open(analysis.ArchivePath);
            var source = new PreparedSource(archive);
            var resources = new List<OivOp>();
            string packageId = PedPackageLibrary.NewId();
            foreach (var selection in selections)
            {
                if (!chosenIds.Add(selection.CandidateId))
                    throw new ArgumentException("Один вариант выбран несколько раз.");
                var candidate = analysis.Candidates.SingleOrDefault(c => c.Id == selection.CandidateId)
                    ?? throw new ArgumentException(L.T("core.peds.selectedVariantMissingFromArchive"));
                PedMetadata.ValidateSpawnName(candidate.SpawnName);
                if (candidate.LayoutAmbiguous)
                    throw new ArgumentException(
                        L.T("core.peds.mixesNormalStreamedWithoutUnambiguous", candidate.SpawnName)
                    );
                if (!names.Add(candidate.SpawnName) || !hashes.Add(PedMetadata.NameHash(candidate.SpawnName)))
                    throw new ArgumentException(L.T("core.peds.pedNameGameHashConflict", candidate.SpawnName));
                if (selection.Layout != candidate.Layout)
                    throw new ArgumentException(L.T("core.peds.structureDoesNotMatchSelected", candidate.SpawnName));
                var item = PedMetadata.ParseItem(
                    candidate.RegistrationXml
                        ?? PedMetadata.CreateDefault(candidate.SpawnName, selection.Gender, selection.Layout)
                );
                if (
                    !string.Equals(item.Element("Name")!.Value, candidate.SpawnName, StringComparison.OrdinalIgnoreCase)
                )
                    throw new ArgumentException(L.T("core.peds.registrationNameDiffersFromModel"));
                var streamed = item.Element("IsStreamedGfx")?.Attribute("value")?.Value;
                if (
                    streamed is not null
                    && (!bool.TryParse(streamed, out bool actual) || actual != (selection.Layout == PedLayout.Streamed))
                )
                    throw new ArgumentException(L.T("core.peds.isstreamedgfxDoesNotMatchFiles", candidate.SpawnName));
                var entry = new PedEntry
                {
                    SpawnName = candidate.SpawnName,
                    DisplayName = CleanDisplay(selection.DisplayName, candidate.SpawnName),
                    Layout = selection.Layout,
                    PedType = item.Element("Pedtype")?.Value ?? "",
                    RegistrationXml = item.ToString(SaveOptions.DisableFormatting),
                    Managed = true,
                    SourceArchive = analysis.ArchivePath,
                    SourceDlc = PackName,
                    PreviewPath = candidate.PreviewPath,
                    PackageId = packageId,
                    MetadataSource = PackPath + "\\" + MetadataPath,
                };
                if (candidate.Resources.Count == 0)
                    throw new ArgumentException(L.T("core.peds.pedSetHasNoResources"));
                foreach (var resource in candidate.Resources)
                {
                    var target = ValidateResourcePath(resource.TargetPath, candidate.SpawnName);
                    if (
                        !targets.Add(target)
                        || catalog.Entries.Any(e => e.ResourcePaths.Contains(target, StringComparer.OrdinalIgnoreCase))
                    )
                        throw new ArgumentException(L.T("core.peds.sharedDuplicatePedResource", target));
                    if (pack.HasResource(target))
                        throw new ArgumentException(L.T("core.peds.resourceAlreadyExistsWillNot", target));
                    ValidateResource(archive, resource);
                    resources.Add(new OivAdd(resource.SourcePath, target));
                    entry.ResourcePaths.Add(target);
                }
                RequireBaseFiles(entry);
                if (
                    candidate.RegistrationXml is null
                    && entry.ResourcePaths.Any(p =>
                        p.StartsWith(candidate.SpawnName + "_p\\", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(p)
                            .Equals(candidate.SpawnName + "_p", StringComparison.OrdinalIgnoreCase)
                    )
                )
                {
                    item.SetElementValue("PropsName", candidate.SpawnName + "_p");
                    entry.RegistrationXml = item.ToString(SaveOptions.DisableFormatting);
                }
                metadata.Root!.Element("InitDatas")!.Add(new XElement(item));
                catalog.Entries.Add(entry);
            }
            // Stage media only for a real plan. The preview references an empty token
            // and never opens image bodies or builds a redundant cache archive.
            using var media = preview ? null : PedPackageLibrary.Stage(game, packageId, analysis, archive);
            var ops = BuildOps(source, pack, catalog, metadata, resources, external, addDlc: true);
            ops.Add(
                new OivAdd(
                    preview ? source.Text("") : source.Local(media!.Path),
                    PedPackageLibrary.RelativePath(packageId)
                )
            );
            var plan = new OivInstaller(game).Prepare(
                L.T("core.peds.installingGmmAddonpeds"),
                ops,
                source,
                preview: preview
            );
            plan.Log.Add(L.T("core.peds.allGameplaySettingsFromOriginal"));
            if (!string.IsNullOrWhiteSpace(analysis.Readme))
                plan.Log.Add(
                    L.T("core.peds.archiveReadme", analysis.Readme[..Math.Min(analysis.Readme.Length, 16000)])
                );
            return plan;
        });

    public InstallPlan PrepareRemove(string spawnName, IEnumerable<PedEntry>? externalEntries = null) =>
        PrepareRemoveMany([spawnName], externalEntries);

    public InstallPlan PrepareRemoveMany(
        IReadOnlyList<string> spawnNames,
        IEnumerable<PedEntry>? externalEntries = null
    ) => Prepare(L.T("core.peds.removingSelectedGmmPeds"), () =>
        {
            if (spawnNames.Count is < 1 or > 1024)
                throw new ArgumentException(L.T("core.peds.select11024ManagedPeds"));
            var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in spawnNames)
            {
                PedMetadata.ValidateSpawnName(name);
                if (!selectedNames.Add(name))
                    throw new ArgumentException("Одна модель выбрана для удаления несколько раз.");
            }
            var catalog = ReadCatalog();
            var selected = selectedNames
                .Select(name =>
                    catalog.Entries.SingleOrDefault(e => e.SpawnName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException(L.T("core.peds.pedDoesNotBelongModhub", name))
                )
                .ToList();
            using var pack = OpenPack(catalog);
            if (!pack.Exists)
                throw new ArgumentException(L.T("core.peds.managedDlcNotFound"));
            var metadata = pack.Metadata;
            var removeItems = new List<XElement>();
            foreach (var entry in selected)
            {
                var matches = metadata
                    .Root!.Element("InitDatas")!
                    .Elements("Item")
                    .Where(i =>
                        string.Equals(i.Element("Name")?.Value, entry.SpawnName, StringComparison.OrdinalIgnoreCase)
                    )
                    .ToList();
                if (
                    matches.Count != 1
                    || !XNode.DeepEquals(
                        XElement.Parse(entry.RegistrationXml),
                        XElement.Parse(matches[0].ToString(SaveOptions.DisableFormatting))
                    )
                )
                    throw new ArgumentException(L.T("core.peds.pedRegistrationWasChangedManually", entry.SpawnName));
                removeItems.Add(matches[0]);
            }
            var ownedDictionaries = selected
                .SelectMany(e => e.ResourcePaths)
                .Select(p => p.Replace('/', '\\').Split('\\'))
                .Select(parts => parts.Length == 1 ? Path.GetFileNameWithoutExtension(parts[0]) : parts[0])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (
                metadata
                    .Root!.Element("InitDatas")!
                    .Elements("Item")
                    .Any(i =>
                        !removeItems.Contains(i)
                        && i.Element("PropsName") is { } props
                        && ownedDictionaries.Contains(props.Value)
                    )
            )
                throw new ArgumentException(L.T("core.peds.pedResourcesUsedByAnother"));
            var resources = new List<OivOp>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in selected)
            {
                foreach (var path in entry.ResourcePaths)
                {
                    string target = ValidateResourcePath(path, entry.SpawnName);
                    if (
                        catalog.Entries.Any(e =>
                            !selectedNames.Contains(e.SpawnName)
                            && e.ResourcePaths.Contains(target, StringComparer.OrdinalIgnoreCase)
                        )
                    )
                        throw new ArgumentException(L.T("core.peds.resourceUsedByAnotherPed"));
                    if (targets.Add(target))
                        resources.Add(new OivDelete(target));
                }
            }
            foreach (var item in removeItems)
                item.Remove();
            catalog.Entries.RemoveAll(e => selectedNames.Contains(e.SpawnName));
            var source = new PreparedSource(null);
            var ops = BuildOps(
                source,
                pack,
                catalog,
                metadata,
                resources,
                ExternalEntries(externalEntries),
                addDlc: false
            );
            return new OivInstaller(game).Prepare(
                L.T("core.peds.removingGmmPed", string.Join(", ", spawnNames)),
                ops,
                source
            );
        });

    /// <summary>
    /// Whether the pack needs updating without installing characters: migrate the old gmm_peds to pedselector
    /// or attach a dropped-in original Ped Selector pack (dlclist.xml).
    /// </summary>
    public bool NeedsPackUpdate()
    {
        if (game.Edition != GameEdition.Legacy)
            return false;
        if (File.Exists(Path.Combine(game.Root, LegacyPackPath)))
            return true;
        if (!File.Exists(Path.Combine(game.Root, PackPath)))
            return false;
        try
        {
            return DlcList.Add(ReadDlcList(), PackName).changed;
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or IOException
                        or InvalidDataException
                        or Rpf.RpfException
                        or OivFormatException
            )
        {
            return false;
        }
    }

    /// <summary>
    /// Migrates the ModHub pack gmm_peds to pedselector (resources are copied byte for byte, registrations are kept)
    /// and/or attaches the original Ped Selector pack the user placed in dlcpacks.
    /// The original pack is never rewritten: it is only registered in dlclist.xml and added to the menu.
    /// </summary>
    public InstallPlan PrepareUpdatePack(IEnumerable<PedEntry>? externalEntries = null) =>
        Prepare(L.T("core.peds.updatingCharacterPack"), () =>
        {
            var catalog = ReadCatalog();
            using var pack = OpenPack(catalog);
            var source = new PreparedSource(null);
            var external = ExternalEntries(externalEntries);
            if (pack.Legacy is not null)
                return new OivInstaller(game).Prepare(
                    L.T("core.peds.movingModhubCharacterPackPedselector"),
                    BuildOps(source, pack, catalog, pack.Metadata, [], external, addDlc: true),
                    source
                );
            var (xml, changed) = DlcList.Add(ReadDlcList(), PackName);
            if (!pack.Exists || !changed)
                throw new ArgumentException(L.T("core.peds.characterPackAlreadyRegisteredNo"));
            return new OivInstaller(game).Prepare(
                L.T("core.peds.connectingPedSelectorPack"),
                [
                    new OivAdd(source.Text(SelectorModels(catalog, pack.Metadata, external)), ModelsPath),
                    new OivArchive(
                        @"update\update.rpf",
                        false,
                        "RPF7",
                        [new OivAdd(source.Text(xml), @"common\data\dlclist.xml")]
                    ),
                ],
                source
            );
        });

    private InstallPlan Prepare(string title, Func<InstallPlan> action)
    {
        using var gate = GameOperationGate.Acquire(game);
        try
        {
            if (game.Edition != GameEdition.Legacy)
                throw new ArgumentException(L.T("core.peds.gmmAddonpedsSupportsGtaV2"));
            if (game.IsRunning())
                throw new ArgumentException(L.T("core.peds.gameRunningPedInstallationRemoval"));
            if (new ModModeService(game).Inspect().WritesBlocked)
                throw new ArgumentException(L.T("core.peds.enableModsFinishRecoveryFirst"));
            return action();
        }
        catch (Exception ex)
            when (ex
                    is ArgumentException
                        or IOException
                        or InvalidDataException
                        or System.Xml.XmlException
                        or JsonException
                        or InvalidOperationException
                        or Rpf.RpfException
                        or OivFormatException
                        or FormatException
                        or SharpCompress.Common.ArchiveException
                        or SharpCompress.Common.ArchiveOperationException
            )
        {
            var plan = new InstallPlan
            {
                Title = title,
                PrepDir = Path.Combine(game.WorkDir, "prep", "ped-rejected-" + Guid.NewGuid().ToString("N")),
            };
            plan.Errors.Add(ex.Message);
            return plan;
        }
    }

    private PedCatalog ReadCatalog()
    {
        var path = Path.Combine(game.Root, CatalogPath);
        if (!File.Exists(path))
            return new();
        if (new FileInfo(path).Length > 16L << 20)
            throw new ArgumentException(L.T("core.peds.pedCatalogTooLarge"));
        var catalog = JsonSerializer.Deserialize<PedCatalog>(File.ReadAllText(path), JsonOptions)
            ?? throw new ArgumentException(L.T("core.peds.pedCatalogCorrupted"));
        if (catalog.Version != 1 || catalog.Entries is null)
            throw new ArgumentException(L.T("core.peds.unknownPedCatalogFormat"));
        var names = new HashSet<uint>();
        foreach (var entry in catalog.Entries)
        {
            PedMetadata.ValidateSpawnName(entry.SpawnName);
            if (!string.IsNullOrEmpty(entry.PackageId) && !PedPackageLibrary.IsValidPackageId(entry.PackageId))
                throw new ArgumentException(L.T("core.peds.pedCatalogContainsInvalidSaved"));
            if (!entry.Managed || !names.Add(PedMetadata.NameHash(entry.SpawnName)))
                throw new ArgumentException(L.T("core.peds.pedCatalogContainsForeignDuplicate"));
            if (entry.ResourcePaths is null || entry.ResourcePaths.Count == 0)
                throw new ArgumentException(L.T("core.peds.resourcesBelongingPedDirectoryMissing"));
            foreach (var p in entry.ResourcePaths)
                ValidateResourcePath(p, entry.SpawnName);
            var item = PedMetadata.ParseItem(entry.RegistrationXml);
            if (!string.Equals(item.Element("Name")!.Value, entry.SpawnName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(L.T("core.peds.pedDirectoryNameDiffersFrom"));
        }
        return catalog;
    }

    private List<PedEntry> ExternalEntries(IEnumerable<PedEntry>? entries)
    {
        // Characters from our pack (and from the gmm_peds being migrated) come from its registration, not as foreign ones.
        var owned = new[] { PackPath, LegacyPackPath }
            .Select(p => Path.Combine(game.Root, p).Replace('/', '\\'))
            .ToArray();
        return (entries ?? [])
            .Where(e =>
                !e.Managed
                && !e.SourceDlc.Equals(PackName, StringComparison.OrdinalIgnoreCase)
                && !e.SourceDlc.Equals(LegacyPackName, StringComparison.OrdinalIgnoreCase)
                && !owned.Any(o => e.SourceDlc.Replace('/', '\\').Equals(o, StringComparison.OrdinalIgnoreCase))
            )
            .ToList();
    }

    /// <summary>Character pack state: pedselector (ours or the original) and the old gmm_peds to migrate.</summary>
    private sealed class PackState : IDisposable
    {
        public ArchiveSession? Own;

        /// <summary>pedselector without the ModHub marker: the original Ped Selector pack dropped in by the user.</summary>
        public bool Foreign;
        public Rpf.Rpf7Archive? Legacy;
        public Rpf.Rpf7Archive? LegacyPeds;
        public XDocument Metadata = null!;

        /// <summary>gmm_peds characters that already exist in the dropped-in Ped Selector pack: its version is kept.</summary>
        public HashSet<string> LegacySkipped { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists => Own is not null || Legacy is not null;

        public bool HasResourceInOwn(string target)
        {
            if (Own is null)
                return false;
            var (container, inner) = Own.Resolve("peds.rpf\\" + target, false);
            return container.Builder.GetSource(inner) is not null;
        }

        public bool HasResource(string target) => HasResourceInOwn(target) || LegacyPeds?.Find(target) is Rpf.RpfFile;

        public void Dispose()
        {
            LegacyPeds?.Dispose();
            Legacy?.Dispose();
            Own?.Dispose();
        }
    }

    private PackState OpenPack(PedCatalog catalog)
    {
        // Even a vanilla-tree pack of this name belongs to somebody else: do not shadow it.
        foreach (var name in new[] { PackName, LegacyPackName })
            if (File.Exists(Path.Combine(game.Root, @"update\x64\dlcpacks", name, "dlc.rpf")))
                throw new ArgumentException(L.T("core.peds.dlcNameAlreadyUseOutside", name));
        var state = new PackState();
        try
        {
            var keys = game.TryLoadKeys();
            var path = Path.Combine(game.Root, PackPath);
            if (File.Exists(path))
            {
                state.Own = ArchiveSession.OpenFile(path, PackPath, keys);
                state.Foreign =
                    state.Own.ReadFile(MarkerPath) is not { } marker || Encoding.UTF8.GetString(marker) != Marker;
            }
            var legacy = Path.Combine(game.Root, LegacyPackPath);
            if (File.Exists(legacy))
            {
                using (var check = ArchiveSession.OpenFile(legacy, LegacyPackPath, keys))
                    if (check.ReadFile(MarkerPath) is not { } marker || Encoding.UTF8.GetString(marker) != Marker)
                        throw new ArgumentException(L.T("core.peds.dlcGmmPedsHasNo"));
                state.Legacy = Rpf.Rpf7Archive.OpenRead(legacy, keys);
                state.LegacyPeds = state.Legacy.Find("peds.rpf") is Rpf.RpfBinaryFile peds
                    ? state.Legacy.OpenNested(peds)
                    : null;
            }
            if (!state.Exists && catalog.Entries.Count > 0)
                throw new ArgumentException(L.T("core.peds.pedDirectoryExistsButManaged"));
            state.Metadata =
                state.Own is null ? EmptyMetadata()
                : state.Foreign ? AdoptedMetadata(state.Own)
                : OwnMetadata(state.Own.ReadFile(MetadataPath));
            if (state.Legacy is not null)
            {
                var file =
                    state.Legacy.Find(MetadataPath) as Rpf.RpfBinaryFile
                    ?? throw new ArgumentException(L.T("core.peds.dlcGmmPedsMissingPeds"));
                var names = Items(state.Metadata)
                    .Select(i => i.Element("Name")!.Value)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Items(OwnMetadata(state.Legacy.ReadContent(file))))
                {
                    if (!names.Add(item.Element("Name")!.Value))
                    {
                        if (state.Foreign)
                        {
                            state.LegacySkipped.Add(item.Element("Name")!.Value);
                            continue;
                        }
                        throw new ArgumentException(
                            L.T("core.peds.characterExistsBothPedselectorGmm", item.Element("Name")!.Value)
                        );
                    }
                    state.Metadata.Root!.Element("InitDatas")!.Add(new XElement(item));
                }
            }
            return state;
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    private static IEnumerable<XElement> Items(XDocument doc) =>
        doc.Root!.Element("InitDatas")!
            .Elements("Item")
            .Where(i => !string.IsNullOrWhiteSpace(i.Element("Name")?.Value));

    private static XDocument EmptyMetadata() =>
        new(new XElement("CPedModelInfo__InitDataList", new XElement("InitDatas")));

    private static XDocument OwnMetadata(byte[]? bytes)
    {
        // Only the model list, as in add-on packs verified in game. Redeclaring residentTxd
        // (comp_peds_generic is already declared by the game) and empty texture binding blocks caused a crash when loading story mode.
        if (bytes is null)
            throw new ArgumentException(L.T("core.peds.managedDlcHasNoPeds"));
        var doc = PedMetadata.ParseDocument(Encoding.UTF8.GetString(bytes));
        if (doc.Root?.Name != "CPedModelInfo__InitDataList" || doc.Root.Elements("InitDatas").Count() != 1)
            throw new ArgumentException(L.T("core.peds.managedPedsMetaDamagedAutomatic"));
        // The file belongs to ModHub: when rewriting it, remove service blocks left over from earlier versions.
        foreach (var name in new[] { "residentTxd", "residentAnims", "txdRelationships", "multiTxdRelationships" })
            doc.Root.Elements(name).Remove();
        return doc;
    }

    /// <summary>
    /// Registrations of the original Ped Selector pack: its appdata.xml refers to user:/UserMods/ap_m.xml.
    /// The file is read-only; the list includes models whose resources are in this pack's peds.rpf.
    /// </summary>
    private XDocument AdoptedMetadata(ArchiveSession pack)
    {
        var result = EmptyMetadata();
        var setup = PedMetadata.ParseDocument(
            Encoding
                .UTF8.GetString(pack.ReadFile("setup2.xml") ?? throw new ArgumentException(NotPedSelector))
                .TrimStart('﻿')
        );
        string device = setup.Root?.Element("deviceName")?.Value.Trim() ?? "";
        string dat = setup.Root?.Element("datFile")?.Value.Trim() is { Length: > 0 } d ? d : DatFile;
        var content = PedMetadata.ParseDocument(
            Encoding.UTF8.GetString(pack.ReadFile(dat) ?? throw new ArgumentException(NotPedSelector)).TrimStart('﻿')
        );
        var files = content
            .Descendants("Item")
            .Where(i => i.Element("fileType")?.Value == "PED_METADATA_FILE")
            .Select(i => i.Element("filename")?.Value.Trim() ?? "")
            .ToList();
        string userMods = Path.GetFullPath(
            userModsDirectory
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "Rockstar Games",
                    "GTA V",
                    "UserMods"
                )
        );
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in files)
        {
            byte[]? bytes = null;
            if (reference.StartsWith("user:/UserMods/", StringComparison.OrdinalIgnoreCase))
            {
                var file = Path.GetFullPath(Path.Combine(userMods, reference[15..].Replace('/', '\\')));
                if (
                    file.StartsWith(userMods.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(file)
                    && new FileInfo(file).Length <= 16L << 20
                )
                    bytes = File.ReadAllBytes(file);
            }
            else if (device.Length > 0 && reference.StartsWith(device + ":/", StringComparison.OrdinalIgnoreCase))
                bytes = pack.ReadFile(reference[(device.Length + 2)..].Replace('/', '\\'));
            if (bytes is null)
                continue;
            var doc = PedMetadata.ParseDocument(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
            foreach (var item in doc.Descendants("Item").Where(i => i.Parent?.Name == "InitDatas"))
            {
                var name = item.Element("Name")?.Value.Trim();
                if (string.IsNullOrEmpty(name) || !names.Add(name))
                    continue;
                var (container, inner) = pack.Resolve("peds.rpf\\" + name + ".yft", false);
                if (container.Builder.GetSource(inner) is null)
                    continue;
                result.Root!.Element("InitDatas")!.Add(new XElement(item));
            }
        }
        return result;
    }

    private const string NotPedSelector =
        "DLC pedselector не похож на пак Ped Selector и не имеет маркера ModHub. Чужой архив не будет изменён.";

    private string ReadDlcList()
    {
        var inMods = Path.Combine(game.Root, @"mods\update\update.rpf");
        var original = Path.Combine(game.Root, @"update\update.rpf");
        using var update = ArchiveSession.OpenFile(
            File.Exists(inMods) ? inMods : original,
            "update/update.rpf",
            game.TryLoadKeys()
        );
        var bytes = update.ReadFile(@"common\data\dlclist.xml")
            ?? throw new ArgumentException(L.T("core.peds.dlclistXmlNotFound"));
        return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
    }

    private static string SelectorModels(PedCatalog catalog, XDocument metadata, IEnumerable<PedEntry> external) =>
        ModelText(
            catalog
                .Entries.Concat(
                    Items(metadata)
                        .Select(i => new PedEntry
                        {
                            SpawnName = i.Element("Name")!.Value,
                            DisplayName = i.Element("Name")!.Value,
                        })
                )
                .Concat(external)
        );

    private List<OivOp> BuildOps(
        PreparedSource source,
        PackState pack,
        PedCatalog catalog,
        XDocument metadata,
        List<OivOp> resources,
        IReadOnlyList<PedEntry> external,
        bool addDlc
    )
    {
        var peds = new List<OivOp>();
        if (pack.LegacyPeds is { } legacyPeds)
        {
            // gmm_peds migration: resources are copied byte for byte, except those removed by the same operation.
            var removed = resources
                .OfType<OivDelete>()
                .Select(d => GameInstall.Normalize(d.Target))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in legacyPeds.Files)
            {
                var path = file.Path.Replace('/', '\\');
                if (removed.Contains(path) || pack.LegacySkipped.Any(name => BelongsTo(path, name)))
                    continue;
                if (pack.Own is not null && pack.HasResourceInOwn(path))
                    throw new ArgumentException(L.T("core.peds.resourceExistsBothPedselectorGmm", path));
                peds.Add(new OivAdd(source.Lazy(() => LegacyFile(legacyPeds, file), file.StoredSize), path));
            }
        }
        peds.AddRange(resources.Where(r => r is not OivDelete || pack.Own is not null));
        var packOps = new List<OivOp>
        {
            new OivAdd(source.Text(Marker), MarkerPath),
            new OivAdd(source.Text(SetupXml), "setup2.xml"),
            new OivAdd(source.Text(AppDataXml), DatFile),
            new OivAdd(source.Text(metadata.ToString(SaveOptions.DisableFormatting)), MetadataPath),
            new OivArchive("peds.rpf", true, "RPF7", peds),
        };
        if (pack.Own?.ReadFile("content.xml") is not null)
            packOps.Add(new OivDelete("content.xml"));
        // Duplicates from gmm_peds no longer belong to ModHub: their version lives in the attached Ped Selector pack.
        catalog.Entries.RemoveAll(e => pack.LegacySkipped.Contains(e.SpawnName));
        foreach (var entry in catalog.Entries)
        {
            entry.SourceDlc = PackName;
            entry.MetadataSource = PackPath + "\\" + MetadataPath;
        }
        var ops = new List<OivOp>
        {
            new OivArchive(PackPath, true, "RPF7", packOps),
            new OivAdd(source.Text(JsonSerializer.Serialize(catalog, JsonOptions)), CatalogPath),
            new OivAdd(source.Text(SelectorModels(catalog, metadata, external)), ModelsPath),
        };
        if (pack.Legacy is not null)
            ops.Add(new OivDelete(LegacyPackPath));
        if (addDlc || pack.Legacy is not null)
        {
            var (xml, changed) = DlcList.Add(ReadDlcList(), PackName);
            if (pack.Legacy is not null)
            {
                var (without, removedLegacy) = DlcList.Remove(xml, LegacyPackName);
                xml = without;
                changed |= removedLegacy;
            }
            if (changed)
                ops.Add(
                    new OivArchive(
                        @"update\update.rpf",
                        false,
                        "RPF7",
                        [new OivAdd(source.Text(xml), @"common\data\dlclist.xml")]
                    )
                );
        }
        return ops;
    }

    private static bool BelongsTo(string path, string model)
    {
        var parts = path.Split('\\');
        string head = parts.Length == 1 ? Path.GetFileNameWithoutExtension(parts[0]) : parts[0];
        return head.Equals(model, StringComparison.OrdinalIgnoreCase)
            || head.Equals(model + "_p", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A resource file from the archive as .ydd/.ytd on disk: the RSC7 header is rebuilt from the entry flags
    /// (for resources ≥ 16 MB the archive stores the size there). Writing it back yields identical bytes.
    /// </summary>
    private static byte[] LegacyFile(Rpf.Rpf7Archive archive, Rpf.RpfFile file)
    {
        if (file is Rpf.RpfResourceFile { StoredSize: < 16 })
            throw new ArgumentException(L.T("core.peds.damagedResourceGmmPeds", file.Path));
        return archive.ReadAsFile(file);
    }

    private static string ModelText(IEnumerable<PedEntry> entries)
    {
        var seen = new HashSet<uint>();
        var lines = new List<string>();
        foreach (var entry in entries)
        {
            PedMetadata.ValidateReferencedSpawnName(entry.SpawnName);
            if (!seen.Add(PedMetadata.NameHash(entry.SpawnName)))
                continue;
            lines.Add(entry.SpawnName + "\t" + CleanDisplay(entry.DisplayName, entry.SpawnName));
        }
        return string.Join("\n", lines) + (lines.Count > 0 ? "\n" : "");
    }

    private static string CleanDisplay(string? display, string fallback)
    {
        string result = new(
            (display ?? "").Select(c => char.IsControl(c) || c == '\u2028' || c == '\u2029' ? ' ' : c).ToArray()
        );
        return string.IsNullOrWhiteSpace(result) ? fallback : result.Trim()[..Math.Min(result.Trim().Length, 160)];
    }

    private static string ValidateResourcePath(string path, string spawn)
    {
        var target = GameInstall.Normalize(path);
        var parts = target.Split('\\');
        if (
            path.StartsWith('/')
            || path.StartsWith('\\')
            || Path.IsPathRooted(path)
            || parts.Length > 2
            || parts.Any(p => p.EndsWith('.') || p.EndsWith(' ') || p.Contains('*') || p.Contains('?'))
        )
            throw new ArgumentException(L.T("core.peds.invalidPedResourcePath"));
        string name = Path.GetFileNameWithoutExtension(parts[^1]);
        string ext = Path.GetExtension(parts[^1]).ToLowerInvariant();
        if (ext is not (".yft" or ".ymt" or ".ydd" or ".ytd" or ".yld" or ".ybn" or ".ycd"))
            throw new ArgumentException(L.T("core.peds.unsupportedPedResource", target));
        bool owned =
            parts.Length == 1
                ? name.Equals(spawn, StringComparison.OrdinalIgnoreCase)
                    || name.Equals(spawn + "_p", StringComparison.OrdinalIgnoreCase)
                : parts[0].Equals(spawn, StringComparison.OrdinalIgnoreCase)
                    || parts[0].Equals(spawn + "_p", StringComparison.OrdinalIgnoreCase);
        if (!owned)
            throw new ArgumentException(L.T("core.peds.resourceOutsidePedSet", target));
        return target;
    }

    private static void RequireBaseFiles(PedEntry entry)
    {
        foreach (
            var ext in entry.Layout == PedLayout.Normal ? new[] { ".yft", ".ymt", ".ydd" } : new[] { ".yft", ".ymt" }
        )
            if (!entry.ResourcePaths.Contains(entry.SpawnName + ext, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(L.T("core.peds.incompleteSetMissing", entry.SpawnName, ext));
        if (
            entry.Layout == PedLayout.Streamed
            && !entry.ResourcePaths.Any(p => p.StartsWith(entry.SpawnName + "\\", StringComparison.OrdinalIgnoreCase))
        )
            throw new ArgumentException(L.T("core.peds.streamedPedHasNoComponent"));
    }

    private static void ValidateResource(ModArchive archive, PedResource resource)
    {
        long size = archive.SourceSize(resource.SourcePath);
        if (size < 16)
            throw new ArgumentException(L.T("core.peds.pedResourceShorterThanRsc7", resource.SourcePath));
        if (size != resource.Size)
            throw new ArgumentException(L.T("core.peds.pedResourceSizeChangedAfter", resource.SourcePath));
        using var stream = archive.OpenSource(resource.SourcePath);
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        if (!Rpf.RpfResourceSource.IsRsc7(header))
            throw new ArgumentException(L.T("core.peds.invalidRsc7HeaderPedResource", resource.SourcePath));
    }

    private sealed class PreparedSource(IModSource? archive) : IModSource
    {
        private readonly Dictionary<string, byte[]> _generated = [];
        private readonly Dictionary<string, string> _local = [];
        private readonly Dictionary<string, (Func<byte[]> read, long size)> _lazy = [];

        public string Lazy(Func<byte[]> read, long size)
        {
            string key = "gmm-lazy:" + Guid.NewGuid().ToString("N");
            _lazy[key] = (read, size);
            return key;
        }

        public string Local(string path)
        {
            string key = "gmm-local:" + Guid.NewGuid().ToString("N");
            _local[key] = path;
            return key;
        }

        public string Text(string text)
        {
            string key = "gmm-generated:" + Guid.NewGuid().ToString("N");
            _generated[key] = new UTF8Encoding(false).GetBytes(text);
            return key;
        }

        public Stream OpenSource(string source) =>
            _generated.TryGetValue(source, out var bytes) ? new MemoryStream(bytes, false)
            : _lazy.TryGetValue(source, out var lazy) ? new MemoryStream(lazy.read(), false)
            : _local.TryGetValue(source, out var file) ? File.OpenRead(file)
            : archive?.OpenSource(source) ?? throw new ArgumentException(L.T("core.peds.missingPedSource", source));

        public long SourceSize(string source) =>
            _generated.TryGetValue(source, out var bytes) ? bytes.Length
            : _lazy.TryGetValue(source, out var lazy) ? lazy.size
            : _local.TryGetValue(source, out var file) ? new FileInfo(file).Length
            : archive?.SourceSize(source) ?? throw new ArgumentException(L.T("core.peds.missingPedSource", source));
    }

    // setup2.xml matches the original Ped Selector (FlareForge); appdata.xml differs only in that
    // the character list lives inside the pack rather than in Documents\Rockstar Games\GTA V\UserMods\ap_m.xml.
    private const string SetupXml = """
        <?xml version="1.0" encoding="UTF-8"?><SSetupData><deviceName>o_pedselector</deviceName><datFile>appdata.xml</datFile><timeStamp>22/09/2015 17:26:39</timeStamp><nameHash>pedselector</nameHash><contentChangeSetGroups><Item><NameHash>GROUP_STARTUP</NameHash><ContentChangeSets><Item>PEDSELECTOR_AUTOGEN</Item></ContentChangeSets></Item></contentChangeSetGroups><type>EXTRACONTENT_COMPAT_PACK</type><order value="1" /></SSetupData>
        """;
    private const string AppDataXml = """
        <?xml version="1.0" encoding="UTF-8"?><CDataFileMgr__ContentsOfDataFileXml><disabledFiles/><includedXmlFiles/><includedDataFiles/><dataFiles><Item><filename>o_pedselector:/peds.rpf</filename><fileType>PEDSTREAM_FILE</fileType><overlay value="false"/><disabled value="true"/><persistent value="true"/></Item><Item><filename>o_pedselector:/common/data/peds.meta</filename><fileType>PED_METADATA_FILE</fileType><overlay value="false" /><disabled value="true" /><persistent value="false" /></Item></dataFiles><contentChangeSets><Item><changeSetName>PEDSELECTOR_AUTOGEN</changeSetName><mapChangeSetData/><filesToInvalidate/><filesToDisable/><filesToEnable><Item>o_pedselector:/peds.rpf</Item><Item>o_pedselector:/common/data/peds.meta</Item></filesToEnable><txdToLoad/><txdToUnload/><residentResources/><unregisterResources/></Item></contentChangeSets><patchFiles/></CDataFileMgr__ContentsOfDataFileXml>
        """;
}
