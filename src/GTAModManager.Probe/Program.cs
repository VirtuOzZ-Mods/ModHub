using System.Security.Cryptography;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Rpf;

// Developer diagnostics tool. All commands only read game files.
// Output is written exclusively to outDir, which cannot be inside the game folder.

Console.OutputEncoding = System.Text.Encoding.UTF8;

return args switch
{
    ["headers", var dir] => Headers(dir),
    ["inspect", var rpf] => Inspect(rpf),
    ["roundtrip", var rpf, var outDir] => RoundTrip(rpf, outDir),
    ["cat", var rpf, var inner] => Cat(rpf, inner),
    ["keys", var exe] => Keys(exe),
    ["ng-inspect", var game, var rpf] => NgInspect(game, rpf),
    ["dlcadd", var rpf, var pack, var outDir] => DlcAdd(rpf, pack, outDir),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine(L.T("core.probe.gtamodmanagerProbeReadOnlyHeaders"));
    return 2;
}

static int Headers(string gameDir)
{
    var files = new List<string>();
    files.AddRange(Directory.EnumerateFiles(gameDir, "*.rpf", SearchOption.TopDirectoryOnly));
    foreach (var sub in new[] { "update", "mods" })
    {
        var p = Path.Combine(gameDir, sub);
        if (Directory.Exists(p))
            files.AddRange(Directory.EnumerateFiles(p, "*.rpf", SearchOption.AllDirectories));
    }

    var stats = new Dictionary<string, int>();
    foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase))
    {
        string kind;
        uint entries = 0;
        try
        {
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = RpfHeader.Read(fs);
            kind = h.IsRpf7 ? h.Encryption.ToString() : $"not-RPF7(0x{h.Magic:X8})";
            entries = h.EntryCount;
        }
        catch (Exception ex)
        {
            kind = "error: " + ex.GetType().Name;
        }

        var rel = Path.GetRelativePath(gameDir, f);
        var bucket = rel.StartsWith("mods", StringComparison.OrdinalIgnoreCase) ? "mods" : "game";
        stats[$"{bucket}/{kind}"] = stats.GetValueOrDefault($"{bucket}/{kind}") + 1;
        Console.WriteLine($"{kind, -10} {entries, 7}  {new FileInfo(f).Length / 1048576.0, 9:F1} MB  {rel}");
    }
    Console.WriteLine();
    foreach (var (k, v) in stats.OrderBy(x => x.Key))
        Console.WriteLine(L.T("core.probe.total", k, v));
    return 0;
}

static int Inspect(string path)
{
    using var a = Rpf7Archive.OpenRead(path);
    var r = InspectArchive(a, Path.GetFileName(path), 0);
    Console.WriteLine(r ? L.T("core.probe.okInvariantsSatisfied") : L.T("core.probe.warningViolationsFound"));
    return r ? 0 : 1;
}

