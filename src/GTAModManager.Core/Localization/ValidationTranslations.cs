namespace GTAModManager.Core.Localization;

public sealed partial class LocalizationService
{
    /// <summary>Localized display of own validation errors; raw diagnostics remain stable for tooling.</summary>
    public IReadOnlyList<string> LocalizedDiagnostics
    {
        get
        {
            lock (gate)
                return diagnostics.Select(message => TranslateValidation(message, 0)).ToArray();
        }
    }

    private string TranslateValidation(string message, int depth)
    {
        if (depth > 4)
            return message;
        var exact = message switch
        {
            "Pack exceeds 2 MB." => "tooLarge",
            "Unsupported schemaVersion." => "schema",
            "Invalid pack id." => "id",
            "Unknown culture." => "culture",
            "strings must be an object." => "strings",
            "Invalid key or too many keys." => "keys",
            "String exceeds 32768 characters." => "length",
            "Plural values require other." => "other",
            "Invalid plural variant." => "plural",
            "Plural variants have incompatible placeholders." => "pluralParameters",
            "Placeholder indices differ from the source." => "parameters",
            "Placeholder index exceeds 127." => "index",
            "Placeholder alignment exceeds 4096." => "alignment",
            "Standard numeric format precision exceeds 256." => "precision",
            _ => null,
        };
        if (exact is not null)
            return Text("localization.validation." + exact);
        const string duplicateId = "Duplicate or protected built-in id: ";
        const string duplicateProperty = "Duplicate JSON property: ";
        if (message.StartsWith(duplicateId, StringComparison.Ordinal))
            return Text("localization.validation.duplicateId", message[duplicateId.Length..]);
        if (message.StartsWith(duplicateProperty, StringComparison.Ordinal))
            return Text("localization.validation.duplicateProperty", message[duplicateProperty.Length..]);
        if (message.StartsWith("Invalid ", StringComparison.Ordinal))
            return Text("localization.validation.field", message[8..]);
        if (
            message.EndsWith(" characters.", StringComparison.Ordinal)
            && message.IndexOf(" exceeds ", StringComparison.Ordinal) is var p
            && p > 0
        )
            return Text("localization.validation.metadataLength", message[..p], message[(p + 9)..^12]);
        var separator = message.IndexOf(": ", StringComparison.Ordinal);
        if (separator > 0)
        {
            var tail = TranslateValidation(message[(separator + 2)..], depth + 1);
            if (Describe(tail) is not null)
                return Join("", [message[..(separator + 2)], tail]);
        }
        return message; // Third-party/runtime exception details are data, not resource keys.
    }
}
