using GTAModManager.Core.Game;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

public sealed class ModModeServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-mode-" + Guid.NewGuid().ToString("N"));
    private string GameRoot => Path.Combine(_root, "game");
    private string StateRoot => Path.Combine(_root, "state");
    private readonly GameInstall _game;

    public ModModeServiceTests()
    {
        Directory.CreateDirectory(GameRoot);
        File.WriteAllText(Path.Combine(GameRoot, "GTA5.exe"), "game");
        _game = GameInstall.Open(GameRoot);
        _game.RunningCheckOverride = () => false;
    }

    private string Put(string rel, string value = "mod")
    {
        string full = Path.Combine(GameRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, value);
        return full;
    }

    private ModModeService Service(Action<string>? fault = null) =>
        new(_game, StateRoot) { FaultPoint = fault ?? (_ => { }) };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    [Fact]
    public void Disable_and_enable_rename_loading_binaries_and_leave_mod_content_in_place()
    {
        Put(@"mods\update\update.rpf", "archive-bytes");
        Put("OpenIV.asi");
        Put("OpenIV.asi.gmm-disabled");
        Put("TrainerV.ini");
        Put("xinput1_4.dll", "original");
        Put("unknown.dll");
        Put("ScriptHookV.dll", "hook");
        Put(@"scripts\mod.dll", "script");
        Put(@"plugins\other.dll", "plugin");
        var service = Service();
        Assert.DoesNotContain("mods", service.PrepareDisable().Candidates);
        Assert.Contains("ScriptHookV.dll", service.PrepareDisable().Candidates);
        Assert.Equal(ModModeState.Disabled, service.Disable().State);
        Assert.True(Directory.Exists(Path.Combine(GameRoot, "mods")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "OpenIV.asi.gmm-off")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "ScriptHookV.dll.gmm-off")));
        Assert.Equal("script", File.ReadAllText(Path.Combine(GameRoot, @"scripts\mod.dll")));
        Assert.Equal("plugin", File.ReadAllText(Path.Combine(GameRoot, @"plugins\other.dll")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "TrainerV.ini")));
        Assert.True(service.Inspect().WritesBlocked);
        Assert.Equal("original", File.ReadAllText(Path.Combine(GameRoot, "xinput1_4.dll")));
        Assert.Equal(ModModeState.Modded, service.Enable().State);
        Assert.Equal("archive-bytes", File.ReadAllText(Path.Combine(GameRoot, @"mods\update\update.rpf")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "OpenIV.asi.gmm-disabled")));
    }

    [Theory]
    [InlineData("after-move:0", false)]
    [InlineData("after-move:0", true)]
    public void Interrupted_rename_reconciles_in_either_direction(string point, bool enable)
    {
        Put("OpenIV.asi", "bytes");
        if (enable)
            Service().Disable();
        var crash = Service(p =>
        {
            if (p == point)
                throw new SimulatedCrashException(p);
        });
        Assert.Throws<SimulatedCrashException>(() =>
        {
            if (enable)
                crash.Enable();
            else
                crash.Disable();
        });
        Assert.Equal(ModModeState.Incomplete, Service().Inspect().State);
        if (enable)
            Assert.Equal(ModModeState.Modded, Service().Enable().State);
        else
            Assert.Equal(ModModeState.Disabled, Service().Disable().State);
        Service().Enable();
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(GameRoot, "OpenIV.asi")));
    }

    [Fact]
    public void Running_game_is_rejected_before_state_writes()
    {
        Put("OpenIV.asi");
        _game.RunningCheckOverride = () => true;
        Assert.Throws<InvalidOperationException>(() => Service().Disable());
        Assert.False(Directory.Exists(StateRoot));
        Assert.True(File.Exists(Path.Combine(GameRoot, "OpenIV.asi")));
    }

    [Fact]
    public void Enable_target_conflict_rejects_all_moves_and_preserves_new_file()
    {
        Put("a.asi", "a");
        Put("b.asi", "b");
        Service().Disable();
        Put("b.asi", "new");
        Assert.Throws<IOException>(() => Service().Enable());
        Assert.False(File.Exists(Path.Combine(GameRoot, "a.asi")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(GameRoot, "b.asi")));
    }

    [Fact]
    public void Changed_stored_content_is_not_restored()
    {
        Put("a.asi");
        var report = Service().Disable();
        File.WriteAllText(Path.Combine(GameRoot, "a.asi.gmm-off"), "changed");
        Assert.Throws<IOException>(() => Service().Enable());
        Assert.False(File.Exists(Path.Combine(GameRoot, "a.asi")));
    }

    [Fact]
    public void Junction_in_untouched_mod_directory_is_not_followed_or_moved()
    {
        var external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.rpf"), "outside-game");
        var junction = Path.Combine(GameRoot, "mods");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(external);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"Cannot create junction fixture: {stdout} {stderr}");
        try
        {
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
            Put("a.asi");
            Assert.Equal(ModModeState.Disabled, Service().Disable().State);
            Assert.True(Directory.Exists(junction));
            Assert.True(Directory.Exists(external));
            Assert.Equal("outside-game", File.ReadAllText(Path.Combine(external, "sentinel.rpf")));
        }
        finally
        {
            // Remove only the junction itself; never recurse into its target.
            Directory.Delete(junction, recursive: false);
        }
    }

    [Theory]
    [InlineData("../GTA5.exe")]
    [InlineData("GTA5.exe")]
    [InlineData("launcher-custom.exe")]
    public void Tampered_journal_cannot_move_paths_outside_allowlist(string replacement)
    {
        Put("a.asi");
        var report = Service().Disable();
        var path = Path.Combine(report.StoragePath, "journal.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Entries"]![0]!["RelativePath"] = replacement;
        File.WriteAllText(path, json.ToJsonString());
        Assert.Throws<IOException>(() => Service().Enable());
        Assert.Equal("game", File.ReadAllText(Path.Combine(GameRoot, "GTA5.exe")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "a.asi.gmm-off")));
    }

    [Fact]
    public void Missing_stored_file_is_reported_incomplete_without_hashing()
    {
        Put("a.asi");
        var report = Service().Disable();
        File.Delete(Path.Combine(GameRoot, "a.asi.gmm-off"));
        Assert.Equal(ModModeState.Incomplete, Service().Inspect().State);
        Assert.True(Service().Inspect().WritesBlocked);
    }

    [Fact]
    public void New_mod_after_disable_keeps_writes_blocked_and_is_not_silently_claimed_disabled()
    {
        Put("a.asi");
        Service().Disable();
        Put("new.asi");
        Assert.Equal(ModModeState.Incomplete, Service().Inspect().State);
        Assert.Throws<IOException>(() => Service().Disable());
        Assert.Equal("mod", File.ReadAllText(Path.Combine(GameRoot, "new.asi")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_modded_journal_does_not_require_old_proxy_files(bool replacement)
    {
        Put("old.asi");
        var report = Service().Disable();
        Service().Enable();
        File.Delete(Path.Combine(GameRoot, "old.asi"));
        // Seed the historical entry a previous successful loader cycle would leave.
        // Once fully restored, this entry no longer owns the live loader path.
        var path = Path.Combine(report.StoragePath, "journal.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["Entries"]![0]!["RelativePath"] = "dinput8.dll";
        File.WriteAllText(path, json.ToJsonString());
        if (replacement)
            Put("dinput8.dll", "replacement-runtime");
        Put("new.asi", "new-mod");

        Assert.Equal(ModModeState.Modded, Service().Inspect().State);
        Assert.Equal(ModModeState.Disabled, Service().Disable().State);
        Assert.False(File.Exists(Path.Combine(GameRoot, "new.asi")));
        if (replacement)
            Assert.Equal("replacement-runtime", File.ReadAllText(Path.Combine(GameRoot, "dinput8.dll")));
        Assert.Equal(ModModeState.Modded, Service().Enable().State);
        Assert.Equal("new-mod", File.ReadAllText(Path.Combine(GameRoot, "new.asi")));
    }

    [Fact]
    public void Next_disable_cycle_discovers_current_mods_after_old_mod_removed()
    {
        Put("old.asi");
        Service().Disable();
        Service().Enable();
        File.Delete(Path.Combine(GameRoot, "old.asi"));
        Put("new.asi", "new-mod");
        Assert.Equal(ModModeState.Modded, Service().Inspect().State);
        Assert.Equal(ModModeState.Disabled, Service().Disable().State);
        Assert.Equal(ModModeState.Modded, Service().Enable().State);
        Assert.Equal("new-mod", File.ReadAllText(Path.Combine(GameRoot, "new.asi")));
        Assert.False(File.Exists(Path.Combine(GameRoot, "old.asi")));
    }

    [Fact]
    public void Unsafe_state_root_inside_game_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ModModeService(_game, Path.Combine(GameRoot, "state")));
    }

    [Fact]
    public void Preexisting_disabled_name_is_not_overwritten()
    {
        Put("a.asi", "current");
        Put("a.asi.gmm-off", "other");
        Assert.Throws<IOException>(() => Service().Disable());
        Assert.Equal("current", File.ReadAllText(Path.Combine(GameRoot, "a.asi")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(GameRoot, "a.asi.gmm-off")));
        Assert.False(Directory.Exists(StateRoot));
    }

    [Fact]
    public void Changed_mod_content_while_loaders_disabled_does_not_affect_restore()
    {
        Put("a.asi");
        Put(@"mods\dlc.rpf", "before");
        Service().Disable();
        Put(@"mods\dlc.rpf", "updated-while-disabled");
        Assert.Equal(ModModeState.Modded, Service().Enable().State);
        Assert.Equal("updated-while-disabled", File.ReadAllText(Path.Combine(GameRoot, @"mods\dlc.rpf")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "a.asi")));
    }

    [Fact]
    public void Legacy_moved_directory_snapshot_is_restored_before_next_rename_cycle()
    {
        Put(@"mods\dlc.rpf", "archive");
        Put("a.asi", "plugin");
        var report = Service().Disable();
        var path = Path.Combine(report.StoragePath, "journal.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json.AsObject().Remove("StorageMode"); // Old journals had no strategy field.
        var files = Path.Combine(report.StoragePath, "files");
        Directory.CreateDirectory(files);
        File.Move(Path.Combine(GameRoot, "a.asi.gmm-off"), Path.Combine(files, "a.asi"));
        var modDir = Path.Combine(GameRoot, "mods");
        var legacyFingerprint = DirectoryFingerprint(modDir);
        Directory.Move(modDir, Path.Combine(files, "mods"));
        json["Entries"]!
            .AsArray()
            .Add(
                new System.Text.Json.Nodes.JsonObject
                {
                    ["RelativePath"] = "mods",
                    ["Directory"] = true,
                    ["Hash"] = legacyFingerprint,
                }
            );
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(ModModeState.Disabled, Service().Inspect().State);
        Assert.Equal(ModModeState.Modded, Service().Enable().State);
        Assert.Equal("archive", File.ReadAllText(Path.Combine(GameRoot, @"mods\dlc.rpf")));
        Assert.Equal(ModModeState.Disabled, Service().Disable().State);
        Assert.True(File.Exists(Path.Combine(GameRoot, @"mods\dlc.rpf")));
        Assert.True(File.Exists(Path.Combine(GameRoot, "a.asi.gmm-off")));
    }

    private static string DirectoryFingerprint(string path)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256
        );
        void Text(string text) => hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text + "\0"));
        void Visit(string current, string relative)
        {
            bool directory = Directory.Exists(current);
            Text((directory ? "D:" : "F:") + relative);
            if (directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                    Visit(child, Path.GetRelativePath(path, child));
            else
            {
                var content = File.ReadAllBytes(current);
                Text(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                hash.AppendData(content);
            }
        }
        Visit(path, "");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