static bool InspectArchive(Rpf7Archive a, string label, int depth)
{
    string pad = new(' ', depth * 2);
    var all = a.Root.Descendants().ToList();
    var dirs = all.OfType<RpfDirectory>().Count() + 1;
    var bins = all.OfType<RpfBinaryFile>().ToList();
    var ress = all.OfType<RpfResourceFile>().ToList();
    bool ok = true;

    // Ordering invariant: children are sorted ordinally by lower-case name.
    int unsorted = 0;
    foreach (var d in all.OfType<RpfDirectory>().Prepend(a.Root))
    {
        var names = d.Children.Select(c => c.Name.ToLowerInvariant()).ToList();
        if (!names.SequenceEqual(names.Order(StringComparer.Ordinal)))
            unsorted++;
    }

    // Data is within the archive bounds; deflate can be read.
    int outOfRange = 0,
        inflateFail = 0,
        inflated = 0;
    foreach (var f in all.OfType<RpfFile>())
        if ((long)f.OffsetBlocks * Rpf7Format.BlockSize + f.StoredSize > a.Length)
            outOfRange++;
    foreach (var b in bins.Where(b => b.CompressedSize != 0 && b.IsEncrypted == 0).Take(50))
    {
        try
        {
            a.ReadContent(b);
            inflated++;
        }
        catch
        {
            inflateFail++;
        }
    }
    var badRes = ress.Where(r => ReadMagic(a, r) != Rpf7Format.ResourceMagic).ToList();
    int rscBad = badRes.Count;
    foreach (var g in badRes.GroupBy(r => ReadMagic(a, r)).Take(5))
        Console.WriteLine(
            L.T(
                "core.probe.resourcesMagic0xEG",
                pad,
                g.Key,
                g.Count(),
                g.First().Path,
                ((RpfResourceFile)g.First()).SystemFlags
            )
        );
    // Resources without RSC7 are encrypted content (copied from an NG archive); that is not a structural violation.
    if (outOfRange + inflateFail > 0)
        ok = false;

    Console.WriteLine(
        L.T(
            "core.probe.directoriesBinariesCompressedEncryptedResources",
            pad,
            label,
            a.Header.Encryption,
            dirs,
            bins.Count,
            bins.Count(b => b.CompressedSize != 0),
            bins.Count(b => b.IsEncrypted != 0),
            ress.Count,
            ress.Count(r => r.SizeField == Rpf7Format.MaxThreeByte)
        )
    );
    Console.WriteLine(
        L.T("core.probe.unsortedDirectoriesOutRangeDeflate", pad, unsorted, outOfRange, inflated, inflateFail, rscBad)
    );

    foreach (var nested in bins.Where(b => b.Name.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
    {
        try
        {
            using var n = a.OpenNested(nested);
            ok &= InspectArchive(n, nested.Path, depth + 1);
        }
        catch (RpfEncryptedException ex)
        {
            Console.WriteLine(L.T("core.probe.tableContentsSkipped", pad, nested.Path, ex.Encryption));
        }
        catch (RpfException ex)
        {
            Console.WriteLine($"{pad}  {nested.Path}: {ex.Message}");
        }
    }
    return ok;
}

static bool OutsideGame(string full, string outFull)
{
    for (var d = Path.GetDirectoryName(full); d is not null; d = Path.GetDirectoryName(d))
    {
        bool isGameRoot = File.Exists(Path.Combine(d, "GTA5.exe")) || File.Exists(Path.Combine(d, "GTA5_Enhanced.exe"));
        if (isGameRoot && outFull.StartsWith(d, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(L.T("core.probe.refusedOutputFolderInsideGame", d));
            return false;
        }
    }
    return true;
}

static int DlcAdd(string path, string pack, string outDir)
{
    const string dlclist = @"common\data\dlclist.xml";
    var full = Path.GetFullPath(path);
    var outFull = Path.GetFullPath(outDir);
    if (!OutsideGame(full, outFull))
        return 3;
    Directory.CreateDirectory(outFull);
    var target = Path.Combine(outFull, "update.dlcadd.rpf");

    using var src = Rpf7Archive.OpenRead(full);
    var builder = Rpf7Builder.FromArchive(src);
    var bytes = builder.TryReadContent(dlclist);
    if (bytes is null)
    {
        Console.WriteLine(L.T("core.probe.dlclistXmlUnavailableWithoutKeys"));
        return 1;
    }

    var bom = bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]);
    var text = System.Text.Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
    var (xml, changed) = GTAModManager.Core.Dlc.DlcList.Add(text, pack);
    Console.WriteLine(changed ? L.T("core.probe.packAdded", pack) : L.T("core.probe.packAlreadyExistsNoChanges", pack));
    var newBytes = (bom ? [0xEF, 0xBB, 0xBF] : Array.Empty<byte>())
        .Concat(System.Text.Encoding.UTF8.GetBytes(xml))
        .ToArray();
    builder.AddFile(dlclist, new RpfBinarySource(newBytes, Compress: true));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    using (var fs = File.Create(target))
        builder.Write(fs);
    Console.WriteLine(L.T("core.probe.writtenSMb", sw.Elapsed.TotalSeconds, new FileInfo(target).Length / 1048576.0));

    using var dst = Rpf7Archive.OpenRead(target);
    var check = System.Text.Encoding.UTF8.GetString(dst.ReadContent((RpfBinaryFile)dst.Find(dlclist)!));
    bool hasPack = GTAModManager.Core.Dlc.DlcList.Packs(check.TrimStart('﻿')).Contains(pack);

    var a = src.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
    int other = 0,
        diff = 0;
    foreach (var f in dst.Files)
    {
        if (f.Path.Equals(dlclist, StringComparison.OrdinalIgnoreCase))
            continue;
        other++;
        if (!a.TryGetValue(f.Path, out var o) || !src.ReadRaw(o).AsSpan().SequenceEqual(dst.ReadRaw(f)))
            diff++;
    }
    diff += Math.Abs(a.Count - dst.Files.Count());
    Console.WriteLine(L.T("core.probe.packNewDlclistOtherFiles", hasPack, other, diff));
    Console.WriteLine(L.T("core.probe.copy", target));
    return hasPack && diff == 0 ? 0 : 1;
}

static int RoundTrip(string path, string outDir)
{
    var full = Path.GetFullPath(path);
    var outFull = Path.GetFullPath(outDir);
    if (!OutsideGame(full, outFull))
        return 3;

    Directory.CreateDirectory(outFull);
    var target = Path.Combine(outFull, Path.GetFileNameWithoutExtension(full) + ".roundtrip.rpf");

    using var src = Rpf7Archive.OpenRead(full);
    using (var fs = File.Create(target))
        Rpf7Builder.FromArchive(src).Write(fs);
    using var dst = Rpf7Archive.OpenRead(target);

    var a = src.Root.Descendants().ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
    var b = dst.Root.Descendants().ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
    int mismatches = 0,
        compared = 0;
    foreach (var (p, e) in a)
    {
        if (!b.TryGetValue(p, out var other) || other.GetType() != e.GetType())
        {
            mismatches++;
            Console.WriteLine(L.T("core.probe.missingDifferentType", p));
            continue;
        }
        if (e is not RpfFile fa)
            continue;
        var fb = (RpfFile)other;
        compared++;
        bool same = SHA256.HashData(src.ReadRaw(fa)).AsSpan().SequenceEqual(SHA256.HashData(dst.ReadRaw(fb)));
        same &= (fa, fb) switch
        {
            (RpfBinaryFile x, RpfBinaryFile y) => x.CompressedSize == y.CompressedSize
                && x.UncompressedSize == y.UncompressedSize,
            (RpfResourceFile x, RpfResourceFile y) => x.SystemFlags == y.SystemFlags
                && x.GraphicsFlags == y.GraphicsFlags
                && x.ActualSize == y.ActualSize,
            _ => false,
        };
        if (!same)
        {
            mismatches++;
            Console.WriteLine(L.T("core.probe.difference", p));
        }
    }
    mismatches += b.Keys.Count(k => !a.ContainsKey(k));

    bool identicalOrder = src
        .Root.Descendants()
        .Select(e => e.Path)
        .SequenceEqual(dst.Root.Descendants().Select(e => e.Path));
    bool identicalBytes = FileHash(full) == FileHash(target);
    bool identicalToc = MaskedToc(full).SequenceEqual(MaskedToc(target));
    Console.WriteLine(L.T("core.probe.tableContentsEntriesNamesWithout", identicalToc));
    Console.WriteLine(L.T("core.probe.entriesFilesComparedDifferences", a.Count, compared, mismatches));
    Console.WriteLine(L.T("core.probe.tableContentsOrderMatchesFile", identicalOrder, identicalBytes));
    Console.WriteLine(L.T("core.probe.copy", target));
    return mismatches == 0 ? 0 : 1;
}

static int Keys(string exe)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var k = GTAModManager.Core.Crypto.Gta5Keys.Search(File.ReadAllBytes(exe));
        Console.WriteLine(L.T("core.probe.allKeysFoundSAes", sw.Elapsed.TotalSeconds));
        return 0;
    }
    catch (GTAModManager.Core.Crypto.KeysNotFoundException ex)
    {
        Console.WriteLine(L.T("core.probe.elapsedError", ex.Message, sw.Elapsed.TotalSeconds));
        Console.WriteLine(L.T("core.probe.found", GTAModManager.Core.Crypto.Gta5Keys.LastSearchSummary));
        return 1;
    }
}

