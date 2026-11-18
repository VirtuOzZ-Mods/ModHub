using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Oiv;
using GTAModManager.Core.Rpf;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Installation;

public sealed record RpfEntryInfo(string Path, string Kind, long Size);

public sealed record RpfEditRequest(
    string Archive,
    string InnerPath,
    string? SourceFile,
    bool Delete = false,
    string? ExpectedHash = null
);

public sealed class RpfWorkspace(GameInstall game)
{
    public const string Update = @"update\update.rpf";
    public string WorkingUpdate =>
        File.Exists(Path.Combine(game.Root, @"mods\update\update.rpf")) ? @"mods\update\update.rpf" : Update;

    public string FullPath(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.StartsWith('\\') || relative.StartsWith('/'))
            throw new ArgumentException(L.T("core.installation.relativePathWithinSelectedGame"));
        string clean = GameInstall.Normalize(relative);
        if (!clean.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(L.T("core.installation.rpfArchiveRequired"));
        string full = Path.GetFullPath(Path.Combine(game.Root, clean));
        for (
            string? p = full;
            p is not null && p.StartsWith(game.Root, StringComparison.OrdinalIgnoreCase);
            p = Path.GetDirectoryName(p)
        )
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("core.installation.fileSystemLinksNotSupported"));
        return full;
    }

    public bool Editable(string relative) => GameInstall.IsInMods(GameInstall.Normalize(relative));

    private T Open<T>(string relative, string nested, Func<Rpf7Archive, T> action)
    {
        var children = new List<Rpf7Archive>();
        using var root = Rpf7Archive.OpenRead(FullPath(relative), game.TryLoadKeys());
        try
        {
            var cursor = root;
            foreach (var part in nested.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                if (cursor.Find(GameInstall.Normalize(part)) is not RpfBinaryFile file)
                    throw new InvalidDataException(L.T("core.installation.nestedRpfNotFound", part));
                cursor = cursor.OpenNested(file);
                children.Add(cursor);
            }
            return action(cursor);
        }
        finally
        {
            foreach (var child in children.AsEnumerable().Reverse())
                child.Dispose();
        }
    }

    public IReadOnlyList<RpfEntryInfo> List(string relative, string nested = "") =>
        Open(relative, nested, a => a.Root.Descendants()
                .Select(e => new RpfEntryInfo(
                    e.Path,
                    e switch
                    {
                        RpfDirectory => "Папка",
                        RpfResourceFile => "RSC — просмотр не поддержан",
                        _ => "Файл",
                    },
                    e is RpfFile f ? f.StoredSize : 0
                ))
                .ToArray());

    public byte[] Read(string relative, string path, long limit = 8L << 20)
    {
        var parts = path.Split('|');
        string nested = string.Join('|', parts[..^1]);
        string inner = GameInstall.Normalize(parts[^1]);
        return Open(relative, nested, a =>
        {
            if (a.Find(inner) is not RpfBinaryFile f)
                throw new NotSupportedException(L.T("core.installation.rscResourceDecodingNotSupported"));
            if (f.UncompressedSize > limit)
                throw new InvalidDataException(L.T("core.installation.fileExceedsPreviewExportLimit"));
            return a.ReadContent(f);
        });
    }

    public InstallPlan PrepareCopy(string original)
    {
        original = GameInstall.Normalize(original);
        if (Editable(original))
            throw new ArgumentException(L.T("core.installation.archiveAlreadyMods"));
        FullPath(original);
        if (File.Exists(FullPath(game.MapWriteTarget(original))))
            throw new InvalidOperationException(L.T("core.installation.copyAlreadyExistsModsOpen"));
        return new OivInstaller(game).Prepare(
            L.T("core.installation.copyingSingleRpfMods"),
            [new OivAdd(Path.Combine(game.Root, original), game.MapWriteTarget(original))]
        );
    }

    public InstallPlan PrepareEdit(RpfEditRequest request, bool preview = false)
    {
        string relative = GameInstall.Normalize(request.Archive);
        FullPath(relative);
        if (!Editable(relative))
            throw new InvalidOperationException(L.T("core.installation.originalRpfReadOnlyCreate"));
        if (request.ExpectedHash is not null && FileTransactionEngine.Hash(FullPath(relative)) != request.ExpectedHash)
            throw new IOException(L.T("core.installation.archiveChangedAfterOpeningRefresh"));
        var components = request.InnerPath.Split('|');
        OivOp op = request.Delete
            ? new OivDelete(GameInstall.Normalize(components[^1]))
            : new OivAdd(
                Path.GetFullPath(
                    request.SourceFile ?? throw new ArgumentException(L.T("core.installation.noImportFile"))
                ),
                GameInstall.Normalize(components[^1])
            );
        for (int i = components.Length - 2; i >= 0; i--)
            op = new OivArchive(GameInstall.Normalize(components[i]), false, "RPF7", [op]);
        return new OivInstaller(game).Prepare(
            request.Delete ? L.T("core.installation.removeFromRpf") : L.T("core.installation.importIntoRpf"),
            [new OivArchive(relative, false, "RPF7", [op])],
            preview: preview
        );
    }

    public InstallPlan PrepareXmlImport(
        string source,
        bool prepareMods,
        bool allowUnverifiedGameConfig,
        bool replaceDlc = false,
        bool preview = false
    )
    {
        string incoming = InstallDocuments.ReadText(source);
        var kind = InstallDocuments.Kind(incoming);
        if (kind == XmlDocumentKind.Other)
            throw new InvalidDataException(L.T("core.installation.xmlDestinationNotRecognizedSpecify"));
        if (kind == XmlDocumentKind.GameConfig && !allowUnverifiedGameConfig)
            throw new InvalidOperationException(L.T("core.installation.gameconfigCompatibilityExactBuildUnconfirmed"));
        if (WorkingUpdate == Update && !prepareMods)
            throw new InvalidOperationException(L.T("core.installation.confirmModsPreparationOnlyCopy"));
        string inner = kind == XmlDocumentKind.GameConfig ? @"common\data\gameconfig.xml" : @"common\data\dlclist.xml";
        string current = Encoding.UTF8.GetString(Read(WorkingUpdate, inner)).TrimStart('\uFEFF');
        string result =
            kind == XmlDocumentKind.DlcList && !replaceDlc ? InstallDocuments.MergeDlc(current, incoming) : incoming;
        var memory = new TextSource(result);
        return new OivInstaller(game).Prepare(
            kind == XmlDocumentKind.DlcList
                ? L.T("core.installation.mergeDlcList")
                : L.T("core.installation.importGameconfig"),
            [new OivArchive(Update, false, "RPF7", [new OivAdd("document", inner)])],
            memory,
            preview
        );
    }

    private sealed class TextSource(string text) : Mods.IModSource
    {
        readonly byte[] bytes = Encoding.UTF8.GetBytes(text);

        public Stream OpenSource(string source) => new MemoryStream(bytes, false);

        public long SourceSize(string source) => bytes.Length;
    }
}
