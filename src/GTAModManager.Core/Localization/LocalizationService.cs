using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GTAModManager.Core.Localization;

/// <summary>A declarative language pack. Values are plain text or one/few/many/other plural objects.</summary>
public sealed class LanguagePack
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Culture { get; init; } = "ru-RU";
    public string DisplayName { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1";
    public IReadOnlyDictionary<string, JsonElement> Strings { get; init; } =
        new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>());

    [JsonIgnore]
    public bool IsBuiltIn { get; internal init; }
}

public sealed record LocalizedText(string Key, object?[] Arguments)
{
    internal bool IsComposition { get; init; }
    internal string? Separator { get; init; }
    internal TextTransform Transform { get; init; }
    internal int MaxLength { get; init; }
}

internal enum TextTransform
{
    None,
    Truncate,
    TrimEnd,
}

public static class L
{
    public static string T(string key, params object?[] args) => LocalizationService.Current.Text(key, args);

    public static string Owned(string value) => LocalizationService.Current.Owned(value);

    public static string Source(string value) => LocalizationService.Current.SourceText(value);

    public static string Display(string value) =>
        LocalizationService.Current.Describe(value) is { } message
            ? LocalizationService.Current.Render(message)
            : value;

    public static string Truncate(string value, int maxLength) =>
        LocalizationService.Current.Truncate(value, maxLength);

    public static string TrimEnd(string value) => LocalizationService.Current.TrimEnd(value);

    public static string Join(string? separator, IEnumerable<string?> values) =>
        LocalizationService.Current.Join(separator, values);

    public static string Join(string? separator, params string?[] values) =>
        Join(separator, (IEnumerable<string?>)values);

    public static string Join<T>(string? separator, IEnumerable<T> values) =>
        Join(separator, values.Select(value => value?.ToString()));
}