static int NgInspect(string gameDir, string rpf)
{
    var g = GTAModManager.Core.Game.GameInstall.Open(gameDir);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var keys = g.LoadKeys();
    Console.WriteLine(L.T("core.probe.keysS", sw.Elapsed.TotalSeconds));
    using var a = Rpf7Archive.OpenRead(Path.Combine(g.Root, rpf), keys);
    var files = a.Files.ToList();
    Console.WriteLine(
        L.T(
            "core.probe.entriesExamples",
            rpf,
            a.Header.Encryption,
            files.Count,
            string.Join(", ", files.Take(5).Select(f => f.Path))
        )
    );
    var enc = files.OfType<RpfBinaryFile>().Where(f => f.IsEncrypted != 0).ToList();
    int ok = 0,
        bad = 0;
    foreach (
        var f in enc.Where(f => f.Name.EndsWith(".xml") || f.Name.EndsWith(".meta") || f.Name.EndsWith(".dat")).Take(30)
    )
    {
        try
        {
            var t = System.Text.Encoding.UTF8.GetString(a.ReadContent(f)).TrimStart('﻿');
            if (t.TrimStart().StartsWith('<') || t.All(c => c >= 9))
                ok++;
            else
                bad++;
        }
        catch
        {
            bad++;
        }
    }
    Console.WriteLine(L.T("core.probe.encryptedFilesTextFilesDecrypted", enc.Count, ok, bad));
    var sample = enc.FirstOrDefault(f => f.Name.EndsWith(".xml") || f.Name.EndsWith(".meta"));
    if (sample is not null)
        Console.WriteLine($"{sample.Path}:\n{System.Text.Encoding.UTF8.GetString(a.ReadContent(sample))[..200]}");
    return bad == 0 ? 0 : 1;
}

