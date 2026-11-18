using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GTAModManager.Core.Localization;

/// <summary>Opt-in adapter for serialized application messages. Never used by Text or Describe.</summary>
internal sealed class OwnedMessageAdapter
{
    // These UI values were deliberately retained invariant for machine comparisons.
    private static readonly HashSet<string> InvariantUiKeys =
    [
        "ui.common.error",
        "ui.localization.file",
        "ui.localization.multipleTimes",
        "ui.common.female",
        "ui.common.male",
        "ui.common.error2",
        "ui.common.downloadInterruptedCanOpenAuthor",
        "ui.common.loadingBuiltBrowser",
        "ui.common.naturalvisionWizardOpened",
        "ui.common.bindingsNotesOpenPlanBefore",
        "ui.common.destinationsWereSelectedManuallyCheck",
        "ui.common.applyingConfirmedPlanVerifyingFiles",
    ];
    private readonly Dictionary<string, List<string>> literals = new(StringComparer.Ordinal);
    private readonly List<Pattern> patterns = [];

    private sealed record Pattern(string Key, Regex Regex, int[] Indices, int ArgumentCount, int LiteralLetters);

    public OwnedMessageAdapter(IReadOnlyDictionary<string, JsonElement> source)
    {
        foreach (var (key, element) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (
                !(
                    key.StartsWith("core.", StringComparison.Ordinal)
                    || key.StartsWith("cli.", StringComparison.Ordinal)
                    || InvariantUiKeys.Contains(key)
                )
                || element.ValueKind != JsonValueKind.String
            )
                continue;
            var template = element.GetString()!;
            var format = System.Text.CompositeFormat.Parse(template);
            if (format.MinimumArgumentCount == 0)
            {
                var literal = string.Format(CultureInfo.InvariantCulture, template, []);
                if (!literals.TryGetValue(literal, out var keys))
                    literals[literal] = keys = [];
                keys.Add(key);
                continue;
            }
            var pattern = BuildPattern(key, template, format.MinimumArgumentCount);
            if (pattern.LiteralLetters >= 4)
                patterns.Add(pattern);
        }
    }

    public LocalizedText? Match(string value, Func<string, bool> hasTranslation)
    {
        if (value.Length > 32768)
            return null;
        if (literals.TryGetValue(value, out var keys))
            return new(keys.FirstOrDefault(hasTranslation) ?? keys[0], []);
        foreach (
            var pattern in patterns
                .OrderByDescending(p => hasTranslation(p.Key))
                .ThenByDescending(p => p.LiteralLetters)
        )
        {
            Match match;
            try
            {
                match = pattern.Regex.Match(value);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
            if (!match.Success)
                continue;
            var args = new object?[pattern.ArgumentCount];
            var seen = new HashSet<int>();
            var compatible = true;
            for (var i = 0; i < pattern.Indices.Length; i++)
            {
                var index = pattern.Indices[i];
                var captured = match.Groups["a" + i].Value;
                if (!seen.Add(index) && !Equals(args[index], captured))
                {
                    compatible = false;
                    break;
                }
                args[index] = captured;
            }
            if (compatible)
                return new(pattern.Key, args);
        }
        return null;
    }

    private static Pattern BuildPattern(string key, string template, int argumentCount)
    {
        var expression = new StringBuilder(@"\A");
        var literal = new StringBuilder();
        var indices = new List<int>();
        var letters = 0;
        void Flush()
        {
            letters += literal.ToString().Count(char.IsLetter);
            expression.Append(Regex.Escape(literal.ToString()));
            literal.Clear();
        }
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if (c is '{' or '}' && i + 1 < template.Length && template[i + 1] == c)
            {
                literal.Append(c);
                i++;
                continue;
            }
            if (c != '{')
            {
                literal.Append(c);
                continue;
            }
            Flush();
            var start = ++i;
            while (i < template.Length && char.IsAsciiDigit(template[i]))
                i++;
            var index = int.Parse(template.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            expression.Append("(?<a").Append(indices.Count).Append(@">.*?)");
            indices.Add(index);
            while (i < template.Length && template[i] != '}')
                i++;
        }
        Flush();
        expression.Append(@"\z");
        return new(
            key,
            new Regex(
                expression.ToString(),
                RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(50)
            ),
            indices.ToArray(),
            argumentCount,
            letters
        );
    }
}
