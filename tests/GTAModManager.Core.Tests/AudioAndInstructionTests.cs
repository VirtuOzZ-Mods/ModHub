using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GTAModManager.Core.Audio;
using GTAModManager.Core.Game;
using GTAModManager.Core.Instructions;
using GTAModManager.Core.Mods;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;

namespace GTAModManager.Core.Tests;

/// <summary>Audio banks (.awc), .wav reading, variants inside archives and readme instruction parsing.</summary>
public sealed class AudioAndInstructionTests : IDisposable
{
    private static readonly uint[] Key = [0x11223344, 0x55667788, 0x99AABBCC, 0xDDEEFF00];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gmm-audio-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    // ---------- .awc ----------

    /// <summary>A bank in the game layout: a header with chunk indices, service chunks in a row, data aligned to 16.</summary>
    private static byte[] MakeBank(params (string Name, short[] Pcm, int Rate)[] sounds)
    {
        var chunks = new List<(int Stream, byte Type, byte[] Data)>();
        for (int s = 0; s < sounds.Length; s++)
        {
            var fmt = new byte[24];
            BinaryPrimitives.WriteUInt32LittleEndian(fmt, (uint)sounds[s].Pcm.Length);
            BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), -1);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(8), (ushort)sounds[s].Rate);
            BinaryPrimitives.WriteInt16LittleEndian(fmt.AsSpan(10), -191);
            var pcm = new byte[sounds[s].Pcm.Length * 2];
            for (int i = 0; i < sounds[s].Pcm.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), sounds[s].Pcm[i]);
            chunks.Add((s, AwcBank.FormatChunk, fmt));
            chunks.Add((s, AwcBank.DataChunk, pcm));
        }
        int tables = 16 + sounds.Length * 2 + sounds.Length * 4 + chunks.Count * 8;
        long pos = tables;
        var offsets = new int[chunks.Count];
        // Service chunks first, then data, as in the game's banks.
        foreach (
            var i in Enumerable
                .Range(0, chunks.Count)
                .OrderBy(i => chunks[i].Type == AwcBank.DataChunk ? 1 : 0)
                .ThenBy(i => i)
        )
        {
            if (chunks[i].Type == AwcBank.DataChunk)
                pos = (pos + 15) / 16 * 16;
            offsets[i] = (int)pos;
            pos += chunks[i].Data.Length;
        }
        var file = new byte[pos];
        BinaryPrimitives.WriteUInt32LittleEndian(file, AwcBank.MagicLe);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 0xFF09);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), sounds.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), tables);
        int w = 16;
        for (int s = 0; s < sounds.Length; s++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(w), (ushort)(s * 2));
            w += 2;
        }
        for (int s = 0; s < sounds.Length; s++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(w), (2u << 29) | AwcBank.StreamId(sounds[s].Name));
            w += 4;
        }
        for (int i = 0; i < chunks.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                file.AsSpan(w),
                ((ulong)chunks[i].Type << 56) | ((ulong)(uint)chunks[i].Data.Length << 28) | (uint)offsets[i]
            );
            w += 8;
            chunks[i].Data.CopyTo(file, offsets[i]);
        }
        return file;
    }

    private static short[] Tone(int n, short amplitude) =>
        Enumerable.Range(0, n).Select(i => (short)(i % 2 == 0 ? amplitude : -amplitude)).ToArray();

    private static byte[] Wav(short[] pcm, int rate, int channels = 1)
    {
        var data = new byte[pcm.Length * 2 * channels];
        for (int i = 0; i < pcm.Length; i++)
        for (int c = 0; c < channels; c++)
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan((i * channels + c) * 2), pcm[i]);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * 2 * channels);
        w.Write((short)(2 * channels));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    [Fact]
    public void Awc_EncryptDecrypt_AreInverse()
    {
        var data = Enumerable.Range(0, 400).Select(i => (byte)(i * 7)).ToArray();
        var copy = (byte[])data.Clone();
        AwcBank.Encrypt(copy, Key);
        Assert.False(copy.AsSpan().SequenceEqual(data));
        AwcBank.Decrypt(copy, Key);
        Assert.Equal(data, copy);
    }

    [Fact]
    public void Awc_ParseAndBuild_WithoutChanges_IsByteIdentical_PlainAndEncrypted()
    {
        var plain = MakeBank(("DUMP_VALVE", Tone(5000, 1000), 24000), ("0x0103B3B2", Tone(30, 200), 22000));
        Assert.Equal(plain, AwcBank.Parse(plain, null).Build(null));
        var encrypted = (byte[])plain.Clone();
        AwcBank.Encrypt(encrypted, Key);
        var bank = AwcBank.Parse(encrypted, Key);
        Assert.True(bank.WasEncrypted);
        Assert.Equal(encrypted, bank.Build(Key));
    }

    [Fact]
    public void Awc_ReplacePcm_UpdatesFormatPeaksAndKeepsOtherSounds()
    {
        var plain = MakeBank(("DUMP_VALVE", Tone(100, 1000), 24000), ("WASTE_GATE", Tone(50, 300), 32000));
        var bank = AwcBank.Parse(plain, null);
        var replacement = Tone(4096 * 3 + 10, 5000);
        replacement[4096 + 7] = 20000; // peak of the second block
        bank.ReplacePcm(AwcBank.StreamId("dump_valve.wav"), replacement, 44100);

        var rebuilt = AwcBank.Parse(bank.Build(null), null);
        var dump = rebuilt.Find(AwcBank.StreamId("DUMP_VALVE"))!;
        var fmt = dump.Find(AwcBank.FormatChunk)!.Data;
        Assert.Equal((uint)replacement.Length, BinaryPrimitives.ReadUInt32LittleEndian(fmt));
        Assert.Equal(44100, BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(8)));
        Assert.Equal(-191, BinaryPrimitives.ReadInt16LittleEndian(fmt.AsSpan(10))); // headroom untouched
        Assert.Equal(10000, BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(20))); // 2 × peak of the first block
        var peaks = dump.Find(AwcBank.PeakChunk)!.Data; // full blocks 2 and 3
        Assert.Equal(
            new ushort[] { 40000, 10000 },
            new ushort[]
            {
                BinaryPrimitives.ReadUInt16LittleEndian(peaks),
                BinaryPrimitives.ReadUInt16LittleEndian(peaks.AsSpan(2)),
            }
        );
        Assert.Equal(replacement.Length * 2, dump.Find(AwcBank.DataChunk)!.Data.Length);
        Assert.Equal(
            Tone(50, 300).Length * 2,
            rebuilt.Find(AwcBank.StreamId("WASTE_GATE"))!.Find(AwcBank.DataChunk)!.Data.Length
        );
    }

    [Fact]
    public void Awc_UnknownSound_IsAnError()
    {
        var bank = AwcBank.Parse(MakeBank(("DUMP_VALVE", Tone(10, 1), 24000)), null);
        Assert.Throws<InvalidDataException>(() => bank.ReplacePcm(AwcBank.StreamId("NOT_THERE"), Tone(10, 1), 24000));
    }

    [Fact]
    public void Wav_StereoIsMixedToMono()
    {
        var pcm = WavFile.Read(Wav([100, -200, 300], 22050, channels: 2));
        Assert.Equal(22050, pcm.SampleRate);
        Assert.Equal(new short[] { 100, -200, 300 }, pcm.Samples);
        Assert.Equal(2, pcm.SourceChannels);
        Assert.Throws<InvalidDataException>(() => WavFile.Read(Encoding.ASCII.GetBytes("not a wav file at all")));
    }

    // ---------- Variants ----------

    [Fact]
    public void Variants_FoundByStructure_NotByNames()
    {
        string[] files =
        [
            @"Mod\Dump Valve\Silent\DUMP_VALVE.wav",
            @"Mod\Dump Valve\Version 1\DUMP_VALVE.wav",
            @"Mod\Dump Valve\Version 2\DUMP_VALVE.wav",
            @"Mod\Waste Gate (New)\Version 1\WASTE_GATE.wav",
            @"Mod\Waste Gate (Old)\Version 1\WASTE_GATE.wav",
            @"Mod\Waste Gate (Old)\Version 2\WASTE_GATE.wav",
            @"Mod\Original files\DUMP_VALVE.wav",
            @"Mod\Empty\0x0103B3B2.wav",
            @"Mod\Readme.txt",
            @"Mod\preview.png",
        ];
        var groups = Variants.Find(files);
        Assert.Equal(2, groups.Count);
        var dump = groups.Single(g => g.Title == "Dump Valve");
        Assert.Equal(["Silent", "Version 1", "Version 2"], dump.Options.Select(o => o.Label));
        var gate = groups.Single(g => g.Title == "WASTE_GATE.wav");
        Assert.Contains("Waste Gate (Old) / Version 2", gate.Options.Select(o => o.Label));

        var chosen = new Dictionary<string, string>
        {
            [dump.Key] = dump.Options[2].Key,
            [gate.Key] = gate.Options[0].Key,
        };
        var kept = Variants.Apply(files.Where(f => !Variants.InBackupFolder(f)), groups, chosen);
        Assert.Contains(@"Mod\Dump Valve\Version 2\DUMP_VALVE.wav", kept);
        Assert.DoesNotContain(@"Mod\Dump Valve\Silent\DUMP_VALVE.wav", kept);
        Assert.Contains(@"Mod\Empty\0x0103B3B2.wav", kept);
        Assert.Single(kept, f => f.EndsWith("WASTE_GATE.wav"));
    }

    [Fact]
    public void Variants_IgnoreSteps_Genders_SeparateDlcPacks_AndPreviews()
    {
        string[] files =
        [
            @"PART 1\player_two\a.ydd",
            @"PART 2\player_two\a.ydd", // installation steps
            @"MP Male\feet_000_u.ydd",
            @"MP Female\feet_000_u.ydd", // clothing for both genders
            @"Legacy\spbmr\dlc.rpf",
            @"Legacy\spbmr_patch\dlc.rpf", // two different DLC packs
            @"Shots A\1.jpg",
            @"Shots B\1.jpg", // preview
            @"menyooStuff\Outfit\GhostRider.xml", // trainer data: one coincidental shared name,
            @"menyooStuff\Vehicle\GhostRider.xml",
            @"menyooStuff\Vehicle\GhostRiderCats.xml",
            @"menyooStuff\Vehicle\GhostRiderCandles.xml", // not variants
        ];
        Assert.Empty(Variants.Find(files));
    }

    [Fact]
    public void Variants_DlcPackVariants_AreFound()
    {
        var groups = Variants.Find([
            @"SP_MPmaps_OFF\tlzdecotower\dlc.rpf",
            @"SP_MPmaps_ON\tlzdecotower\dlc.rpf",
            @"scripts\TlzDecoLift.3.cs",
        ]);
        var group = Assert.Single(groups);
        Assert.Equal(["SP_MPmaps_OFF", "SP_MPmaps_ON"], group.Options.Select(o => o.Label));
    }

    // ---------- Instructions ----------

    [Theory]
    [InlineData(
        @"Grand Theft Auto V\mods\update\x64\dlcpacks\mpjanuary2016\dlc.rpf\x64\audio\sfx\dlc_ja16\",
        @"update\x64\dlcpacks\mpjanuary2016\dlc.rpf",
        @"x64\audio\sfx\dlc_ja16"
    )]
    [InlineData(@"\x64\audio\sfx\RESIDENT.rpf", @"x64\audio\sfx\RESIDENT.rpf", "")]
    [InlineData(@"Grand Theft Auto V/(mods)/x64/audio/sfx/RESIDENT.rpf", @"x64\audio\sfx\RESIDENT.rpf", "")]
    [InlineData(
        @"D:\Steam\steamapps\common\Grand Theft Auto V\mods\x64v.rpf\models\cdimages\streamedpeds_players.rpf\player_two",
        @"x64v.rpf|models\cdimages\streamedpeds_players.rpf",
        "player_two"
    )]
    public void ParsePath_HandlesRootsModsAndNestedArchives(string raw, string archives, string dir)
    {
        var path = ReadmeInstructions.ParsePath(raw)!;
        Assert.Equal(archives.Split('|'), path.Archives);
        Assert.Equal(dir, path.Dir);
    }

    [Fact]
    public void Instructions_OpenIvSoundRecipe_BecomesAwcReplacement()
    {
        const string readme = """
            - In OpenIV, go in Edit Mode, and go to \x64\audio\sfx\RESIDENT.rpf (or \mods\x64\audio\sfx\RESIDENT.rpf if you use that).
            - Right click on "vehicles.awc" and click on "Export to opnFormat (.oac)".
            - Copy the DUMP_VALVE.wav files of the version you want and put them in the "vehicles" folder.
            - Click on "New" then "Import openFormats".
            """;
        string[] files =
        [
            @"Mod\Dump Valve\Version 1\DUMP_VALVE.wav",
            @"Mod\Empty\0x0103B3B2.wav",
            @"Mod\Samples\preview.wav",
        ];
        var plan = ReadmeInstructions.Plan(files, readme)!;
        Assert.Empty(plan.Errors);
        var archive = Assert.IsType<OivArchive>(Assert.Single(plan.Ops));
        Assert.Equal(@"x64\audio\sfx\RESIDENT.rpf", archive.Path);
        var awc = Assert.IsType<OivAwc>(Assert.Single(archive.Ops));
        Assert.Equal("vehicles.awc", awc.Path);
        Assert.Equal(["DUMP_VALVE", "0x0103B3B2"], awc.Sounds.Select(s => s.Name)); // samples from Samples are not installed
    }

    [Fact]
    public void Instructions_ReplaceUnquotedFilesInPath_RequiresExistingFolder()
    {
        const string readme =
            "- Using OpenIV, replace car_engine.awc and car_engine_npc.awc in in Grand Theft Auto V\\mods\\update\\x64\\dlcpacks\\mpjanuary2016\\dlc.rpf\\x64\\audio\\sfx\\dlc_ja16\\";
        string[] files =
        [
            @"Sound\Version 1\car_engine.awc",
            @"Sound\Version 1\car_engine_npc.awc",
            @"Sound\Version 1\Samples\Idle.wav",
        ];
        var plan = ReadmeInstructions.Plan(files, readme)!;
        var outer = Assert.IsType<OivArchive>(Assert.Single(plan.Ops));
        Assert.Equal(@"update\x64\dlcpacks\mpjanuary2016\dlc.rpf", outer.Path);
        Assert.Equal(new OivRequire(@"x64\audio\sfx\dlc_ja16", "car_engine.awc"), outer.Ops[0]);
        Assert.Equal(
            [@"x64\audio\sfx\dlc_ja16\car_engine.awc", @"x64\audio\sfx\dlc_ja16\car_engine_npc.awc"],
            outer.Ops.Skip(1).Cast<OivAdd>().Select(a => a.Target)
        );
        Assert.Equal(2, plan.Used.Count);
    }

    [Fact]
    public void Install_MissingFolderInsideArchive_StopsWithHint()
    {
        var game = Path.Combine(_root, "GTA V Enhanced");
        Directory.CreateDirectory(Path.Combine(game, "mods", "update"));
        File.WriteAllText(Path.Combine(game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(game, "OpenRPF.asi"), "MZ stub loader");
        using (var fs = File.Create(Path.Combine(game, "mods", "update", "update.rpf")))
            new Rpf7Builder()
                .AddFile(@"x64\audio\sfx\real_folder\car_engine.awc", new RpfBinarySource([1, 2, 3, 4]))
                .Write(fs);
        var source = Path.Combine(_root, "mod.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("car_engine.awc").Open())
            s.Write([9, 9, 9, 9]);
        var g = GameInstall.Open(game);
        g.RunningCheckOverride = () => false;
        using var archive = ModArchive.Open(source);
        var ops = new List<OivOp>
        {
            new OivArchive(
                @"update\update.rpf",
                false,
                "RPF7",
                [
                    new OivRequire(@"x64\audio\sfx\old_folder", "car_engine.awc"),
                    new OivAdd("car_engine.awc", @"x64\audio\sfx\old_folder\car_engine.awc"),
                ]
            ),
        };

        using var plan = new OivInstaller(g).Prepare("Sound", ops, archive);

        var error = Assert.Single(plan.Errors);
        Assert.Contains(@"x64\audio\sfx\old_folder", error);
        Assert.Contains(@"x64\audio\sfx\real_folder", error);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Install_AwcOp_ReplacesSoundInsideArchive()
    {
        var game = Path.Combine(_root, "GTA V Enhanced");
        Directory.CreateDirectory(Path.Combine(game, "mods", "x64", "audio", "sfx"));
        File.WriteAllText(Path.Combine(game, "GTA5_Enhanced.exe"), "stub");
        File.WriteAllText(Path.Combine(game, "OpenRPF.asi"), "MZ stub loader");
        var bankPath = Path.Combine(game, "mods", "x64", "audio", "sfx", "RESIDENT.rpf");
        using (var fs = File.Create(bankPath))
            new Rpf7Builder()
                .AddFile("vehicles.awc", new RpfBinarySource(MakeBank(("DUMP_VALVE", Tone(10, 100), 24000))))
                .Write(fs);
        var source = Path.Combine(_root, "sound.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry(@"Mod/DUMP_VALVE.wav").Open())
            s.Write(Wav(Tone(500, 7000), 32000));
        var g = GameInstall.Open(game);
        g.RunningCheckOverride = () => false;
        using var archive = ModArchive.Open(source);
        var ops = new List<OivOp>
        {
            new OivArchive(
                @"x64\audio\sfx\RESIDENT.rpf",
                false,
                "RPF7",
                [new OivAwc("vehicles.awc", [new OivAwcSound(@"Mod\DUMP_VALVE.wav", "DUMP_VALVE")])]
            ),
        };

        using var plan = new OivInstaller(g).Prepare("Sound", ops, archive);
        Assert.Empty(plan.Errors);
        var change = Assert.Single(plan.Changes);
        using var rpf = Rpf7Archive.OpenRead(change.SourcePath!);
        var entry = (RpfBinaryFile)rpf.Find("vehicles.awc")!;
        Assert.Equal(0u, entry.CompressedSize); // the bank is stored uncompressed
        var bank = AwcBank.Parse(rpf.ReadContent(entry), null);
        Assert.Equal(1000, bank.Find(AwcBank.StreamId("DUMP_VALVE"))!.Find(AwcBank.DataChunk)!.Data.Length);
    }

    [Fact]
    public void PickEdition_UsesPackageFileNames()
    {
        var oivs = new List<string> { @"Minimap\Enhanced.oiv", @"Minimap\Legacy.oiv" };
        Assert.Equal([@"Minimap\Legacy.oiv"], ModAnalyzer.PickEdition(oivs, GameEdition.Legacy, byFileName: true));
        Assert.Equal([@"Minimap\Enhanced.oiv"], ModAnalyzer.PickEdition(oivs, GameEdition.Enhanced, byFileName: true));
    }
}