static int Cat(string path, string inner)
{
    using var a = Rpf7Archive.OpenRead(path);
    if (a.Find(inner) is not RpfBinaryFile f)
    {
        Console.WriteLine(L.T("core.probe.notFoundNotBinaryFile"));
        return 1;
    }
    Console.WriteLine($"{f.Path}: compressed={f.CompressedSize} size={f.UncompressedSize} encrypted={f.IsEncrypted}");
    if (f.IsEncrypted != 0)
        return 0;
    var text = System.Text.Encoding.UTF8.GetString(a.ReadContent(f));
    Console.WriteLine(text.Length > 1500 ? text[..1500] + "\n…" : text);
    return 0;
}

static byte[] MaskedToc(string path)
{
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    var h = RpfHeader.Read(fs);
    var toc = new byte[h.EntryCount * Rpf7Format.EntrySize + h.NamesLength];
    fs.ReadExactly(toc);
    for (int i = 0; i < h.EntryCount; i++)
    {
        int o = i * Rpf7Format.EntrySize;
        if (BitConverter.ToUInt32(toc, o + 4) == Rpf7Format.DirectoryIdent)
            continue;
        toc[o + 5] = toc[o + 6] = 0;
        toc[o + 7] &= 0x80; // keep the resource flag
    }
    return toc;
}

static uint ReadMagic(Rpf7Archive a, RpfResourceFile r) =>
    r.StoredSize < 4 ? 0 : BitConverter.ToUInt32(a.ReadRaw(r).AsSpan(0, 4));

static string FileHash(string p)
{
    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    return Convert.ToHexString(SHA256.HashData(fs));
}
