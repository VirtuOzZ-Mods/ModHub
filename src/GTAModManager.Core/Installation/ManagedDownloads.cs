using System.Security.Cryptography;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Transactions;

namespace GTAModManager.Core.Installation;

public sealed class DownloadReceipt
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string GameRoot { get; set; } = "";
    public string? Hash { get; set; }
    public bool Completed { get; set; }
}

/// <summary>Own downloads only. Never enumerate or clean the user's Downloads folder.</summary>
public sealed class ManagedDownloads
{
    readonly string root;

    public ManagedDownloads(string? directory = null) =>
        root = Path.GetFullPath(directory ?? AppPaths.Downloads);

    string Folder(string id) =>
        id.Length == 32 && id.All(Uri.IsHexDigit)
            ? Path.Combine(root, id)
            : throw new ArgumentException(L.T("core.installation.invalidDownloadId"));

    public DownloadReceipt Begin(string name, string sourceUrl, string gameRoot)
    {
        string clean = Path.GetFileName(name);
        if (
            string.IsNullOrWhiteSpace(clean)
            || clean.Equals("receipt.json", StringComparison.OrdinalIgnoreCase)
            || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        )
            throw new ArgumentException(L.T("core.installation.invalidDownloadName"));
        var receipt = new DownloadReceipt
        {
            Id = Guid.NewGuid().ToString("N"),
            SourceUrl = sourceUrl,
            GameRoot = Path.GetFullPath(gameRoot),
        };
        Directory.CreateDirectory(Folder(receipt.Id));
        receipt.File = Path.Combine(Folder(receipt.Id), clean);
        Save(receipt);
        return receipt;
    }

    void Save(DownloadReceipt r) =>
        System.IO.File.WriteAllText(Path.Combine(Folder(r.Id), "receipt.json"), JsonSerializer.Serialize(r));

    public DownloadReceipt Load(string id) =>
        JsonSerializer.Deserialize<DownloadReceipt>(
            System.IO.File.ReadAllText(Path.Combine(Folder(id), "receipt.json"))
        ) ?? throw new InvalidDataException(L.T("core.installation.corruptDownloadRecord"));

    public DownloadReceipt Complete(string id)
    {
        var r = Load(id);
        Validate(r);
        if (!System.IO.File.Exists(r.File))
            throw new IOException(L.T("core.installation.downloadIncomplete"));
        using (var stream = new FileStream(r.File, FileMode.Open, FileAccess.Read, FileShare.Read))
            r.Hash = Convert.ToHexString(SHA256.HashData(stream));
        r.Completed = true;
        Save(r);
        return r;
    }

    void Validate(DownloadReceipt r)
    {
        string directory = Folder(r.Id);
        if (
            !Path.GetFullPath(r.File)
                .StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(r.File) != directory
        )
            throw new InvalidDataException(L.T("core.installation.fileDoesNotBelongDownload"));
        for (string? p = directory; p is not null; p = Path.GetDirectoryName(p))
            if (Directory.Exists(p) && (System.IO.File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("core.installation.downloadFolderLink"));
        if (System.IO.File.Exists(r.File) && (System.IO.File.GetAttributes(r.File) & FileAttributes.ReparsePoint) != 0)
            throw new IOException(L.T("core.installation.downloadFileLink"));
    }

    public void Retarget(string id, string previousRoot, string selectedRoot)
    {
        var receipt = Load(id);
        Validate(receipt);
        if (
            !receipt.GameRoot.Equals(Path.GetFullPath(previousRoot), StringComparison.OrdinalIgnoreCase)
            || !receipt.Completed
            || receipt.Hash is null
            || FileTransactionEngine.Hash(receipt.File) != receipt.Hash
        )
            throw new InvalidOperationException(L.T("core.installation.downloadChangedBelongsAnotherGame"));
        receipt.GameRoot = GameInstall.Open(selectedRoot).Root;
        Save(receipt);
    }

    public void Finish(string id, bool keep, string? library)
    {
        var r = Load(id);
        Validate(r);
        if (!r.Completed || r.Hash is null || FileTransactionEngine.Hash(r.File) != r.Hash)
            throw new InvalidOperationException(L.T("core.installation.sourceArchiveChangedDownloadIncomplete"));
        if (keep)
        {
            if (string.IsNullOrWhiteSpace(library))
                return;
            string target = Path.Combine(Path.GetFullPath(library), r.Hash + "-" + Path.GetFileName(r.File));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!System.IO.File.Exists(target))
                System.IO.File.Copy(r.File, target);
            if (FileTransactionEngine.Hash(target) != r.Hash)
                throw new IOException(L.T("core.installation.couldNotVerifyLibraryCopy"));
        }
        System.IO.File.Delete(r.File); // receipt is retained as source/re-download provenance
    }
}
