using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Dlc;

/// <summary>An add-on pack in the game folder and what it contains.</summary>
public sealed record InstalledPack(string Pack, string Rel, DlcContent Content);

/// <summary>A car whose engine sound can be changed: where its vehicles.meta lives and what it plays now.</summary>
public sealed record SoundTarget(string Pack, string ArchiveRel, DlcVehicle Vehicle)
{
    /// <summary>A stock car (its meta is in update.rpf), not an add-on.</summary>
    public bool Stock => ArchiveRel.Equals(EngineSounds.StockArchive, StringComparison.OrdinalIgnoreCase);
}

/// <summary>An engine sound that can be given to a car, and where it comes from.</summary>
public sealed record SoundChoice(string Name, string Pack, DlcPackKind Source);

/// <summary>Request for the elevated CLI: give <see cref="Model"/> in the pack at <see cref="ArchiveRel"/> another sound.</summary>
public sealed class EngineSoundRequest
{
    public string ArchiveRel { get; set; } = "";
    public string Model { get; set; } = "";
    public string Sound { get; set; } = "";
}

public sealed record EngineSoundResult(bool Success, string Id, string Message);

/// <summary>A recorded sound change (one Mod Hub installation): model → sound.</summary>
public sealed record SoundAssignment(string Id, string Model, string Sound);