/// <summary>Loads data-only JSON packs, retaining explicit token identity for live UI updates.</summary>
public sealed partial class LocalizationService
{
    private const int MaximumBytes = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly ConditionalWeakTable<string, LocalizedText> tokens = new();
    private readonly List<string> diagnostics = [];
    private List<LanguagePack> packs = [];
    private OwnedMessageAdapter? ownedMessages;
    private string selectedId = "official.ru";
    public static LocalizationService Current { get; } = new();
    public event EventHandler? LanguageChanged;
    public IReadOnlyList<LanguagePack> Packs
    {
        get
        {
            lock (gate)
                return packs.AsReadOnly();
        }
    }
    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (gate)
                return diagnostics.ToArray();
        }
    }
    public string SelectedId
    {
        get
        {
            lock (gate)
                return selectedId;
        }
    }
    public CultureInfo Culture
    {
        get
        {
            lock (gate)
                return CultureInfo.GetCultureInfo(Selected.Culture);
        }
    }
    private LanguagePack Selected => packs.First(p => p.Id == selectedId);

    public LocalizationService() => ReloadCore(null);

    public void Reload(string directory)
    {
        lock (gate)
            ReloadCore(directory);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadCore(string? directory)
    {
        var previousCulture = packs.FirstOrDefault(p => p.Id == selectedId)?.Culture ?? "ru-RU";
        diagnostics.Clear();
        var loaded = new List<LanguagePack>();
        var assembly = typeof(LocalizationService).Assembly;
        foreach (
            var name in assembly
                .GetManifestResourceNames()
                .Where(n =>
                    n.Contains(".Localization.Languages.", StringComparison.Ordinal)
                    && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                )
                .Order(StringComparer.Ordinal)
        )
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            TryLoad(stream, name, true, loaded);
        }
        if (!loaded.Any(p => p.Id == "official.ru"))
            loaded.Add(
                new LanguagePack
                {
                    Id = "official.ru",
                    Culture = "ru-RU",
                    DisplayName = "Русский",
                    IsBuiltIn = true,
                }
            );
        if (directory is not null && Directory.Exists(directory))
        {
            try
            {
                foreach (
                    var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase)
                )
                {
                    try
                    {
                        using var stream = File.OpenRead(file);
                        TryLoad(stream, Path.GetFileName(file), false, loaded);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Report($"{Path.GetFileName(file)}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(ex.Message);
            }
        }
        packs = loaded;
        ownedMessages = null;
        if (!packs.Any(p => p.Id == selectedId))
            selectedId = Official(previousCulture)?.Id ?? "official.ru";
    }

    public bool Select(string id)
    {
        lock (gate)
        {
            if (!packs.Any(p => p.Id == id))
                return false;
            if (selectedId == id)
                return true;
            selectedId = id;
        }
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Plain values of one key in every loaded pack (no arguments). Used to recognize text that Mod Hub stored
    /// in whatever language was active at the time, such as operation titles in the history.
    /// </summary>
    public IReadOnlyList<string> AllValues(string key)
    {
        lock (gate)
            return packs
                .Select(p =>
                    p.Strings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null
                )
                .OfType<string>()
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
    }

    public string Text(string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (gate)
        {
            return Materialize(new LocalizedText(key, (object?[])args.Clone()));
        }
    }

    private string Materialize(LocalizedText descriptor)
    {
        var value = RenderCore(descriptor, 0);
        // The runtime canonicalizes empty strings; leave those untracked.
        if (value.Length == 0)
            return string.Empty;
        var unique = new string(value.ToCharArray());
        tokens.Add(unique, descriptor);
        return unique;
    }

    public LocalizedText? Describe(string value) => tokens.TryGetValue(value, out var result) ? result : null;

    public string Render(LocalizedText text)
    {
        lock (gate)
            return Materialize(text with { Arguments = (object?[])text.Arguments.Clone() });
    }

    public string Join(string? separator, IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (gate)
            return Materialize(
                new LocalizedText("", values.Cast<object?>().ToArray()) { IsComposition = true, Separator = separator }
            );
    }

    /// <summary>Canonical Russian representation for persistence; never changes selection or emits events.</summary>
    public string SourceText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
            return tokens.TryGetValue(value, out var descriptor)
                ? RenderCore(descriptor, 0, packs.First(p => p.Id == "official.ru"))
                : value;
    }

    /// <summary>Retains the first maxLength characters, adding an ellipsis when shortened.</summary>
    public string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        lock (gate)
            return tokens.TryGetValue(value, out var descriptor)
                ? Materialize(
                    new LocalizedText("", [descriptor]) { Transform = TextTransform.Truncate, MaxLength = maxLength }
                )
                : Shorten(value, maxLength);
    }

    public string TrimEnd(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
            return tokens.TryGetValue(value, out var descriptor)
                ? Materialize(new LocalizedText("", [descriptor]) { Transform = TextTransform.TrimEnd })
                : value.TrimEnd();
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] + "…" : value;

    /// <summary>Explicit display boundary for application-owned invariant statuses and serialized CLI output.</summary>
    public string Owned(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (gate)
        {
            if (value.Length == 0 || value.Length > MaximumBytes || tokens.TryGetValue(value, out _))
                return value;
            ownedMessages ??= new OwnedMessageAdapter(packs.First(p => p.Id == "official.ru").Strings);
            string ConvertLine(string line)
            {
                var descriptor = ownedMessages.Match(line, key => Selected.Strings.ContainsKey(key));
                return descriptor is null ? line : Materialize(descriptor);
            }
            if (!value.Contains('\r') && !value.Contains('\n'))
                return ConvertLine(value);
            var parts = new List<string?>();
            var changed = false;
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] is not ('\r' or '\n'))
                    continue;
                var line = value[start..i];
                var converted = ConvertLine(line);
                changed |= tokens.TryGetValue(converted, out _);
                parts.Add(converted);
                var newlineLength = value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n' ? 2 : 1;
                parts.Add(value.Substring(i, newlineLength));
                i += newlineLength - 1;
                start = i + 1;
            }
            var tail = ConvertLine(value[start..]);
            changed |= tokens.TryGetValue(tail, out _);
            parts.Add(tail);
            return changed ? Join("", parts) : value;
        }
    }

    private string RenderCore(LocalizedText text, int depth, LanguagePack? renderPack = null)
    {
        if (depth > 32)
            return text.Key;
        var args = text
            .Arguments.Select(arg =>
                arg switch
                {
                    string s when tokens.TryGetValue(s, out var nested) => RenderCore(nested, depth + 1, renderPack),
                    LocalizedText nested => RenderCore(nested, depth + 1, renderPack),
                    _ => arg,
                }
            )
            .ToArray();
        if (text.Transform == TextTransform.Truncate)
            return Shorten((string)args[0]!, text.MaxLength);
        if (text.Transform == TextTransform.TrimEnd)
            return ((string)args[0]!).TrimEnd();
        if (text.IsComposition)
            return string.Join(text.Separator, args);
        var selected = renderPack ?? Selected;
        var fallback = Official(selected.Culture);
        var candidates = new[] { selected, fallback, packs.First(p => p.Id == "official.ru") }
            .OfType<LanguagePack>()
            .Distinct();
        HashSet<int>? reference = null;
        var source = packs.First(p => p.Id == "official.ru");
        if (source.Strings.TryGetValue(text.Key, out var baseValue))
            reference = Indices(Variant(baseValue, source.Culture, args));
        foreach (var pack in candidates)
        {
            if (!pack.Strings.TryGetValue(text.Key, out var value))
                continue;
            var template = Variant(value, pack.Culture, args);
            try
            {
                if (reference is not null && !reference.SetEquals(Indices(template)))
                    throw new FormatException("Placeholder indices differ from the source.");
                return string.Format(CultureInfo.GetCultureInfo(selected.Culture), template, args);
            }
            catch (FormatException ex)
            {
                Report($"{pack.Id}/{text.Key}: {ex.Message}");
            }
        }
        return text.Key;
    }

    private LanguagePack? Official(string culture) =>
        packs.FirstOrDefault(p => p.IsBuiltIn && p.Culture.Equals(culture, StringComparison.OrdinalIgnoreCase))
        ?? packs.FirstOrDefault(p =>
            p.IsBuiltIn
            && CultureInfo.GetCultureInfo(p.Culture).TwoLetterISOLanguageName
                == CultureInfo.GetCultureInfo(culture).TwoLetterISOLanguageName
        );

    public void ExportTemplate(string path)
    {
        lock (gate)
        {
            var source = packs.First(p => p.Id == "official.ru");
            var template = new LanguagePack
            {
                Id = "custom",
                Culture = source.Culture,
                DisplayName = "Custom language",
                Author = "",
                Version = "1.0",
                Strings = source.Strings,
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(
                    template,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        WriteIndented = true,
                    }
                ),
                new UTF8Encoding(false)
            );
        }
    }

    private void TryLoad(Stream stream, string label, bool builtin, List<LanguagePack> loaded)
    {
        try
        {
            if (stream.CanSeek && stream.Length > MaximumBytes)
                throw new InvalidDataException("Pack exceeds 2 MB.");
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = stream.Read(chunk)) != 0)
            {
                if (buffer.Length + count > MaximumBytes)
                    throw new InvalidDataException("Pack exceeds 2 MB.");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            ValidateUniqueProperties(json.RootElement);
            var root = json.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("Unsupported schemaVersion.");
            var id = Required(root, "id", 128);
            if (id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                throw new InvalidDataException("Invalid pack id.");
            if (
                (!builtin && id.StartsWith("official.", StringComparison.OrdinalIgnoreCase))
                || loaded.Any(p => p.Id == id)
            )
                throw new InvalidDataException("Duplicate or protected built-in id: " + id);
            var culture = Required(root, "culture", 128);
            if (
                !CultureInfo
                    .GetCultures(CultureTypes.AllCultures)
                    .Any(c => c.Name.Equals(culture, StringComparison.OrdinalIgnoreCase))
            )
                throw new InvalidDataException("Unknown culture.");
            var displayName = Required(root, "displayName", 256);
            var strings = root.GetProperty("strings");
            if (strings.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("strings must be an object.");
            if (strings.GetPropertyCount() > 15000)
                throw new InvalidDataException("Invalid key or too many keys.");
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in strings.EnumerateObject())
            {
                if (values.Count >= 15000 || property.Name.Length is 0 or > 256)
                    throw new InvalidDataException("Invalid key or too many keys.");
                try
                {
                    ValidateValue(property.Value);
                    foreach (var text in Texts(property.Value))
                        _ = Indices(text);
                }
                catch (FormatException ex)
                {
                    Report($"{label}/{property.Name}: {ex.Message}");
                    continue;
                }
                values.Add(property.Name, property.Value.Clone());
            }
            loaded.Add(
                new LanguagePack
                {
                    Id = id,
                    Culture = culture,
                    DisplayName = displayName,
                    Author = Optional(root, "author", 256),
                    Version = Optional(root, "version", 128),
                    IsBuiltIn = builtin,
                    Strings = new ReadOnlyDictionary<string, JsonElement>(values),
                }
            );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or InvalidDataException
                        or InvalidOperationException
                        or KeyNotFoundException
                        or FormatException
                        or CultureNotFoundException
            )
        {
            Report($"{label}: {ex.Message}");
        }
    }

    private static string Required(JsonElement root, string name, int max)
    {
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new InvalidDataException("Invalid " + name);
        return value;
    }

    private static string Optional(JsonElement root, string name, int maxLength)
    {
        var value = root.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
        if (value.Length > maxLength)
            throw new InvalidDataException($"{name} exceeds {maxLength} characters.");
        return value;
    }

    private static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException("Duplicate JSON property: " + property.Name);
            ValidateUniqueProperties(property.Value);
        }
    }

    private static void ValidateValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString()!.Length > 32768)
                throw new InvalidDataException("String exceeds 32768 characters.");
            return;
        }
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("other", out _))
            throw new InvalidDataException("Plural values require other.");
        foreach (var property in value.EnumerateObject())
        {
            if (
                property.Name is not ("zero" or "one" or "two" or "few" or "many" or "other")
                || property.Value.ValueKind != JsonValueKind.String
            )
                throw new InvalidDataException("Invalid plural variant.");
            ValidateValue(property.Value);
        }
        var indices = Texts(value).Select(Indices).ToArray();
        if (indices.Skip(1).Any(i => !i.SetEquals(indices[0])))
            throw new FormatException("Plural variants have incompatible placeholders.");
    }

    private static IEnumerable<string> Texts(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? [value.GetString()!]
            : value.EnumerateObject().Select(p => p.Value.GetString()!);

    private static HashSet<int> Indices(string template)
    {
        _ = CompositeFormat.Parse(template);
        var indices = new HashSet<int>();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
                continue;
            if (i + 1 < template.Length && template[i + 1] == '{')
            {
                i++;
                continue;
            }
            var start = ++i;
            while (i < template.Length && char.IsAsciiDigit(template[i]))
                i++;
            var index = int.Parse(template.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            if (index > 127)
                throw new FormatException("Placeholder index exceeds 127.");
            indices.Add(index);
            while (i < template.Length && template[i] == ' ')
                i++;
            if (i < template.Length && template[i] == ',')
            {
                start = ++i;
                while (i < template.Length && template[i] is not (':' or '}'))
                    i++;
                if (
                    !long.TryParse(
                        template.AsSpan(start, i - start),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var alignment
                    ) || alignment is < -4096 or > 4096
                )
                    throw new FormatException("Placeholder alignment exceeds 4096.");
            }
            if (i < template.Length && template[i] == ':')
            {
                start = ++i;
                while (i < template.Length && template[i] != '}')
                    i++;
                var format = template.AsSpan(start, i - start);
                if (
                    format.Length > 1
                    && char.IsAsciiLetter(format[0])
                    && IsDigits(format[1..])
                    && (
                        !int.TryParse(format[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var precision)
                        || precision > 256
                    )
                )
                    throw new FormatException("Standard numeric format precision exceeds 256.");
            }
            while (i < template.Length && template[i] != '}')
                i++;
        }
        return indices;
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
            if (!char.IsAsciiDigit(character))
                return false;
        return true;
    }

    private static string Variant(JsonElement value, string culture, object?[] args)
    {
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString()!;
        var category = "other";
        if (args.Length > 0 && args[0] is not null && args[0] is not (string or bool or char))
        {
            try
            {
                var number = Math.Abs(Convert.ToDecimal(args[0], CultureInfo.InvariantCulture));
                if (decimal.Truncate(number) == number)
                {
                    var language = CultureInfo.GetCultureInfo(culture).TwoLetterISOLanguageName;
                    if (language is "ru" or "uk")
                    {
                        var last = number % 10;
                        var lastTwo = number % 100;
                        category =
                            last == 1 && lastTwo != 11 ? "one"
                            : last is >= 2 and <= 4 && lastTwo is not (>= 12 and <= 14) ? "few"
                            : "many";
                    }
                    else if (number == 1)
                        category = "one";
                }
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException) { }
        }
        return (value.TryGetProperty(category, out var variant) ? variant : value.GetProperty("other")).GetString()!;
    }

    private void Report(string message)
    {
        if (diagnostics.Count < 500 && !diagnostics.Contains(message, StringComparer.Ordinal))
            diagnostics.Add(message);
    }
}
