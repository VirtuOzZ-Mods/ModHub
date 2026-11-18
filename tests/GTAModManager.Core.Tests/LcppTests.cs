using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Library;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Tests;

[Collection("NaturalVision integration")]
public sealed partial class LcppTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("gmm-lcpp-").FullName;
    readonly GameInstall game;
    readonly BackupLibrary library;
    readonly string bundle,
        states;
    const string Update = @"mods\update\update.rpf",
        Pack = @"mods\update\x64\dlcpacks\lcpack\dlc.rpf";
    static readonly string[] Words =
    [
        "ONE",
        "TWO",
        "THREE",
        "FOUR",
        "FIVE",
        "SIX",
        "SEVEN",
        "EIGHT",
        "NINE",
        "TEN",
        "ELEVEN",
        "TWELVE",
    ];

    public LcppTests()
    {
        string target = Path.Combine(root, "game");
        Directory.CreateDirectory(target);
        foreach (string file in new[] { "GTA5.exe", "RageOpenV.asi", "dinput8.dll", "ScriptHookV.dll" })
            File.WriteAllText(Path.Combine(target, file), "MZ test");
        game = GameInstall.Open(target);
        game.RunningCheckOverride = () => false;
        library = BackupLibrary.Create(Path.Combine(root, "library"), 1L << 29);
        states = Path.Combine(root, "states");
        bundle = Directory.CreateDirectory(Path.Combine(root, "bundle")).FullName;
        Put(
            @"update\update.rpf",
            Rpf(
                new()
                {
                    ["same.txt"] = "original"u8.ToArray(),
                    [@"common\data\gameconfig.xml"] = "<config>original</config>"u8.ToArray(),
                }
            )
        );
        File.WriteAllText(Path.Combine(target, "WorldTravel.ini"), "previous settings");
        for (int i = 1; i <= 5; i++)
            Part(i);
    }

    string Name(int i) => $"{i}. Liberty City Installer - Part {Words[i - 1]}.oiv";

    void Put(string path, byte[] bytes)
    {
        string full = Path.Combine(game.Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    static byte[] Rpf(Dictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        var builder = new Rpf7Builder();
        foreach (var file in files)
            builder.AddFile(file.Key, new RpfBinarySource(file.Value));
        builder.Write(stream);
        return stream.ToArray();
    }

    void Oiv(string name, string title, string ops, Dictionary<string, byte[]> files)
    {
        string path = Path.Combine(bundle, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(zip.CreateEntry("assembly.xml").Open()))
            writer.Write(
                $"<package version=\"2.2\" target=\"Five\"><metadata><name>{title}</name></metadata><content>{ops}</content></package>"
            );
        foreach (var file in files)
        {
            using var output = zip.CreateEntry("content/" + file.Key).Open();
            output.Write(file.Value);
        }
    }

    void Part(int i, bool broken = false)
    {
        var files = new Dictionary<string, byte[]> { ["data"] = Encoding.UTF8.GetBytes("part-" + i) };
        string ops =
            $"<archive path=\"update\\update.rpf\" type=\"RPF7\" createIfNotExist=\"True\"><add source=\"{(broken ? "missing" : "data")}\">same.txt</add>";
        if (i == 1)
        {
            files["config"] = "<config>bundled</config>"u8.ToArray();
            ops += "<add source=\"config\">\\common\\data\\gameconfig.xml</add>";
        }
        ops += "</archive>";
        if (i == 1)
            ops += "<add source=\"data\">WorldTravel.ini</add>";
        if (i == 2)
        {
            files["dlc"] = Rpf(new() { ["map.txt"] = "base"u8.ToArray() });
            ops += "<add source=\"dlc\">update\\x64\\dlcpacks\\lcpack\\dlc.rpf</add>";
        }
        if (i >= 3)
            ops +=
                "<archive path=\"update\\x64\\dlcpacks\\lcpack\\dlc.rpf\" type=\"RPF7\" createIfNotExist=\"False\"><add source=\"data\">map.txt</add></archive>";
        Oiv(Name(i), "Liberty City Installer - Part " + Words[i - 1], ops, files);
    }

    NveCatalog Catalog()
    {
        using var source = new NveBundle(bundle);
        return source.Analyze();
    }

    NveService Service() => new(game, library, states);

    NveResult Install(Action<string>? progress = null)
    {
        var catalog = Catalog();
        return Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = catalog.Stamp,
                    Components = catalog.Components.Where(c => c.Core).Select(c => c.Id).ToList(),
                },
                progress
            );
    }

    NveState State(string id) => new NveStore(game, states).Load(id);

    string Read(string outer, string inner) => Encoding.UTF8.GetString(new RpfWorkspace(game).Read(outer, inner));

    // What the game sees: the mods copy when it exists, otherwise the original archive.
    string ReadEffective(string inner) =>
        Read(File.Exists(Path.Combine(game.Root, Update)) ? Update : @"update\update.rpf", inner);

    Dictionary<string, string> Snapshot() =>
        Directory
            .EnumerateFiles(game.Root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains("\\ModHub\\", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => Path.GetRelativePath(game.Root, p), FileTransactionEngine.Hash);

    void Same(Dictionary<string, string> before)
    {
        var after = Snapshot();
        Assert.Equal(before.OrderBy(p => p.Key), after.OrderBy(p => p.Key));
    }

    void Edit(string outer, string inner, string value)
    {
        string source = Path.Combine(root, "edit.txt");
        File.WriteAllText(source, value);
        using var plan = new RpfWorkspace(game).PrepareEdit(new(outer, inner, source));
        Assert.True(plan.CanInstall, string.Join(";", plan.Errors));
        Assert.Equal(TxState.Completed, new OivInstaller(game).Install(plan, library).State);
    }

    [Fact]
    public void Archive_is_recognized_as_five_required_parts_in_order()
    {
        string archive = Path.Combine(root, "renamed.zip");
        ZipFile.CreateFromDirectory(bundle, archive);
        Assert.True(NveBundle.IsCandidate(archive));
        using var source = new NveBundle(archive);
        var catalog = source.Analyze();
        Assert.Equal("Liberty City Preservation Project", catalog.Title);
        Assert.Equal(Enumerable.Range(1, 5).Select(i => "core-" + i), catalog.Components.Select(c => c.Id));
        Assert.All(catalog.Components, c => Assert.True(c.Core));
        Assert.NotEmpty(catalog.Validate(["core-1", "core-2", "core-3", "core-4"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shared_rpf_changes_install_and_remove_as_one_mod_without_source(bool existingOverlay)
    {
        if (existingOverlay)
            Put(Update, File.ReadAllBytes(Path.Combine(game.Root, @"update\update.rpf")));
        var before = Snapshot();
        var result = Install();
        Assert.True(result.Success, result.Message);
        Assert.Equal("part-5", Read(Update, "same.txt"));
        Assert.Equal("part-5", Read(Pack, "map.txt"));
        Assert.Equal("<config>original</config>", Read(Update, @"common\data\gameconfig.xml"));
        var state = State(result.InstallationId);
        Assert.Equal("Liberty City Preservation Project", state.ProductTitle);
        Assert.Equal(5, state.Components.Count(c => c.Active));
        Assert.Single(new NveStore(game, states).Active());
        Directory.Delete(bundle, true);
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Theory]
    [InlineData("loose")]
    [InlineData("rpf")]
    public void Later_edits_of_lcpp_files_do_not_block_removal(string change)
    {
        var result = Install();
        Assert.True(result.Success, result.Message);
        if (change == "loose")
            File.WriteAllText(Path.Combine(game.Root, "WorldTravel.ini"), "foreign");
        else
            Edit(Update, "same.txt", "foreign");
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.All(State(result.InstallationId).Components, c => Assert.False(c.Active));
        if (change == "loose")
            Assert.NotEqual("foreign", File.ReadAllText(Path.Combine(game.Root, "WorldTravel.ini")));
        else
            Assert.Equal("original", ReadEffective("same.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_corrupt_earliest_backup_stops_removal_before_any_undo(bool corrupt)
    {
        var result = Install();
        Assert.True(result.Success, result.Message);
        var record = library.LoadRecord("Legacy", State(result.InstallationId).Components[0].TransactionId!)!;
        string hash = record.Files.Single(f => f.Rel == "WorldTravel.ini").OldBlob!;
        if (corrupt)
            BackupPackHelpers.CorruptBlob(library, hash);
        else
            BackupPackHelpers.DropBlob(library, hash);
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => Service().Remove(new() { InstallationId = result.InstallationId }));
        Same(before);
        Assert.All(State(result.InstallationId).Components, c => Assert.True(c.Active));
    }

    [Fact]
    public void Failed_last_part_restores_all_completed_parts_including_shared_rpfs()
    {
        Part(5, broken: true);
        var before = Snapshot();
        Assert.Throws<OivFormatException>(() => Install());
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Exception_after_four_parts_rolls_back_the_logical_installation()
    {
        var before = Snapshot();
        var result = Install(message =>
        {
            if (
                message.Contains("FIVE")
                && new NveStore(game, states).Active().Any(s => s.Components.Count(c => c.Active) == 4)
            )
                throw new IOException("injected failure");
        });
        Assert.False(result.Success);
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    public void Invalid_part_set_is_rejected_before_install(string kind)
    {
        if (kind == "missing")
            File.Delete(Path.Combine(bundle, Name(5)));
        if (kind == "duplicate")
            File.Copy(Path.Combine(bundle, Name(1)), Path.Combine(bundle, "Liberty City Installer - Part ONE.oiv"));
        if (kind == "unknown")
            Oiv("Surprise.oiv", "Surprise", "<delete>WorldTravel.ini</delete>", []);
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => Catalog());
        Same(before);
    }

    [Fact]
    public void Insufficient_backup_capacity_rejects_before_first_game_change()
    {
        library.SetQuota(1);
        var before = Snapshot();
        Assert.Throws<IOException>(() => Install());
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Explicit_addons_are_optional_and_enhanced_is_rejected()
    {
        Oiv(
            @"Optional Add-Ons\1. Extra\INSTALL Extra.oiv",
            "Extra",
            "<add source=\"x\">extra.txt</add>",
            new() { ["x"] = [1] }
        );
        var c = Catalog();
        Assert.Single(c.Components.Where(p => !p.Core));
        File.Delete(Path.Combine(game.Root, "GTA5.exe"));
        File.WriteAllText(Path.Combine(game.Root, "GTA5_Enhanced.exe"), "stub");
        var enhanced = GameInstall.Open(game.Root);
        enhanced.RunningCheckOverride = () => false;
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() =>
            new NveService(enhanced, library, states).Install(
                new()
                {
                    Source = bundle,
                    Stamp = c.Stamp,
                    Components = c.Components.Where(p => p.Core).Select(p => p.Id).ToList(),
                }
            )
        );
        Same(before);
    }

    [Fact]
    public void Lcpp_and_nve_have_independent_installations_and_removals()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "nve")).FullName;
        for (int i = 1; i <= 3; i++)
            Oiv(
                Path.Combine(source, $"{i}. NaturalVision Installer - Part {Words[i - 1]}.oiv"),
                "NaturalVision Evolved - Part " + Words[i - 1],
                $"<add source=\"x\">nve{i}.txt</add>",
                new() { ["x"] = [(byte)i] }
            );
        NveCatalog nve;
        using (var b = new NveBundle(source))
            nve = b.Analyze();
        var first = Service()
            .Install(
                new()
                {
                    Source = source,
                    Stamp = nve.Stamp,
                    Components = nve.Components.Select(c => c.Id).ToList(),
                }
            );
        Assert.True(first.Success, first.Message);
        var second = Install();
        Assert.True(second.Success, second.Message);
        Assert.Equal(2, new NveStore(game, states).Active().Count);
        Assert.True(Service().Remove(new() { InstallationId = second.InstallationId }).Success);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(game.Root, "nve1.txt")));
        Assert.Single(new NveStore(game, states).Active());
        Assert.True(Service().Remove(new() { InstallationId = first.InstallationId }).Success);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Optional_addon_can_be_removed_then_entire_lcpp_without_source()
    {
        Oiv(
            @"Optional Add-Ons\1. Extra\INSTALL Extra.oiv",
            "Extra",
            "<archive path=\"update\\update.rpf\" type=\"RPF7\" createIfNotExist=\"False\"><add source=\"x\">same.txt</add></archive>",
            new() { ["x"] = "optional"u8.ToArray() }
        );
        var before = Snapshot();
        var catalog = Catalog();
        var result = Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = catalog.Stamp,
                    Components = catalog.Components.Select(c => c.Id).ToList(),
                }
            );
        Assert.True(result.Success, result.Message);
        string option = catalog.Components.Single(c => !c.Core).Id;
        Assert.Equal("optional", Read(Update, "same.txt"));
        Directory.Delete(bundle, true);
        var removed = Service().Remove(new() { InstallationId = result.InstallationId, Components = [option] });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal("part-5", Read(Update, "same.txt"));
        removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Same(before);
    }

    [Fact]
    public void Another_mod_editing_a_shared_rpf_after_lcpp_is_never_overwritten()
    {
        var installed = Install();
        Assert.True(installed.Success, installed.Message);
        string source = Directory.CreateDirectory(Path.Combine(root, "nve")).FullName;
        for (int i = 1; i <= 3; i++)
            Oiv(
                Path.Combine(source, $"{i}. NaturalVision Installer - Part {Words[i - 1]}.oiv"),
                "NaturalVision Evolved - Part " + Words[i - 1],
                "<archive path=\"update\\update.rpf\" type=\"RPF7\" createIfNotExist=\"False\"><add source=\"x\">nve.txt</add></archive>",
                new() { ["x"] = [(byte)i] }
            );
        NveCatalog nve;
        using (var b = new NveBundle(source))
            nve = b.Analyze();
        Assert.True(
            Service()
                .Install(
                    new()
                    {
                        Source = source,
                        Stamp = nve.Stamp,
                        Components = nve.Components.Select(c => c.Id).ToList(),
                    }
                )
                .Success
        );
        // Removing LCPP keeps NaturalVision's own entry in the shared update.rpf, and NaturalVision still removes cleanly.
        var removed = Service().Remove(new() { InstallationId = installed.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal([(byte)3], new RpfWorkspace(game).Read(Update, "nve.txt"));
        Assert.Equal("original", Read(Update, "same.txt"));
        var nveState = new NveStore(game, states).Active().Single();
        var nveRemoved = Service().Remove(new() { InstallationId = nveState.Id });
        Assert.True(nveRemoved.Success, nveRemoved.Message);
        Assert.DoesNotContain(new RpfWorkspace(game).List(Update), e =>
            e.Path.Equals("nve.txt", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void Interrupted_removal_can_be_retried_after_one_shared_rpf_undo()
    {
        var before = Snapshot();
        var result = Install();
        Assert.True(result.Success, result.Message);
        bool interrupted = false;
        var removal = Service()
            .Remove(new() { InstallationId = result.InstallationId }, _ =>
            {
                if (!interrupted && State(result.InstallationId).Components.Count(c => c.Active) == 4)
                {
                    interrupted = true;
                    throw new IOException("interrupted removal");
                }
            });
        Assert.True(interrupted);
        Assert.False(removal.Success);
        Assert.Equal(4, State(result.InstallationId).Components.Count(c => c.Active));
        removal = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removal.Success, removal.Message);
        Same(before);
    }

    [Fact]
    public void Exhausted_game_volume_is_rejected_before_expanding_declared_large_payload()
    {
        const uint declared = 2_147_483_000;
        long free = new DriveInfo(Path.GetPathRoot(game.Root)!).AvailableFreeSpace;
        int copies = checked((int)(free / declared) + 32);
        string ops = string.Concat(
            Enumerable.Range(1, copies).Select(i => $"<add source=\"large\">large-{i}.bin</add>")
        );
        Oiv(Name(5), "Liberty City Installer - Part FIVE", ops, new() { ["large"] = [1] });
        // A ZIP central directory declares lengths without allocating those bytes. No payload read is allowed.
        string file = Path.Combine(bundle, Name(5));
        byte[] bytes = File.ReadAllBytes(file);
        bool patched = false;
        for (int i = 0; i < bytes.Length - 46; i++)
            if (BitConverter.ToUInt32(bytes, i) == 0x02014b50)
            {
                int nameLength = BitConverter.ToUInt16(bytes, i + 28);
                if (Encoding.UTF8.GetString(bytes, i + 46, nameLength) != "content/large")
                    continue;
                BitConverter.GetBytes(declared).CopyTo(bytes, i + 24);
                patched = true;
                break;
            }
        Assert.True(patched);
        File.WriteAllBytes(file, bytes);
        var before = Snapshot();
        Assert.Throws<IOException>(() => Install());
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Fact]
    public void Failed_install_checks_all_backups_before_rolling_back_any_completed_part()
    {
        Dictionary<string, string>? atFailure = null;
        var result = Install(message =>
        {
            var state = new NveStore(game, states).Active().FirstOrDefault();
            if (atFailure is not null || !message.Contains("FIVE") || state?.Components.Count(c => c.Active) != 4)
                return;
            var record = library.LoadRecord("Legacy", state.Components[0].TransactionId!)!;
            string blob = record.Files.Single(f => f.Rel == "WorldTravel.ini").OldBlob!;
            BackupPackHelpers.DropBlob(library, blob);
            atFailure = Snapshot();
            throw new IOException("injected failure with missing earlier backup");
        });
        Assert.NotNull(atFailure);
        Assert.False(result.Success);
        Assert.Equal("NeedsAttention", State(result.InstallationId).Phase);
        Assert.Equal(4, State(result.InstallationId).Components.Count(c => c.Active));
        Same(atFailure);
    }

    void MakeHd()
    {
        for (int i = 1; i <= 12; i++)
        {
            if (i > 5)
                Part(i);
            string path = Path.Combine(bundle, Name(i));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("assembly.xml")!;
                string text;
                using (var reader = new StreamReader(entry.Open()))
                    text = reader.ReadToEnd();
                entry.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("assembly.xml").Open());
                writer.Write(text.Replace("Liberty City Installer - Part ", "Liberty City Installer - HD Part "));
            }
            File.Move(path, Path.Combine(bundle, HdName(i)));
        }
    }

    string HdName(int i) => $"{i}. Liberty City Installer - HD Part {Words[i - 1]}.oiv";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hd_folder_and_archive_offer_twelve_required_parts_in_numeric_order(bool archive)
    {
        MakeHd();
        string source = bundle;
        if (archive)
        {
            source = Path.Combine(root, "renamed-hd.zip");
            ZipFile.CreateFromDirectory(bundle, source);
        }
        Assert.True(NveBundle.IsCandidate(source));
        using var b = new NveBundle(source);
        var catalog = b.Analyze();
        Assert.Equal("Liberty City Preservation Project (HD Textures)", catalog.Title);
        Assert.Equal(Enumerable.Range(1, 12).Select(i => "core-" + i), catalog.Components.Select(c => c.Id));
        Assert.All(catalog.Components, c => Assert.True(c.Core));
        Assert.NotEmpty(catalog.Validate(Enumerable.Range(1, 11).Select(i => "core-" + i)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hd_twelve_parts_install_and_remove_without_source_and_preserve_gameconfig(bool archive)
    {
        MakeHd();
        var before = Snapshot();
        string source = bundle;
        if (archive)
        {
            source = Path.Combine(root, "hd.zip");
            ZipFile.CreateFromDirectory(bundle, source);
        }
        NveCatalog catalog;
        using (var b = new NveBundle(source))
            catalog = b.Analyze();
        var result = Service()
            .Install(
                new()
                {
                    Source = source,
                    Stamp = catalog.Stamp,
                    Components = catalog.Components.Select(c => c.Id).ToList(),
                }
            );
        Assert.True(result.Success, result.Message);
        Assert.Equal("part-12", Read(Update, "same.txt"));
        Assert.Equal("part-12", Read(Pack, "map.txt"));
        Assert.Equal("<config>original</config>", Read(Update, @"common\data\gameconfig.xml"));
        Assert.Equal("Liberty City Preservation Project (HD Textures)", State(result.InstallationId).ProductTitle);
        Assert.Equal(12, State(result.InstallationId).Components.Count(c => c.Active));
        Assert.Single(new NveStore(game, states).Active());
        if (archive)
            File.Delete(source);
        Directory.Delete(bundle, true);
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Same(before);
    }

    [Fact]
    public void Hd_failure_before_twelfth_part_rolls_back_eleven_shared_rpf_changes()
    {
        MakeHd();
        var before = Snapshot();
        var result = Install(message =>
        {
            if (
                message.Contains("TWELVE")
                && new NveStore(game, states).Active().Any(s => s.Components.Count(c => c.Active) == 11)
            )
                throw new IOException("injected HD failure");
        });
        Assert.False(result.Success);
        Same(before);
        Assert.Empty(new NveStore(game, states).Active());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("mixed")]
    [InlineData("metadata")]
    public void Hd_incomplete_duplicate_mixed_or_mislabeled_parts_are_rejected(string kind)
    {
        MakeHd();
        if (kind == "missing")
            File.Delete(Path.Combine(bundle, HdName(12)));
        if (kind == "duplicate")
            File.Copy(
                Path.Combine(bundle, HdName(1)),
                Path.Combine(bundle, "Liberty City Installer - HD Part ONE.oiv")
            );
        if (kind == "mixed")
            Part(1);
        if (kind == "metadata")
            Oiv(
                HdName(12),
                "Liberty City Installer - Part TWELVE",
                "<add source=\"x\">wrong.txt</add>",
                new() { ["x"] = [1] }
            );
        var before = Snapshot();
        Assert.Throws<InvalidDataException>(() => Catalog());
        Same(before);
    }

    [Fact]
    public void Hd_removal_undoes_all_twelve_parts_despite_a_later_edit()
    {
        MakeHd();
        var result = Install();
        Assert.True(result.Success, result.Message);
        Edit(Update, "same.txt", "foreign mod");
        var removed = Service().Remove(new() { InstallationId = result.InstallationId });
        Assert.True(removed.Success, removed.Message);
        Assert.Equal(0, State(result.InstallationId).Components.Count(c => c.Active));
        Assert.Equal("original", ReadEffective("same.txt"));
    }

    [Fact]
    public void Standard_and_hd_cannot_be_installed_over_each_other()
    {
        var standard = Install();
        Assert.True(standard.Success, standard.Message);
        MakeHd();
        var before = Snapshot();
        Assert.Throws<InvalidOperationException>(() => Install());
        Same(before);
        Assert.Single(new NveStore(game, states).Active());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Package_progress_counts_only_finished_parts_and_reports_verification(bool hd)
    {
        if (hd)
            MakeHd();
        int total = hd ? 12 : 5;
        var updates = new List<NveProgress>();
        var catalog = Catalog();
        var result = Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = catalog.Stamp,
                    Components = catalog.Components.Select(c => c.Id).ToList(),
                },
                componentProgress: updates.Add
            );
        Assert.True(result.Success, result.Message);
        Assert.All(updates, p => Assert.Equal(total, p.Total));
        Assert.All(
            updates
                .TakeWhile(p => p.Phase != NveProgressPhase.Installing)
                .Where(p => p.Phase == NveProgressPhase.Preparing),
            p => Assert.Equal(0, p.Completed)
        );
        Assert.Equal(
            Enumerable.Range(1, total),
            updates.Where(p => p.Phase == NveProgressPhase.Installed).Select(p => p.Completed)
        );
        for (int i = 1; i <= total; i++)
        {
            Assert.Contains(updates, p =>
                p.Phase == NveProgressPhase.Installing && p.Index == i && p.Completed == i - 1
            );
            Assert.Contains(updates, p =>
                p.Phase == NveProgressPhase.Verifying && p.Index == i && p.Completed == i - 1
            );
        }
    }

    [Fact]
    public void Failed_install_does_not_report_failed_part_as_finished_and_reports_rollback()
    {
        var updates = new List<NveProgress>();
        var before = Snapshot();
        var catalog = Catalog();
        var result = Service()
            .Install(
                new()
                {
                    Source = bundle,
                    Stamp = catalog.Stamp,
                    Components = catalog.Components.Select(c => c.Id).ToList(),
                },
                message =>
                {
                    if (
                        message.Contains("FIVE")
                        && new NveStore(game, states).Active().Any(s => s.Components.Count(c => c.Active) == 4)
                    )
                        throw new IOException("injected failure");
                },
                updates.Add
            );
        Assert.False(result.Success);
        Assert.Equal(
            new[] { 1, 2, 3, 4 },
            updates.Where(p => p.Phase == NveProgressPhase.Installed).Select(p => p.Completed)
        );
        Assert.Contains(updates, p => p.Phase == NveProgressPhase.RollingBack && p.Completed == 0);
        Same(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vanilla_restore_clears_composed_installation_and_history(bool hd)
    {
        if (hd)
            MakeHd();
        var installed = Install();
        Assert.True(installed.Success, installed.Message);
        var result = VanillaRestore.Execute(game, VanillaRestore.Inspect(game), library, states);
        try
        {
            Assert.Empty(result.Failed);
            Assert.Empty(new NveStore(game, states).Active());
            Assert.Equal("Removed", State(installed.InstallationId).Phase);
            Assert.All(State(installed.InstallationId).Components, c => Assert.False(c.Active));
            Assert.Empty(game.CreateEngine().History().Where(j => j.TargetRoot == game.Root));
            Assert.Empty(library.Records());
            Assert.True(File.Exists(Path.Combine(game.Root, "GTA5.exe")));
            Assert.True(VanillaRestore.Inspect(game).Empty);
            Assert.True(Service().Remove(new() { InstallationId = installed.InstallationId }).Success);
        }
        finally
        {
            if (result.Folder.Length > 0)
                VanillaRestore.DeleteQuarantine(game, result.Folder);
        }
    }

    [Fact]
    public void Reconciliation_repairs_old_phantom_state_without_undo_or_backup()
    {
        var installed = Install();
        Assert.True(installed.Success, installed.Message);
        var stale = State(installed.InstallationId);
        var result = VanillaRestore.Execute(game, VanillaRestore.Inspect(game), library, states);
        try
        {
            // Simulate the old version keeping Installed metadata after all files were removed.
            new NveStore(game, states).Save(stale);
            Assert.Single(new NveStore(game, states).Active());
            Assert.True(VanillaModCleanup.TryComplete(game, library, states));
            Assert.Empty(new NveStore(game, states).Active());
            Assert.True(Service().Remove(new() { InstallationId = installed.InstallationId }).Success);
        }
        finally
        {
            if (result.Folder.Length > 0)
                VanillaRestore.DeleteQuarantine(game, result.Folder);
        }
    }

    [Fact]
    public void Incomplete_vanilla_restore_preserves_suite_and_history()
    {
        var installed = Install();
        Assert.True(installed.Success, installed.Message);
        VanillaResult result;
        using (
            var locked = new FileStream(Path.Combine(game.Root, Pack), FileMode.Open, FileAccess.Read, FileShare.None)
        )
            result = VanillaRestore.Execute(game, VanillaRestore.Inspect(game), library, states);
        try
        {
            Assert.NotEmpty(result.Failed);
            Assert.Equal("NeedsAttention", State(installed.InstallationId).Phase);
            Assert.Single(new NveStore(game, states).Active());
            Assert.Equal(5, game.CreateEngine().History().Count(j => j.TargetRoot == game.Root));
            Assert.Equal(5, library.Records().Count);
            Assert.False(VanillaModCleanup.TryComplete(game, library, states));
        }
        finally
        {
            VanillaRestore.DeleteQuarantine(game, result.Folder);
        }
    }

    public void Dispose()
    {
        foreach (
            var journal in game.CreateEngine()
                .History()
                .Where(j => j.TargetRoot.Equals(game.Root, StringComparison.OrdinalIgnoreCase))
        )
            File.Delete(Path.Combine(game.WorkDir, "journals", journal.Id + ".json"));
        Directory.Delete(root, true);
    }
}
