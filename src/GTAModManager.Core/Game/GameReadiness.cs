using System.Text.RegularExpressions;

namespace GTAModManager.Core.Game;

public enum BattlEyeState
{
    Unknown,
    Enabled,
    Disabled,
}

/// <summary>
/// First-run safety: whether the game has been launched once and whether BattlEye is off. Only reads Rockstar files
/// in Documents; never writes or launches anything.
/// </summary>
public static partial class GameReadiness
{
    public static string RockstarDocuments =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Rockstar Games");

    /// <summary>Game profile folder in Documents: settings.xml appears after the first launch and graphics setup.</summary>
    public static string ProfileDir(GameEdition edition, string? documents = null) =>
        Path.Combine(documents ?? RockstarDocuments, edition == GameEdition.Enhanced ? "GTAV Enhanced" : "GTA V");

    public static bool FirstLaunchDone(GameEdition edition, string? documents = null) =>
        File.Exists(Path.Combine(ProfileDir(edition, documents), "settings.xml"));

    [GeneratedRegex(@"\[gamelaunch\]\s+Launch Path:\s*(?<path>.+?\.exe)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LaunchPathRx();

    /// <summary>
    /// BattlEye state from the Rockstar Games Launcher logs (launcher.log, launcher.01–09.log): the last launch
    /// of this exact game folder. GTA5_BE.exe / GTA5_Enhanced_BE.exe means BattlEye on, GTA5.exe / GTA5_Enhanced.exe means off.
    /// No launch record means unknown (the game needs to be launched with BattlEye off).
    /// </summary>
    public static BattlEyeState BattlEye(string gameRoot, string? documents = null)
    {
        var dir = Path.Combine(documents ?? RockstarDocuments, "Launcher");
        if (!Directory.Exists(dir))
            return BattlEyeState.Unknown;
        IEnumerable<string> logs;
        try
        {
            logs = new DirectoryInfo(dir)
                .EnumerateFiles("launcher*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return BattlEyeState.Unknown;
        }
        foreach (var log in logs)
        {
            var state = FromLog(ReadShared(log), gameRoot);
            if (state != BattlEyeState.Unknown)
                return state;
        }
        return BattlEyeState.Unknown;
    }

    /// <summary>The last launch record for this game folder in the log text.</summary>
    public static BattlEyeState FromLog(IEnumerable<string> lines, string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot).TrimEnd('\\');
        var result = BattlEyeState.Unknown;
        foreach (var line in lines)
        {
            var m = LaunchPathRx().Match(line);
            if (!m.Success)
                continue;
            var path = m.Groups["path"].Value.Trim().Trim('"');
            string? dir;
            try
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (dir is null || !dir.Equals(root, StringComparison.OrdinalIgnoreCase))
                continue;
            var exe = Path.GetFileNameWithoutExtension(path);
            if (exe.EndsWith("_BE", StringComparison.OrdinalIgnoreCase))
                result = BattlEyeState.Enabled;
            else if (
                exe.Equals("GTA5", StringComparison.OrdinalIgnoreCase)
                || exe.Equals("GTA5_Enhanced", StringComparison.OrdinalIgnoreCase)
            )
                result = BattlEyeState.Disabled;
        }
        return result;
    }

    [GeneratedRegex(@"^\[(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?:\.\d+)?\]")]
    private static partial Regex StampRx();

    /// <summary>Time of the last launch of this game folder through the official launcher (UTC), or null.</summary>
    public static DateTime? LastLaunchUtc(string gameRoot, string? documents = null)
    {
        var dir = Path.Combine(documents ?? RockstarDocuments, "Launcher");
        if (!Directory.Exists(dir))
            return null;
        DateTime? best = null;
        try
        {
            foreach (var log in new DirectoryInfo(dir).EnumerateFiles("launcher*.log"))
                if (LastLaunchFromLog(ReadShared(log.FullName), gameRoot) is { } t && (best is null || t > best))
                    best = t;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return best;
    }

    public static DateTime? LastLaunchFromLog(IEnumerable<string> lines, string gameRoot)
    {
        var root = Path.GetFullPath(gameRoot).TrimEnd('\\');
        DateTime? best = null;
        foreach (var line in lines)
        {
            var m = LaunchPathRx().Match(line);
            if (!m.Success)
                continue;
            string? dir;
            try
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(m.Groups["path"].Value.Trim().Trim('"')))?.TrimEnd('\\');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (dir is null || !dir.Equals(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (
                StampRx().Match(line) is { Success: true } s
                && DateTime.TryParseExact(
                    s.Groups["t"].Value,
                    "yyyy-MM-dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal,
                    out var local
                )
            )
            {
                var utc = local.ToUniversalTime();
                if (best is null || utc > best)
                    best = utc;
            }
        }
        return best;
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16L << 20)
                return [];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
