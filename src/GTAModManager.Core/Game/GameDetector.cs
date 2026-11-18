using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GTAModManager.Core.Game;

public sealed record DetectedGame(GameEdition Edition, string Root, string Source);

/// <summary>Finds GTA V installations: Rockstar Launcher, Steam, Epic. Reads the registry and manifests only.</summary>
public static partial class GameDetector
{
    public static List<DetectedGame> Detect()
    {
        var found = new List<DetectedGame>();
        void Try(string? root, string source)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;
            try
            {
                var g = GameInstall.Open(root);
                if (!found.Any(f => string.Equals(f.Root, g.Root, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new(g.Edition, g.Root, source));
            }
            catch (DirectoryNotFoundException) { }
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var hive in new[] { @"SOFTWARE\WOW6432Node\Rockstar Games", @"SOFTWARE\Rockstar Games" })
            {
                using var key = Registry.LocalMachine.OpenSubKey(hive);
                if (key is null)
                    continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    using var sub = key.OpenSubKey(name);
                    Try(sub?.GetValue("InstallFolder") as string, "Rockstar Launcher");
                }
            }

            var steam = (
                Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string
            )?.Replace('/', '\\') ?? @"C:\Program Files (x86)\Steam";
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            var libs = new List<string> { steam };
            if (File.Exists(vdf))
                libs.AddRange(
                    VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
                );
            foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var appId in new[] { 271590, 3240220 })
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf))
                    continue;
                var m = VdfInstallDir().Match(File.ReadAllText(acf));
                if (m.Success)
                    Try(Path.Combine(lib, "steamapps", "common", m.Groups[1].Value), "Steam");
            }
        }

        var epic = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (Directory.Exists(epic))
            foreach (var item in Directory.EnumerateFiles(epic, "*.item"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(item));
                    if (
                        doc.RootElement.TryGetProperty("DisplayName", out var dn)
                        && dn.GetString()?.Contains("Grand Theft Auto V") == true
                        && doc.RootElement.TryGetProperty("InstallLocation", out var loc)
                    )
                        Try(loc.GetString(), "Epic Games");
                }
                catch (JsonException) { }
            }

        return found;
    }

    [GeneratedRegex("\"path\"\\s+\"(.+?)\"")]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"installdir\"\\s+\"(.+?)\"")]
    private static partial Regex VdfInstallDir();
}
