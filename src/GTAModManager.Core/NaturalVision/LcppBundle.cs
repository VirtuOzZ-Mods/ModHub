using System.Security.Cryptography;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.NaturalVision;

public sealed partial class NveBundle
{
    public const string LcppTitle = "Liberty City Preservation Project";
    public const string LcppHdTitle = LcppTitle + " (HD Textures)";
    static readonly string[] LcppParts =
    [
        "one",
        "two",
        "three",
        "four",
        "five",
        "six",
        "seven",
        "eight",
        "nine",
        "ten",
        "eleven",
        "twelve",
    ];

    public static int LcppPart(string path) => Math.Max(LcppPart(path, false), LcppPart(path, true));

    public static bool IsLcppHdPart(string path) => LcppPart(path, true) > 0;

    private static int LcppPart(string path, bool hd)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!Path.GetExtension(path).Equals(".oiv", StringComparison.OrdinalIgnoreCase))
            return 0;
        string prefix = hd ? "Liberty City Installer - HD Part " : "Liberty City Installer - Part ";
        for (int i = 0; i < (hd ? 12 : 5); i++)
            if (
                name.Equals(prefix + LcppParts[i], StringComparison.OrdinalIgnoreCase)
                || name.Equals($"{i + 1}. {prefix}{LcppParts[i]}", StringComparison.OrdinalIgnoreCase)
            )
                return i + 1;
        return 0;
    }

    NveCatalog AnalyzeLcpp()
    {
        var result = new List<NveComponent>();
        bool hd = Files.Any(IsLcppHdPart);
        int required = hd ? 12 : 5;
        foreach (
            string path in Files.Where(p => Path.GetExtension(p).Equals(".oiv", StringComparison.OrdinalIgnoreCase))
        )
        {
            string name = Path.GetFileName(path);
            if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                continue;
            int part = LcppPart(path);
            if (part > 0 && IsLcppHdPart(path) != hd)
                throw new InvalidDataException(L.T("lcpp.invalidParts"));
            string[] pieces = path.Split('\\');
            int group = Array.FindIndex(pieces, p => p.Equals("Optional Add-Ons", StringComparison.OrdinalIgnoreCase));
            if (part == 0 && (group < 0 || !name.StartsWith("INSTALL", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(L.T("lcpp.unknownPackage", path));
            using var input = Open(path);
            var document = InstallDocuments.Parse(Metadata(input));
            if (
                document.Root?.Name != "package"
                || (string?)document.Root.Attribute("target") is not (null or "" or "Five")
            )
                throw new InvalidDataException(L.T("core.nve.invalidPackageFormat", path));
            var metadata = document.Root.Element("metadata");
            string title = metadata?.Element("name")?.Value.Trim() ?? "";
            if (
                part > 0
                && !title.Equals(
                    (hd ? "Liberty City Installer - HD Part " : "Liberty City Installer - Part ") + LcppParts[part - 1],
                    StringComparison.OrdinalIgnoreCase
                )
            )
                throw new InvalidDataException(L.T("lcpp.invalidParts"));
            string description = DocumentRequirements.PlainDocumentation(metadata?.Element("description")?.Value ?? "");
            string id =
                part > 0
                    ? "core-" + part
                    : "option-"
                        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];
            result.Add(
                new(
                    id,
                    path,
                    title.Length > 0 ? title : name,
                    description,
                    description,
                    part > 0 ? ""
                        : group + 1 < pieces.Length - 1 ? pieces[group + 1]
                        : title,
                    part > 0 ? part : 100,
                    part > 0,
                    Size(path)
                )
            );
        }
        if (
            result.Count(c => c.Core) != required
            || Enumerable.Range(1, required).Any(i => result.Count(c => c.Id == "core-" + i) != 1)
        )
            throw new InvalidDataException(L.T("lcpp.invalidParts"));
        return new(
            Source,
            Stamp(),
            GameEdition.Legacy,
            result.OrderBy(c => c.Order).ThenBy(c => c.Source, StringComparer.OrdinalIgnoreCase).ToArray()
        )
        {
            Product = "lcpp",
        };
    }
}
