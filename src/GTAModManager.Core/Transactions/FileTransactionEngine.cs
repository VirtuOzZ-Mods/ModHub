using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Transactions;

public enum TxState
{
    Staged,
    Committing,
    Completed,
    RolledBack,
    Rejected,
    NeedsIntervention,
    Undone,
}

/// <summary>
/// A change to one file inside targetRoot.
/// ExpectedPreHash = null means "the file must not exist". SourcePath = null means deletion.
/// MoveSource = true: the source is a temp file on the same volume and can be moved instead of copied.
/// </summary>
public sealed record FileChange(
    string RelativePath,
    string? ExpectedPreHash,
    string? SourcePath,
    bool MoveSource = false
);

public sealed record TxResult(string Id, TxState State, string Message);

public sealed class TxOp
{
    public required string Rel { get; init; }
    public string? PreHash { get; init; }
    public string? PostHash { get; set; }
    public string? Staged { get; set; }
    public required string Backup { get; init; }

    /// <summary>Written to the journal before the first file change of this operation.</summary>
    public bool Started { get; set; }
}

public sealed class TxJournal
{
    public required string Id { get; init; }
    public TxState State { get; set; }
    public required string TargetRoot { get; init; }
    public required string StagingDir { get; init; }

    /// <summary>What was installed (for history and undo).</summary>
    public string? Title { get; set; }

    /// <summary>Temporary full copies were deleted (undo goes through the backup library or is impossible).</summary>
    public bool BackupsDiscarded { get; set; }
    public List<TxOp> Ops { get; init; } = [];
    public DateTimeOffset Updated { get; set; }
}

/// <summary>Exception tests use to simulate a sudden process termination.</summary>
public sealed class SimulatedCrashException(string point) : Exception($"Simulated crash at {point}");

