using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.NaturalVision;

public enum NveProgressPhase
{
    Preparing,
    Installing,
    Verifying,
    Installed,
    Removing,
    RollingBack,
}

public sealed record NveProgress(NveProgressPhase Phase, string Component, int Index, int Total, int Completed)
{
    public const string Prefix = "GMM_COMPONENT_PROGRESS:";

    [JsonIgnore]
    public string ActivityText => L.T("suite.progress." + Phase.ToString().ToLowerInvariant(), Index, Total, Component);

    [JsonIgnore]
    public string CounterText =>
        L.T(
            Phase is NveProgressPhase.Removing or NveProgressPhase.RollingBack
                ? "suite.progress.remaining"
                : "suite.progress.installedCount",
            Completed,
            Total
        );

    public string ToLine() => Prefix + JsonSerializer.Serialize(this);

    public static bool TryParse(string line, [NotNullWhen(true)] out NveProgress? progress)
    {
        progress = null;
        if (!line.StartsWith(Prefix, StringComparison.Ordinal) || line.Length > 16384)
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<NveProgress>(line[Prefix.Length..]);
            if (
                parsed is null
                || !Enum.IsDefined(parsed.Phase)
                || parsed.Component is not { Length: > 0 and <= 1024 }
                || parsed.Total < 1
                || parsed.Index < 1
                || parsed.Index > parsed.Total
                || parsed.Completed < 0
                || parsed.Completed > parsed.Total
            )
                return false;
            progress = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