/// <summary>
/// Gives an installed car another engine sound: the car's &lt;audioNameHash&gt; in its vehicles.meta (inside the
/// car's dlc.rpf) is set to the sound name. The edit goes through OivInstaller like any other installation, so it has
/// a targeted backup and is undone from History or with "Restore original" (undo of all sound changes of that car).
/// Any engine sound loaded by the game works: from an engine sound pack, from another add-on car, or a stock one.
/// </summary>
public static partial class EngineSounds
{
    private static readonly ConcurrentDictionary<string, (long Size, DateTime Time, DlcContent? Content)> Cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Add-on packs in mods\update\x64\dlcpacks (and onigiri\dlcpacks for packs that exist only there).</summary>
    public static IReadOnlyList<InstalledPack> Packs(GameInstall game)
    {
        var result = new List<InstalledPack>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { @"mods\update\x64\dlcpacks", @"onigiri\dlcpacks" })
        {
            var dir = Path.Combine(game.Root, root);
            if (!Directory.Exists(dir))
                continue;
            foreach (
                var packDir in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            )
            {
                var pack = Path.GetFileName(packDir);
                // Rockstar packs copied to mods\ are game content, not add-ons.
                if (!seen.Add(pack) || Directory.Exists(Path.Combine(game.Root, "update", "x64", "dlcpacks", pack)))
                    continue;
                var rel = $@"{root}\{pack}\dlc.rpf";
                if (Inspect(Path.Combine(game.Root, rel), pack) is { } content)
                    result.Add(new(pack, rel, content));
            }
        }
        return result;
    }

    /// <summary>Contents of a pack on disk, cached until the file changes.</summary>
    public static DlcContent? Inspect(string path, string pack)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        if (Cache.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Time == info.LastWriteTimeUtc)
            return hit.Content;
        var content = DlcInspector.TryInspectFile(path, pack);
        Cache[path] = (info.Length, info.LastWriteTimeUtc, content);
        return content;
    }

    /// <summary>The game archive that holds the stock vehicles.meta files; edited through its copy in mods\.</summary>
    public const string StockArchive = @"update\update.rpf";

    private static readonly ConcurrentDictionary<
        string,
        (long Size, DateTime Time, IReadOnlyList<SoundTarget> Cars)
    > StockCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stock cars: the base game's common\data\levels\gta5\vehicles.meta in update.rpf and the patched metas of
    /// Rockstar DLCs in update.rpf\dlc_patch\&lt;pack&gt;\…; read from the mods\ copy when there is one (that is what
    /// the game loads). Cars whose only meta is inside an encrypted Rockstar dlc.rpf are not listed. Pack is "" for
    /// the base game and the DLC name for patched cars. Empty when update.rpf cannot be read (no keys, missing).
    /// </summary>
    public static IReadOnlyList<SoundTarget> StockTargets(GameInstall game)
    {
        var mods = Path.Combine(game.Root, "mods", "update", "update.rpf");
        var path = File.Exists(mods) ? mods : Path.Combine(game.Root, "update", "update.rpf");
        var info = new FileInfo(path);
        if (!info.Exists)
            return [];
        if (StockCache.TryGetValue(path, out var hit) && hit.Size == info.Length && hit.Time == info.LastWriteTimeUtc)
            return hit.Cars;
        var cars = new List<SoundTarget>();
        try
        {
            var keys = GameInstall.IsPlainRpf(path) ? null : game.TryLoadKeys();
            var archive = Rpf7Archive.OpenRead(path, keys);
            try
            {
                bool Wanted(RpfBinaryFile f) =>
                    f.Path.Equals(@"common\data\levels\gta5\vehicles.meta", StringComparison.OrdinalIgnoreCase)
                    || f.Path.StartsWith(@"dlc_patch\", StringComparison.OrdinalIgnoreCase)
                        && f.Path.EndsWith(
                            @"\common\data\levels\gta5\vehicles.meta",
                            StringComparison.OrdinalIgnoreCase
                        );
                // An OPEN copy in mods\ may still hold encrypted entries: reopen it with keys once.
                if (keys is null && archive.Files.OfType<RpfBinaryFile>().Any(f => Wanted(f) && f.IsEncrypted != 0))
                {
                    keys = game.TryLoadKeys();
                    if (keys is not null)
                    {
                        archive.Dispose();
                        archive = Rpf7Archive.OpenRead(path, keys);
                    }
                }
                var metas = archive
                    .Files.OfType<RpfBinaryFile>()
                    .Where(Wanted)
                    // The base game first, then Rockstar DLC patches in name order.
                    .OrderBy(f => f.Path.StartsWith(@"dlc_patch\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var meta in metas)
                {
                    string text;
                    try
                    {
                        text = Encoding.UTF8.GetString(archive.ReadContent(meta));
                    }
                    catch (Exception ex) when (ex is RpfException or InvalidDataException)
                    {
                        continue;
                    }
                    string pack = meta.Path.StartsWith(@"dlc_patch\", StringComparison.OrdinalIgnoreCase)
                        ? meta.Path.Split('\\')[1]
                        : "";
                    foreach (var (model, audio, type, hasAudio) in DlcInspector.ParseVehicles(text))
                        if (
                            ValidName(model)
                            && TakesEngineSound(type)
                            && !cars.Any(c => c.Vehicle.Model.Equals(model, StringComparison.OrdinalIgnoreCase))
                        )
                            cars.Add(
                                new(pack, StockArchive, new(model, audio, type, [meta.Path]) { HasAudioTag = hasAudio })
                            );
                }
            }
            finally
            {
                archive.Dispose();
            }
        }
        catch (Exception ex) when (ex is RpfException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        StockCache[path] = (info.Length, info.LastWriteTimeUtc, cars);
        return cars;
    }

    /// <summary>Cars whose sound can be changed (a vehicles.meta was found for them).</summary>
    public static IReadOnlyList<SoundTarget> Targets(IEnumerable<InstalledPack> packs) =>
        packs
            .SelectMany(p =>
                p.Content.Vehicles.Where(v => v.CanChangeSound && TakesEngineSound(v.VehicleType))
                    .Select(v => new SoundTarget(p.Pack, p.Rel, v))
            )
            .ToList();

    /// <summary>Sounds the game will know: sound packs first, then the sounds of add-on cars.</summary>
    public static IReadOnlyList<SoundChoice> Choices(IEnumerable<InstalledPack> packs) =>
        packs
            .OrderBy(p => p.Content.Kind == DlcPackKind.EngineSounds ? 0 : 1)
            .SelectMany(p => p.Content.SoundNames.Select(n => new SoundChoice(n, p.Pack, p.Content.Kind)))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Vehicles that play a car engine sound. Bikes, boats, aircraft and trailers use other kinds of sound objects,
    /// so a car engine would not play on them. An unknown type is allowed (hand-written metas often omit it).
    /// </summary>
    public static bool TakesEngineSound(string type) =>
        type.Trim().ToUpperInvariant()
            is ""
                or "VEHICLE_TYPE_CAR"
                or "VEHICLE_TYPE_QUADBIKE"
                or "VEHICLE_TYPE_AMPHIBIOUS_AUTOMOBILE"
                or "VEHICLE_TYPE_AMPHIBIOUS_QUADBIKE"
                or "VEHICLE_TYPE_SUBMARINECAR";

    /// <summary>Sound and model names are plain identifiers; anything else could break the XPath or the meta.</summary>
    public static bool ValidName(string name) => NameRx().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$")]
    private static partial Regex NameRx();

    public static string Title(string model, string sound) => L.T("core.dlc.engineSoundTitle", model, sound);

    /// <summary>The installation ops that set <paramref name="sound"/> for the target car.</summary>
    public static IReadOnlyList<OivOp> Ops(SoundTarget target, string sound)
    {
        var v = target.Vehicle;
        if (!ValidName(sound))
            throw new ArgumentException(L.T("core.dlc.invalidSoundName", sound));
        if (!ValidName(v.Model) || !v.CanChangeSound)
            throw new ArgumentException(L.T("core.dlc.soundCannotBeChanged", v.Model));
        string item = $"//InitDatas/Item[normalize-space(modelName)='{v.Model}']";
        var tag = $"<audioNameHash>{sound}</audioNameHash>";
        OivOp op = new OivXml(
            v.MetaPath[^1],
            v.HasAudioTag
                ? [new OivXmlOp(OivXmlKind.Replace, item + "/audioNameHash", null, tag)]
                : [new OivXmlOp(OivXmlKind.Add, item, "Last", tag)]
        );
        // Nested archives inside the pack, innermost last.
        for (int i = v.MetaPath.Count - 2; i >= 0; i--)
            op = new OivArchive(v.MetaPath[i], false, "RPF7", [op]);
        // A path under mods\ is written to the same place; the installer keeps the onigiri copy in step.
        return [new OivArchive(target.ArchiveRel, false, "RPF7", [op])];
    }

    /// <summary>
    /// Sets the sound: re-reads the car's pack from disk (the request may be stale), then installs the meta edit
    /// as one transaction with a targeted backup. Throws when the request no longer matches the game folder.
    /// </summary>
    public static EngineSoundResult Assign(GameInstall game, Library.BackupLibrary? library, EngineSoundRequest request)
    {
        if (!ValidName(request.Sound))
            throw new ArgumentException(L.T("core.dlc.invalidSoundName", request.Sound));
        var candidates = request.ArchiveRel.Equals(StockArchive, StringComparison.OrdinalIgnoreCase)
            ? StockTargets(game)
            : Targets(Packs(game));
        var target = candidates.FirstOrDefault(t =>
            t.ArchiveRel.Equals(request.ArchiveRel, StringComparison.OrdinalIgnoreCase)
            && t.Vehicle.Model.Equals(request.Model, StringComparison.OrdinalIgnoreCase)
        ) ?? throw new InvalidOperationException(L.T("core.dlc.soundCannotBeChanged", request.Model));
        if (target.Vehicle.EffectiveAudio.Equals(request.Sound, StringComparison.OrdinalIgnoreCase))
            return new(true, "", L.T("core.dlc.soundAlreadySet", request.Model, request.Sound));
        RememberOriginal(game, target);
        var installer = new OivInstaller(game);
        using var plan = installer.Prepare(Title(target.Vehicle.Model, request.Sound), Ops(target, request.Sound));
        if (!plan.CanInstall)
            return new(false, "", string.Join("\n", plan.Errors.DefaultIfEmpty(L.T("core.oiv.nothingInstall"))));
        var result = installer.Install(plan, library);
        return result.State == Transactions.TxState.Completed
            ? new(true, result.Id, L.T("core.dlc.soundAssigned", target.Vehicle.Model, request.Sound))
            : new(false, result.Id, result.Message);
    }

    /// <summary>Recognizes a sound change by its History title (in any interface language).</summary>
    public static (string Model, string Sound)? Parse(string title)
    {
        foreach (var rx in TitlePatterns.Value)
            if (rx.Match(title) is { Success: true } m)
                return (m.Groups["model"].Value, m.Groups["sound"].Value);
        return null;
    }

    private static readonly Lazy<IReadOnlyList<Regex>> TitlePatterns = new(() =>
        LocalizationService
            .Current.AllValues("core.dlc.engineSoundTitle")
            .Distinct()
            .Select(t => new Regex(
                "^"
                    + Regex
                        .Escape(t)
                        .Replace(@"\{0}", @"(?<model>[A-Za-z0-9_\-]+)")
                        .Replace(@"\{1}", @"(?<sound>[A-Za-z0-9_\-]+)")
                    + "$"
            ))
            .ToList()
    );
}
