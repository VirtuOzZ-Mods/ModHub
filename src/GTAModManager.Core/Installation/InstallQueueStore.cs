using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Installation;

public enum InstallPhase
{
    Downloading,
    Analysis,
    AwaitingConfirmation,
    Dependencies,
    Installing,
    Verifying,
    Completed,
    Error,
}

public sealed class InstallQueueRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameRoot { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string? RecommendedId { get; set; }
    public string? ScriptLibraryId { get; set; }
    public string? DownloadId { get; set; }
    public InstallPhase Phase { get; set; }
    public string Message { get; set; } = "";
    public string? TransactionId { get; set; }
    public DateTime Updated { get; set; } = DateTime.UtcNow;
}

public sealed class InstallQueueStore
{
    readonly string path;

    public InstallQueueStore(string gameRoot, string? directory = null)
    {
        string id = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot).ToUpperInvariant()))
        );
        path = Path.Combine(directory ?? AppPaths.LibraryFile("install-queue"), id + ".json");
    }

    public List<InstallQueueRecord> Load()
    {
        if (!File.Exists(path))
            return [];
        if (new FileInfo(path).Length > 8L << 20)
            throw new InvalidDataException(L.T("core.installation.queueHistoryExceedsLimit"));
        return JsonSerializer.Deserialize<List<InstallQueueRecord>>(File.ReadAllText(path)) ?? [];
    }

    public void Save(InstallQueueRecord record)
    {
        using var mutex = new Mutex(
            false,
            "ModHub.InstallQueue."
                + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))
        );
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
                throw new IOException(L.T("core.installation.queueUseByAnotherProcess"));
            var records = Load();
            records.RemoveAll(r => r.Id == record.Id);
            record.Updated = DateTime.UtcNow;
            records.Add(record);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(records.OrderByDescending(r => r.Updated).Take(200)));
                File.Move(temp, path, true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }
}
