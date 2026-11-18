using System.Text.Json;
using System.Xml;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>
/// JSON and XML: read-only. A binding is a property/element/attribute with a key-like name
/// and a string value that is strictly recognized as a key. Numbers without confirmation need confirmation.
/// Writing JSON/XML is not supported yet: we cannot reliably preserve the formatting of such files.
/// </summary>
public static class StructuredKeybindParsers
{
    private const string ReadOnlyReason =
        "Изменение JSON/XML пока не поддерживается — измените значение в файле вручную.";

    public static List<KeybindEntry> ParseJson(string text, string rel, ModRef mod)
    {
        var result = new List<KeybindEntry>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(
                text,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }
            );
        }
        catch (JsonException)
        {
            return result;
        }
        using (doc)
            Walk(doc.RootElement, "");
        return result;

        void Walk(JsonElement e, string path)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                {
                    var childPath = path.Length == 0 ? p.Name : path + "." + p.Name;
                    if (p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        Add(
                            result,
                            KeybindFormat.Json,
                            rel,
                            mod,
                            path,
                            p.Name,
                            p.Value.ToString(),
                            p.Value.ValueKind == JsonValueKind.Number
                        );
                    else
                        Walk(p.Value, childPath);
                }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var item in e.EnumerateArray())
                    Walk(item, $"{path}[{i++}]");
            }
        }
    }

    public static List<KeybindEntry> ParseXml(string text, string rel, ModRef mod)
    {
        var result = new List<KeybindEntry>();
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            using var r = XmlReader.Create(
                new StringReader(text),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }
            );
            doc.Load(r);
        }
        catch (XmlException)
        {
            return result;
        }

        foreach (XmlElement el in doc.GetElementsByTagName("*"))
        {
            var section = el.ParentNode is XmlElement parent ? parent.Name : "";
            if (!el.HasChildNodes || el.ChildNodes.Count == 1 && el.FirstChild is XmlText)
                Add(result, KeybindFormat.Xml, rel, mod, section, el.Name, el.InnerText, false);
            foreach (XmlAttribute a in el.Attributes)
                Add(result, KeybindFormat.Xml, rel, mod, el.Name, a.Name, a.Value, false);
        }
        return result;
    }

    private static void Add(
        List<KeybindEntry> result,
        KeybindFormat format,
        string rel,
        ModRef mod,
        string section,
        string name,
        string value,
        bool numeric
    )
    {
        var (cand, weak) = KeyParamRules.Classify(name);
        if (!cand || KeyNotation.IsUnassigned(value))
            return;
        var r = KeyNotation.ParseValue(value);
        if (r is null)
            return;
        if (weak && (r.Syntax != ValueSyntax.Names || !r.Strict))
            return;
        bool strict = r.Strict && !numeric;
        result.Add(
            new KeybindEntry
            {
                Id = $"{rel}|{section}|{name}",
                ModId = mod.Id,
                ModName = mod.Name,
                Action = KeyParamRules.Humanize(name),
                Chord = r.Chord,
                Format = format,
                File = rel,
                Section = section,
                Params = [new ParamRef(name, -1, value, ParamRole.Whole, r.Syntax, r.Style)],
                Status = strict ? KeybindStatus.Recognized : KeybindStatus.NeedsConfirmation,
                StatusNote = strict ? null : r.Note ?? L.T("core.keybinds.numberMayKeyCodeSomething"),
                Context = KeyParamRules.ContextFrom(section, name),
                Editable = false,
                EditBlockReason = ReadOnlyReason,
            }
        );
    }
}

/// <summary>
/// The modhub.json manifest is an explicit description of hotkeys by the mod author (a future ModHub standard):
/// <code>
/// { "id": "nos", "name": "N.O.S.", "keybinds": [
///   { "action": "Nitro", "file": "scripts/NOS.ini", "section": "Controls", "key": "NitroKey",
///     "modifierKey": "NitroModifier", "default": "CapsLock", "context": "vehicle" } ] }
/// </code>
/// Paths are relative to the manifest folder. The value is read from the referenced INI; the author's description sets
/// the action name, context and default value.
/// </summary>
public sealed class ModHubManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ManifestKeybind> Keybinds { get; set; } = [];

    public sealed class ManifestKeybind
    {
        public string Action { get; set; } = "";
        public string File { get; set; } = "";
        public string Section { get; set; } = "";
        public string Key { get; set; } = "";
        public string? ModifierKey { get; set; }
        public string? Default { get; set; }
        public string? Context { get; set; }
    }

    public static ModHubManifest? TryParse(string text)
    {
        try
        {
            var m = JsonSerializer.Deserialize<ModHubManifest>(
                text,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }
            );
            return m is { Id.Length: > 0 } ? m : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static KeybindContext ParseContext(string? s) =>
        s?.Trim().ToLowerInvariant() switch
        {
            "vehicle" or "car" => KeybindContext.Vehicle,
            "onfoot" or "foot" or "on-foot" => KeybindContext.OnFoot,
            "menu" => KeybindContext.Menu,
            "global" => KeybindContext.Global,
            _ => KeybindContext.Unknown,
        };
}
