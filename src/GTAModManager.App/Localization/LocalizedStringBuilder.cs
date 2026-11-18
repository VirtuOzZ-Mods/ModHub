using System.Globalization;
using System.Text;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Localization;

/// <summary>Display-only builder retaining resource descriptors; never used to build files or commands.</summary>
internal sealed class LocalizedStringBuilder
{
    private readonly List<string?> _parts = [];

    public LocalizedStringBuilder Append(string? text)
    {
        _parts.Add(text);
        return this;
    }

    public LocalizedStringBuilder AppendLine(string? text = null)
    {
        _parts.Add(text);
        _parts.Add(Environment.NewLine);
        return this;
    }

    public override string ToString() => L.Join("", _parts);
}
