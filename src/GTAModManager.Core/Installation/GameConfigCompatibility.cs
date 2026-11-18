using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Installation;

public sealed record VerifiedConfig(string Edition, string Build, string Sha256, string Source);

/// <summary>Exact file/build allowlist. Empty list intentionally grants no automatic compatibility.</summary>
public static class GameConfigCompatibility
{
    public static IReadOnlyList<VerifiedConfig> Read(string path)
    {
        if (!File.Exists(path))
            return [];
        if (new FileInfo(path).Length > 1 << 20)
            throw new InvalidDataException(L.T("core.installation.compatibilityRegistryTooLarge"));
        return JsonSerializer.Deserialize<List<VerifiedConfig>>(File.ReadAllText(path)) ?? [];
    }

    public static bool Verified(string source, GameEdition edition, string? build, IEnumerable<VerifiedConfig> rules)
    {
        if (string.IsNullOrWhiteSpace(build))
            return false;
        var matches = rules
            .Where(r => r.Edition.Equals(edition.ToString(), StringComparison.OrdinalIgnoreCase) && r.Build == build)
            .ToArray();
        return matches.Length > 0
            && matches.Any(r =>
                r.Sha256.Equals(FileTransactionEngine.Hash(source), StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(r.Source, UriKind.Absolute, out var uri)
                && uri.Scheme == "https"
            );
    }
}