/// <summary>
/// Transactional application of a set of file changes inside one directory (for example game\mods).
/// Protocol: checks → staging → write-ahead journal → backup + replace one file at a time → hash verification.
/// On failure or after a crash it rolls back from the journal; an ambiguous state → NeedsIntervention.
/// </summary>
public sealed class FileTransactionEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _targetRoot;
    private readonly string _workRoot;
    private readonly string _journalDir;

    /// <summary>Returns true if the game is running, in which case writing is forbidden.</summary>
    public Func<bool> IsGameRunning { get; init; } = () => false;
    public Func<bool> IsWriteBlocked { get; init; } = () => false;

    /// <summary>Free space on the targetRoot volume.</summary>
    public Func<long> FreeSpace { get; init; }

    /// <summary>Failure injection points for tests: called with the step name.</summary>
    public Action<string> FaultPoint { get; init; } = _ => { };

    /// <param name="targetRoot">Directory inside which changes are allowed.</param>
    /// <param name="workRoot">Service directory on the same volume (staging, backups, journals).</param>
    public FileTransactionEngine(string targetRoot, string workRoot)
    {
        _targetRoot = Path.GetFullPath(targetRoot);
        _workRoot = Path.GetFullPath(workRoot);
        _journalDir = Path.Combine(_workRoot, "journals");
        if (
            !string.Equals(
                Path.GetPathRoot(_targetRoot),
                Path.GetPathRoot(_workRoot),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new ArgumentException(L.T("core.transactions.workingDirectoryMustSameVolume"));
        if (IsUnder(_workRoot, _targetRoot))
            throw new ArgumentException(L.T("core.transactions.serviceDirectoryCannotInsideTarget"));
        Directory.CreateDirectory(_journalDir);
        FreeSpace = () => new DriveInfo(Path.GetPathRoot(_targetRoot)!).AvailableFreeSpace;
    }

    public TxResult Execute(IReadOnlyList<FileChange> changes, string? title = null)
    {
        using var operation = Game.GameOperationGate.Acquire(_targetRoot);
        var id = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];

        if (IsGameRunning())
            return new(id, TxState.Rejected, L.T("core.common.gameRunningChangesProhibited"));
        if (IsWriteBlocked())
            return new(id, TxState.Rejected, L.T("core.transactions.modsDisabledSwitchingIncompleteChanges"));

        // 1. Validate paths and preconditions (before any writes).
        var ops = new List<(FileChange change, string full)>();
        foreach (var c in changes)
        {
            string full;
            try
            {
                full = ResolveTarget(c.RelativePath);
            }
            catch (ArgumentException ex)
            {
                return new(id, TxState.Rejected, ex.Message);
            }
            if (ops.Any(o => string.Equals(o.full, full, StringComparison.OrdinalIgnoreCase)))
                return new(id, TxState.Rejected, L.T("core.transactions.duplicateTarget", c.RelativePath));
            var cur = HashOrNull(full);
            if (!HashEq(cur, c.ExpectedPreHash))
                return new(
                    id,
                    TxState.Rejected,
                    L.T("core.transactions.preconditionFailedFileHasChanged", c.RelativePath)
                );
            if (c.SourcePath is not null && !File.Exists(c.SourcePath))
                return new(id, TxState.Rejected, L.T("core.transactions.noSourceFile", c.RelativePath));
            ops.Add((c, full));
        }

        var denied = ops.Select(o => o.full).Where(f => !CanWrite(f)).ToList();
        if (denied.Count > 0)
            return new(
                id,
                TxState.Rejected,
                L.T(
                    "core.transactions.noWriteAccessExampleRun",
                    denied.Count,
                    Path.GetRelativePath(_targetRoot, denied[0])
                )
            );

        long need = ops.Sum(o =>
            o.change.SourcePath is null || o.change.MoveSource ? 0 : new FileInfo(o.change.SourcePath).Length
        );
        long reserve = Math.Max(need / 10, 64L << 20);
        if (FreeSpace() < need + reserve)
            return new(id, TxState.Rejected, L.T("core.transactions.notEnoughSpaceMbRequired", (need + reserve) >> 20));

        // 2. Staging (the target is not touched).
        var staging = Path.Combine(_workRoot, "tx", id);
        var journal = new TxJournal
        {
            Id = id,
            TargetRoot = _targetRoot,
            StagingDir = staging,
            State = TxState.Staged,
            Title = title,
        };
        for (int i = 0; i < ops.Count; i++)
        {
            var (c, _) = ops[i];
            var op = new TxOp
            {
                Rel = c.RelativePath,
                PreHash = c.ExpectedPreHash,
                Backup = Path.Combine(staging, "backup", i.ToString()),
            };
            if (c.SourcePath is not null)
            {
                op.Staged = Path.Combine(staging, "new", i.ToString());
                Directory.CreateDirectory(Path.GetDirectoryName(op.Staged)!);
                if (c.MoveSource)
                    File.Move(c.SourcePath, op.Staged);
                else
                    File.Copy(c.SourcePath, op.Staged);
                FlushFile(op.Staged);
                op.PostHash = Hash(op.Staged);
            }
            if (HashEq(op.PreHash, op.PostHash))
                continue; // no-op
            journal.Ops.Add(op);
        }
        Directory.CreateDirectory(Path.Combine(staging, "backup"));
        Save(journal);
        FaultPoint("staged");

        // 3. Commit.
        journal.State = TxState.Committing;
        Save(journal);
        try
        {
            for (int i = 0; i < journal.Ops.Count; i++)
            {
                var op = journal.Ops[i];
                var full = ResolveTarget(op.Rel);
                FaultPoint($"before-op:{i}");
                if (!HashEq(HashOrNull(full), op.PreHash))
                    throw new TxAbort(L.T("core.transactions.fileWasModifiedByExternal", op.Rel));

                op.Started = true;
                Save(journal);
                FaultPoint($"started:{i}");
                if (File.Exists(full))
                    File.Move(full, op.Backup);
                FaultPoint($"after-backup:{i}");
                if (op.Staged is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.Move(op.Staged, full);
                }
                FaultPoint($"after-apply:{i}");
            }

            // 4. Verify.
            foreach (var op in journal.Ops)
                if (!HashEq(HashOrNull(ResolveTarget(op.Rel)), op.PostHash))
                    throw new TxAbort(L.T("core.transactions.postWriteVerificationFailed", op.Rel));
            FaultPoint("verified");
        }
        catch (TxAbort ex)
        {
            var r = Rollback(journal);
            return r.State == TxState.RolledBack
                ? r with
                {
                    Message = L.T("core.transactions.changesRolledBack", ex.Message),
                }
                : r;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var r = Rollback(journal);
            return r.State == TxState.RolledBack
                ? r with
                {
                    Message = L.T("core.transactions.iOErrorChangesRolled", ex.Message),
                }
                : r;
        }

        journal.State = TxState.Completed;
        Save(journal);
        TryDelete(Path.Combine(staging, "new"));
        return new(
            id,
            TxState.Completed,
            L.T("core.transactions.changesAppliedBackups", journal.Ops.Count, Path.Combine(staging, "backup"))
        );
    }

    /// <summary>Called on startup: drives unfinished transactions to RolledBack or NeedsIntervention.</summary>
    public IReadOnlyList<TxResult> Recover()
    {
        using var operation = Game.GameOperationGate.Acquire(_targetRoot);
        if (IsWriteBlocked())
            return [new("", TxState.Rejected, L.T("core.transactions.enableModsFinishSwitchingFirst2"))];
        var results = new List<TxResult>();
        foreach (var file in Directory.EnumerateFiles(_journalDir, "*.json").Order())
        {
            var j = JsonSerializer.Deserialize<TxJournal>(File.ReadAllText(file))!;
            // NeedsIntervention is retried too: rollback only acts when hashes match, so retrying is safe.
            if (j.State is TxState.Staged or TxState.Committing or TxState.NeedsIntervention)
                results.Add(Rollback(j));
        }
        return results;
    }

    /// <summary>
    /// Clear History: deletes journals and service copies of all completed operations. Game files are not touched,
    /// but past installs can no longer be undone afterwards. An unfinished operation blocks clearing (it is recovered first).
    /// </summary>
    public int ClearHistory(IReadOnlySet<string>? preserve = null, bool discardIncomplete = false)
    {
        var scoped = History()
            .Where(j =>
                Path.GetFullPath(j.TargetRoot)
                    .TrimEnd('\\')
                    .Equals(_targetRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var journals = scoped;
        if (
            !discardIncomplete
            && journals.FirstOrDefault(j =>
                j.State is not (TxState.Completed or TxState.Undone or TxState.RolledBack or TxState.Rejected)
            )
                is { } open
        )
            throw new InvalidOperationException(
                L.T("core.transactions.operationIncompleteRecoverBeforeClearing", open.Title ?? open.Id)
            );
        journals = scoped.Where(j => preserve?.Contains(j.Id) != true).ToList();
        foreach (var j in journals)
        {
            var staging = Path.Combine(_workRoot, "tx", j.Id);
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            File.Delete(Path.Combine(_journalDir, j.Id + ".json"));
        }
        return journals.Count;
    }

    public IReadOnlyList<TxJournal> History() =>
        Directory
            .EnumerateFiles(_journalDir, "*.json")
            .Select(f => JsonSnapshot.Read<TxJournal>(f))
            .OfType<TxJournal>()
            .OrderBy(j => j.Id)
            .ToList();

    /// <summary>
    /// Undoes a completed transaction (removes an installed mod).
    /// First verifies that no file was changed after installation; otherwise nothing is touched.
    /// </summary>
    public TxResult Undo(string id)
    {
        using var operation = Game.GameOperationGate.Acquire(_targetRoot);
        if (IsWriteBlocked())
            return new(id, TxState.Rejected, L.T("core.transactions.enableModsFinishSwitchingFirst"));
        var j = LoadJournal(id);
        if (j is null)
            return new(id, TxState.Rejected, L.T("core.transactions.transactionNotFound"));
        if (j.State != TxState.Completed)
            return new(id, TxState.Rejected, L.T("core.transactions.onlyCompletedTransactionCanUndone", j.State));
        if (IsGameRunning())
            return new(id, TxState.Rejected, L.T("core.common.gameRunningChangesProhibited"));

        var conflicts = j
            .Ops.Where(op => !HashEq(HashOrNull(ResolveTarget(op.Rel)), op.PostHash))
            .Select(op => op.Rel)
            .ToList();
        if (conflicts.Count > 0)
            return new(
                id,
                TxState.Rejected,
                L.T("core.transactions.filesModifiedAfterInstallationBy", string.Join(", ", conflicts))
            );
        var missingBackups = j
            .Ops.Where(op => op.PreHash is not null && !File.Exists(op.Backup))
            .Select(op => op.Rel)
            .ToList();
        if (missingBackups.Count > 0)
            return new(id, TxState.Rejected, L.T("core.transactions.noBackups", string.Join(", ", missingBackups)));
        // Check before any change: both game files and backups (which carry the originals' permissions) must be accessible.
        var denied = j
            .Ops.SelectMany(op => new[] { ResolveTarget(op.Rel), op.Backup })
            .Where(p => File.Exists(p) && !CanWrite(p))
            .ToList();
        if (denied.Count > 0)
            return new(id, TxState.Rejected, L.T("core.transactions.noFilePermissionsRunAs", denied[0]));

        // Undo = rollback with the same guarantees: all operations are treated as started.
        foreach (var op in j.Ops)
            op.Started = true;
        j.State = TxState.Committing;
        Save(j);
        var r = Rollback(j);
        if (r.State == TxState.RolledBack)
        {
            j.State = TxState.Undone;
            Save(j);
            TryDelete(j.StagingDir);
            return new(id, TxState.Undone, L.T("core.transactions.installationCanceledOriginalFilesRestored"));
        }
        return r;
    }

    /// <summary>
    /// Deletes temporary full copies of a completed transaction (after the targeted backup is saved to the library,
    /// or when the user opted out of backups). Such a transaction can no longer be undone through Undo.
    /// </summary>
    public void DiscardBackups(string id)
    {
        var j = LoadJournal(id);
        if (j is null || j.State != TxState.Completed)
            return;
        j.BackupsDiscarded = true;
        Save(j);
        TryDelete(j.StagingDir);
    }

    public TxJournal? LoadJournal(string id)
    {
        var p = Path.Combine(_journalDir, id + ".json");
        return JsonSnapshot.Read<TxJournal>(p);
    }

    private TxResult Rollback(TxJournal j)
    {
        var problems = new List<string>();
        for (int i = j.Ops.Count - 1; i >= 0; i--)
        {
            var op = j.Ops[i];
            if (!op.Started)
                continue; // the transaction did not touch this file
            try
            {
                var full = ResolveTarget(op.Rel);
                var cur = HashOrNull(full);
                bool backupExists = File.Exists(op.Backup);

                // Remove our new content (only if the original was saved or did not exist).
                // Rename aside instead of deleting: if another program holds the file (for example OpenIV),
                // deletion stays pending and the name remains taken, while a rename succeeds.
                string? aside = null;
                if (op.PostHash is not null && HashEq(cur, op.PostHash) && (backupExists || op.PreHash is null))
                {
                    aside = Path.Combine(j.StagingDir, "removed", i + "-" + Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    File.Move(full, aside);
                    cur = null;
                }
                if (cur is null && backupExists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    try
                    {
                        File.Move(op.Backup, full);
                    }
                    catch when (aside is not null)
                    {
                        // The original could not be restored: put our file back so the path is not left empty.
                        File.Move(aside, full);
                        throw;
                    }
                    cur = HashOrNull(full);
                }
                if (!HashEq(cur, op.PreHash))
                    problems.Add(L.T("core.transactions.stateMatchesNeitherOriginalNor", op.Rel));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add(L.T("core.transactions.closeProgramsUsingGameFiles", op.Rel, ex.Message));
            }
        }
        if (problems.Count == 0)
            TryDelete(Path.Combine(j.StagingDir, "removed"));

        j.State = problems.Count == 0 ? TxState.RolledBack : TxState.NeedsIntervention;
        Save(j);
        if (j.State == TxState.RolledBack)
            TryDelete(Path.Combine(j.StagingDir, "new"));
        return new(
            j.Id,
            j.State,
            problems.Count == 0
                ? L.T("core.transactions.rollbackCompleted")
                : L.T("core.transactions.checkRequired", string.Join("; ", problems))
        );
    }

    private string ResolveTarget(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.Contains(':'))
            throw new ArgumentException(L.T("core.common.invalidPath", rel));
        var parts = rel.Replace('/', '\\').Split('\\');
        if (parts.Any(p => p is "" or "." or ".."))
            throw new ArgumentException(L.T("core.common.invalidPath", rel));
        var full = Path.GetFullPath(Path.Combine(_targetRoot, rel));
        if (!IsUnder(full, _targetRoot))
            throw new ArgumentException(L.T("core.transactions.pathOutsideTargetDirectory", rel));
        return full;
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private void Save(TxJournal j)
    {
        j.Updated = DateTimeOffset.UtcNow;
        var path = Path.Combine(_journalDir, j.Id + ".json");
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, j, JsonOpts);
            fs.Flush(flushToDisk: true);
        }
        ReplaceWithRetry(tmp, path);
    }

    /// <summary>
    /// Replaces a file, retrying briefly: antivirus scanners and the search indexer can hold
    /// a just-written journal for a few milliseconds, which makes the rename fail with access denied.
    /// </summary>
    private static void ReplaceWithRetry(string source, string destination)
    {
        const int attempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is UnauthorizedAccessException or IOException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static void FlushFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        fs.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Whether a file can be replaced/deleted (requires write access to the file and its directory).
    /// Non-destructive check: the file is opened for writing without truncation, and a probe file is created and deleted in the directory.
    /// </summary>
    public static bool CanWrite(string full)
    {
        try
        {
            if (File.Exists(full))
                using (new FileStream(full, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                { }
            var dir = Path.GetDirectoryName(full);
            while (dir is not null && !Directory.Exists(dir))
                dir = Path.GetDirectoryName(dir);
            if (dir is null)
                return false;
            var probe = Path.Combine(dir, ".gmm-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (
                new FileStream(
                    probe,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose
                )
            ) { }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static string Hash(string path)
    {
        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1 << 20
        );
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static string? HashOrNull(string path) => File.Exists(path) ? Hash(path) : null;

    private static bool HashEq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class TxAbort(string message) : Exception(message);
}

/// <summary>
/// Detects a running GTA V. The game process always blocks writing. PlayGTAV.exe (Rockstar's bootstrapper)
/// often lingers after the game exits; it counts as a launch only while the game is starting
/// (the first few minutes) or if the game itself is running next to it. Processes from another game folder are ignored.
/// </summary>
public static class GameProcessGuard
{
    public static readonly string[] LegacyProcesses = ["GTA5", "GTA5_BE", "PlayGTAV"];
    public static readonly string[] EnhancedProcesses = ["GTA5_Enhanced", "GTA5_Enhanced_BE", "PlayGTAV"];
    public static readonly TimeSpan LauncherWindow = TimeSpan.FromMinutes(3);

    public static bool AnyRunning(IEnumerable<string> names) => AnyRunning(names, null);

    public static bool AnyRunning(IEnumerable<string> names, string? gameRoot)
    {
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    var path = TryPath(p);
                    // A process from another game installation does not interfere (its path is known and points elsewhere).
                    if (
                        gameRoot is not null
                        && path is not null
                        && !path.StartsWith(gameRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    )
                        continue;
                    if (!name.Equals("PlayGTAV", StringComparison.OrdinalIgnoreCase))
                        return true;
                    // A lingering PlayGTAV without the game blocks only during the launch window.
                    if (TryStart(p) is not { } started || DateTime.Now - started < LauncherWindow)
                        return true;
                }
            }
        }
        return false;
    }

    private static string? TryPath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTime? TryStart(Process p)
    {
        try
        {
            return p.StartTime;
        }
        catch (Exception ex)
            when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
