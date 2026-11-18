using System.Xml.Linq;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Oiv;

/// <summary>
/// NaturalVision Enhanced installs the onigiri loader: the game takes common\data\dlclist.xml and gameconfig.xml from
/// onigiri\common\data\ and does not see their copies in mods\update\update.rpf. So add-on DLC registration and
/// raised pools must go into these files too, otherwise the pack is "installed" but the engine never mounts it.
/// </summary>
public static class OnigiriOverlay
{
    public const string DlcListInner = @"common\data\dlclist.xml";
    public const string GameConfigInner = @"common\data\gameconfig.xml";
    public const string UpdateRpf = @"mods\update\update.rpf";

    public static string OverlayOf(string inner) => @"onigiri\" + inner;

    /// <summary>Adds missing packs and removes uninstalled ones; existing lines are never rewritten.</summary>
    public static (string text, List<string> log) SyncPacks(
        string overlay,
        IEnumerable<string> add,
        IEnumerable<string> remove
    )
    {
        var log = new List<string>();
        var present = Dlc.DlcList.Packs(overlay).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in add.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (present.Contains(pack))
                continue;
            (overlay, var changed) = Dlc.DlcList.Add(overlay, pack);
            if (changed)
            {
                present.Add(pack);
                log.Add(L.T("core.oiv.onigiriNaturalvisionEnhancedPackAdded", pack));
            }
        }
        foreach (var pack in remove.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            (overlay, var changed) = Dlc.DlcList.Remove(overlay, pack);
            if (changed)
                log.Add(L.T("core.oiv.onigiriNaturalvisionEnhancedPackRemoved", pack));
        }
        return (overlay, log);
    }

    /// <summary>Pools (PoolSizes) from source that are larger than in overlay are raised; nothing is lowered.</summary>
    public static (string text, List<string> log) RaisePools(string overlay, string source)
    {
        var log = new List<string>();
        var wanted = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, value) in Pools(InstallDocuments.Parse(source)))
            if (!wanted.TryGetValue(name, out var v) || value > v)
                wanted[name] = value;
        var doc = InstallDocuments.Parse(overlay);
        foreach (var (name, attribute, value) in Pools(doc).ToList())
        {
            if (!wanted.TryGetValue(name, out var target) || target <= value)
                continue;
            attribute.Value = target.ToString(System.Globalization.CultureInfo.InvariantCulture);
            log.Add(L.T("core.oiv.onigiriNaturalvisionEnhancedPoolRaised", name, value, target));
        }
        if (log.Count == 0)
            return (overlay, log);
        string declaration = doc.Declaration is { } d ? d + (overlay.Contains("\r\n") ? "\r\n" : "\n") : "";
        return (declaration + doc.ToString(SaveOptions.DisableFormatting), log);
    }

    private static IEnumerable<(string name, XAttribute attribute, long value)> Pools(XDocument doc)
    {
        foreach (var item in doc.Descendants("PoolSizes").Elements("Entries").Elements("Item"))
        {
            var name = item.Element("PoolName")?.Value.Trim();
            var attribute = item.Element("PoolSize")?.Attribute("value");
            if (
                string.IsNullOrEmpty(name)
                || attribute is null
                || !long.TryParse(
                    attribute.Value.Trim(),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value
                )
            )
                continue;
            yield return (name, attribute, value);
        }
    }
}
