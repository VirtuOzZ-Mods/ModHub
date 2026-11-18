using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Dlc;

/// <summary>What an add-on DLC pack mainly adds, judged by its contents rather than by a README.</summary>
public enum DlcPackKind
{
    Unknown,
    Vehicles,
    EngineSounds,
    Peds,
    Map,
}

/// <summary>
/// A vehicle declared in a vehicles.meta of the pack. <paramref name="MetaPath"/> is the chain of archive paths
/// to that meta: nested archives first, the meta itself last (empty when the model was only seen as a .yft).
/// An empty <paramref name="AudioName"/> means the game uses the model name as the sound name.
/// </summary>
public sealed record DlcVehicle(string Model, string AudioName, string VehicleType, IReadOnlyList<string> MetaPath)
{
    /// <summary>The sound the game actually plays for the vehicle.</summary>
    public string EffectiveAudio => AudioName.Length > 0 ? AudioName : Model;

    public bool CanChangeSound => MetaPath.Count > 0;

    /// <summary>The meta has an &lt;audioNameHash&gt; element for this vehicle (possibly empty).</summary>
    public bool HasAudioTag { get; init; }
}

/// <summary>A vehicle sound (an engine) defined in a *_game.dat151.rel of the pack; usable as audioNameHash.</summary>
public sealed record DlcEngineSound(string Name, uint Hash, bool NameKnown);

/// <summary>Result of looking inside a dlc.rpf: vehicles, engine sounds and other content markers.</summary>
public sealed class DlcContent
{
    public required string Pack { get; init; }
    public List<DlcVehicle> Vehicles { get; } = [];
    public List<DlcEngineSound> EngineSounds { get; } = [];
    public bool HasPedData { get; set; }
    public bool HasMapData { get; set; }

    /// <summary>Files that could not be read (encrypted or damaged); the result may be incomplete.</summary>
    public int Unreadable { get; set; }

    public DlcPackKind Kind =>
        Vehicles.Count > 0 ? DlcPackKind.Vehicles
        : EngineSounds.Count > 0 ? DlcPackKind.EngineSounds
        : HasPedData ? DlcPackKind.Peds
        : HasMapData ? DlcPackKind.Map
        : DlcPackKind.Unknown;

    /// <summary>Engine sounds whose names are known (only those can be written into a vehicles.meta).</summary>
    public IEnumerable<string> SoundNames => EngineSounds.Where(s => s.NameKnown).Select(s => s.Name);
}

/// <summary>
/// Recognizes add-on DLC packs by their contents (the same signs OpenIV users look for by hand):
/// a vehicles.meta with CVehicleModelInfo__InitDataList → vehicles (spawn names from modelName);
/// vehicle objects in *_game.dat151.rel → engine sounds (names from the .nametable or matched by hash
/// against the pack, file and wave bank names); ymap/ITYP → map; peds.meta/PED_METADATA_FILE → characters.
/// Read-only; nested archives are opened when stored uncompressed and unencrypted.
/// </summary>
public static partial class DlcInspector
{
    /// <summary>Type of a vehicle object in a dat151 (game data) relation file.</summary>
    public const byte VehicleAudioType = 3;
    private const int MaxDepth = 3;
    private const long MaxTextSize = 8L << 20;

    public static DlcContent Inspect(Stream rpf, string pack)
    {
        using var archive = Rpf7Archive.Open(rpf, leaveOpen: true, name: "dlc.rpf");
        return Inspect(archive, pack);
    }

    /// <summary>Inspects an installed or unpacked dlc.rpf; null if it is not a readable RPF7 archive.</summary>
    public static DlcContent? TryInspectFile(string path, string pack)
    {
        try
        {
            using var archive = Rpf7Archive.OpenRead(path);
            return Inspect(archive, pack);
        }
        catch (Exception ex) when (ex is RpfException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DlcContent Inspect(Rpf7Archive archive, string pack)
    {
        var content = new DlcContent { Pack = pack };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pack };
        var sounds = new List<uint>();
        Walk(archive, [], 0, content, names, sounds);
        foreach (var v in content.Vehicles)
            names.Add(v.Model);
        var byHash = new Dictionary<uint, string>();
        foreach (var candidate in names.SelectMany(Variants))
            byHash.TryAdd(Joaat(candidate), candidate.ToLowerInvariant());
        foreach (var hash in sounds.Distinct())
            content.EngineSounds.Add(
                byHash.TryGetValue(hash, out var name) ? new(name, hash, true) : new($"0x{hash:X8}", hash, false)
            );
        return content;
    }

    private static void Walk(
        Rpf7Archive archive,
        List<string> chain,
        int depth,
        DlcContent content,
        HashSet<string> names,
        List<uint> sounds
    )
    {
        foreach (var file in archive.Files)
        {
            var lower = file.Name.ToLowerInvariant();
            if (lower.EndsWith(".ymap") || lower.EndsWith(".ytyp"))
                content.HasMapData = true;
            if (lower is "peds.meta" or "peds.ymt")
                content.HasPedData = true;
            if (lower.EndsWith(".awc"))
            {
                var bank = Path.GetFileNameWithoutExtension(lower);
                names.Add(bank);
                if (bank.EndsWith("_npc"))
                    names.Add(bank[..^4]);
            }
            if (file is not RpfBinaryFile bin)
                continue;
            if (bin.IsEncrypted != 0)
            {
                if (lower.EndsWith(".meta") || lower.EndsWith(".rel") || lower.EndsWith(".xml"))
                    content.Unreadable++;
                continue;
            }
            try
            {
                if (lower.EndsWith(".rpf"))
                {
                    // Language packs hold only text; skipping them keeps the scan fast.
                    if (depth < MaxDepth && bin.CompressedSize == 0 && !lower.EndsWith("dlc.rpf") && !IsLangPack(file))
                    {
                        using var nested = archive.OpenNested(bin);
                        Walk(nested, [.. chain, file.Path], depth + 1, content, names, sounds);
                    }
                }
                else if (lower.EndsWith(".dat151.rel"))
                {
                    names.Add(RelPrefix(lower));
                    sounds.AddRange(
                        RelIndex(archive.ReadContent(bin)).Where(i => i.Type == VehicleAudioType).Select(i => i.Hash)
                    );
                }
                else if (lower.EndsWith(".rel"))
                {
                    var data = archive.ReadContent(bin);
                    names.Add(RelPrefix(lower));
                    // Wave bank names ("DLC_A80FFENG\A80FFENG") name the engine more often than anything else.
                    foreach (var bank in RelNames(data))
                        names.Add(bank.Split('\\')[^1]);
                }
                else if (lower.EndsWith(".nametable"))
                {
                    foreach (var n in Encoding.ASCII.GetString(archive.ReadContent(bin)).Split('\0'))
                        if (n.Trim().Length > 0)
                            names.Add(n.Trim());
                }
                else if ((lower.EndsWith(".meta") || lower == "content.xml") && bin.UncompressedSize <= MaxTextSize)
                {
                    var text = Encoding.UTF8.GetString(archive.ReadContent(bin));
                    if (lower == "content.xml")
                    {
                        if (text.Contains("PED_METADATA_FILE", StringComparison.Ordinal))
                            content.HasPedData = true;
                        if (text.Contains("DLC_ITYP_REQUEST", StringComparison.Ordinal))
                            content.HasMapData = true;
                    }
                    else if (text.Contains("CVehicleModelInfo__InitDataList", StringComparison.Ordinal))
                        foreach (var (model, audio, type, hasAudio) in ParseVehicles(text))
                            if (!content.Vehicles.Any(v => v.Model.Equals(model, StringComparison.OrdinalIgnoreCase)))
                                content.Vehicles.Add(
                                    new(model, audio, type, [.. chain, file.Path]) { HasAudioTag = hasAudio }
                                );
                }
            }
            catch (Exception ex) when (ex is RpfException or InvalidDataException or IOException)
            {
                content.Unreadable++;
            }
        }
        // A pack without a readable vehicles.meta: the models in vehicles.rpf still tell what it is.
        if (depth == 0 && content.Vehicles.Count == 0)
            foreach (var model in VehicleModels(archive, "").Distinct())
                content.Vehicles.Add(new(model, "", "", []));
    }

    /// <summary>Car models (.yft without _hi) inside vehicles archives or folders, not tuning parts.</summary>
    private static IEnumerable<string> VehicleModels(Rpf7Archive archive, string prefix)
    {
        foreach (var f in archive.Files)
        {
            var path = prefix + f.Path;
            if (
                path.Contains("vehiclemods", StringComparison.OrdinalIgnoreCase)
                || path.Contains("_mods", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            if (
                f is RpfBinaryFile { IsEncrypted: 0, CompressedSize: 0 } nested
                && f.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)
                && prefix.Count(c => c == '\\') < MaxDepth
                && !IsLangPack(f)
            )
            {
                List<string> inner;
                try
                {
                    using var child = archive.OpenNested(nested);
                    inner = VehicleModels(child, path + "\\").ToList();
                }
                catch (RpfException)
                {
                    continue;
                }
                foreach (var m in inner)
                    yield return m;
            }
            else if (
                f.Name.EndsWith(".yft", StringComparison.OrdinalIgnoreCase)
                && !f.Name.EndsWith("_hi.yft", StringComparison.OrdinalIgnoreCase)
                && path.Contains("vehicles", StringComparison.OrdinalIgnoreCase)
            )
                yield return Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
        }
    }

    private static bool IsLangPack(RpfEntry file) =>
        file.Path.Contains(@"data\lang\", StringComparison.OrdinalIgnoreCase);

    /// <summary>"dlcr352sound_game.dat151.rel" → "dlcr352sound".</summary>
    private static string RelPrefix(string relName)
    {
        var name = relName.Split('.')[0];
        int underscore = name.LastIndexOf('_');
        return underscore > 0 ? name[..underscore] : name;
    }

    /// <summary>Name spellings a pack author typically uses for the same engine.</summary>
    private static IEnumerable<string> Variants(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        if (n.Length == 0)
            yield break;
        yield return n;
        if (n.StartsWith("dlc_"))
            yield return n[4..];
        else if (n.StartsWith("dlc"))
            yield return n[3..];
        foreach (var suffix in new[] { "_npc", "_engine", "_game", "_sounds" })
            if (n.EndsWith(suffix))
                yield return n[..^suffix.Length];
    }

    /// <summary>Vehicles from a vehicles.meta: (modelName, audioNameHash, type, whether audioNameHash is present).</summary>
    public static IEnumerable<(string Model, string Audio, string Type, bool HasAudio)> ParseVehicles(string xml)
    {
        var result = new List<(string, string, string, bool)>();
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            using var reader = XmlReader.Create(
                new StringReader(xml.TrimStart('﻿')),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }
            );
            doc.Load(reader);
            foreach (XmlNode item in doc.SelectNodes("//InitDatas/Item")!)
            {
                var model = item.SelectSingleNode("modelName")?.InnerText.Trim() ?? "";
                if (model.Length > 0)
                    result.Add(
                        (
                            model,
                            item.SelectSingleNode("audioNameHash")?.InnerText.Trim() ?? "",
                            item.SelectSingleNode("type")?.InnerText.Trim() ?? "",
                            item.SelectSingleNode("audioNameHash") is not null
                        )
                    );
            }
        }
        catch (XmlException)
        {
            // Hand-edited metas are often slightly broken; the game itself is lenient, so fall back to the tags.
            foreach (Match m in ModelTag().Matches(xml))
                result.Add((m.Groups[1].Value.Trim(), "", "", false));
        }
        return result;
    }

    [GeneratedRegex(@"<modelName>\s*([^<]+?)\s*</modelName>")]
    private static partial Regex ModelTag();

    /// <summary>Index of a RAGE audio relation file (.rel): hash and object type of each item.</summary>
    public static IReadOnlyList<(uint Hash, byte Type)> RelIndex(byte[] rel)
    {
        var items = new List<(uint, byte)>();
        var span = rel.AsSpan();
        if (span.Length < 8)
            return items;
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        int pos = 8;
        if (dataLength > span.Length - pos)
            return items;
        var data = span.Slice(pos, (int)dataLength);
        pos += (int)dataLength;
        if (!SkipNameTable(span, ref pos) || pos + 4 > span.Length)
            return items;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        pos += 4;
        if (count > (span.Length - pos) / 12)
            return items;
        for (int i = 0; i < count; i++, pos += 12)
        {
            uint hash = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
            if (offset < data.Length)
                items.Add((hash, data[(int)offset]));
        }
        return items;
    }

    /// <summary>Wave bank names listed in the name table of a .rel (dat54 sound files reference their banks there).</summary>
    public static IReadOnlyList<string> RelNames(byte[] rel)
    {
        var span = rel.AsSpan();
        if (span.Length < 16)
            return [];
        uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (dataLength > span.Length - 16)
            return [];
        int pos = 8 + (int)dataLength;
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
        pos += 8;
        if (count > 1024 || length < 4 + 4L * count || pos - 4L + length > span.Length)
            return [];
        var names = span.Slice(pos + 4 * (int)count, (int)(length - 4 - 4 * count));
        return Encoding.ASCII.GetString(names).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool SkipNameTable(ReadOnlySpan<byte> span, ref int pos)
    {
        if (pos + 8 > span.Length)
            return false;
        // The table length counts everything after itself: the count, the offsets and the strings.
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
        long next = pos + 4L + length;
        if (count > 1024 || length < 4 + 4L * count || next > span.Length)
            return false;
        pos = (int)next;
        return true;
    }

    /// <summary>Jenkins one-at-a-time hash of the lower-case name, as the game hashes audioNameHash.</summary>
    public static uint Joaat(string text)
    {
        uint h = 0;
        foreach (char c in text.ToLowerInvariant())
        {
            h += (byte)c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }
}
