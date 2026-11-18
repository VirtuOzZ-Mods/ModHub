using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Installation;

/// <summary>The queue approved once under UAC: what may be installed and where the commands come from.</summary>
public sealed class BatchQueueRequest
{
    public List<BatchQueueEntry> Items { get; set; } = [];

    /// <summary>Folder the window writes commands to and reads results from.</summary>
    public string ControlDir { get; set; } = "";
}

public sealed class BatchQueueEntry
{
    public string Path { get; set; } = "";

    /// <summary>"mod" or "oiv".</summary>
    public string Verb { get; set; } = "mod";
}

public sealed record BatchQueueResult(int Index, bool Ok, string Message);

/// <summary>
/// One elevated gmm process for a whole install queue, so UAC is asked once rather than for every mod.
/// The window writes numbered command files ("install 3", "undo 3", "stop") and the worker answers each with a
/// result file. A command can only name an entry of the list approved at launch; each entry is installed at most
/// once and only what this worker installed can be undone, so another program writing to the folder cannot get
/// anything else done with administrator rights.
/// </summary>
public static class BatchChannel
{
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(60);

    public static string CommandFile(string dir, int n) => Path.Combine(dir, $"cmd-{n}.txt");

    public static string ResultFile(string dir, int n) => Path.Combine(dir, $"result-{n}.json");

    /// <summary>Worker side: answers commands in order until "stop", an idle hour or an invalid command.</summary>
    public static int Serve(
        BatchQueueRequest request,
        Func<BatchQueueEntry, (bool Ok, string Message, IReadOnlyList<string> Journals)> install,
        Func<IReadOnlyList<string>, (bool Ok, string Message)> undo,
        TimeSpan? idle = null,
        TimeSpan? poll = null
    )
    {
        if (!Directory.Exists(request.ControlDir))
            throw new DirectoryNotFoundException(request.ControlDir);
        var done = new HashSet<int>();
        // Installations made by this worker, per queue entry: the only ones "undo" may take back.
        var journals = new Dictionary<int, IReadOnlyList<string>>();
        for (int n = 1; ; n++)
        {
            var command = WaitForCommand(
                request.ControlDir,
                n,
                idle ?? IdleLimit,
                poll ?? TimeSpan.FromMilliseconds(200)
            );
            if (command is null or "stop")
                return 0;
            BatchQueueResult result;
            var parts = command.Split(' ', 2);
            int index = parts.Length == 2 && int.TryParse(parts[1], out int i) ? i : -1;
            if (parts[0] == "install" && index >= 0 && index < request.Items.Count && done.Add(index))
            {
                (bool ok, string message, var made) = install(request.Items[index]);
                journals[index] = made;
                result = new(index, ok, message);
            }
            else if (parts[0] == "undo" && journals.Remove(index, out var made))
            {
                (bool ok, string message) = undo(made);
                result = new(index, ok, message);
            }
            else
            {
                Write(request.ControlDir, n, new(-1, false, L.T("core.batch.invalidCommand", command)));
                return 1;
            }
            Write(request.ControlDir, n, result);
        }
    }

    private static string? WaitForCommand(string dir, int n, TimeSpan idle, TimeSpan poll)
    {
        var file = CommandFile(dir, n);
        var deadline = DateTime.UtcNow + idle;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(file))
                try
                {
                    return File.ReadAllText(file).Trim();
                }
                catch (IOException)
                {
                    // Still being written by the window.
                }
            Thread.Sleep(poll);
        }
        return null;
    }

    private static void Write(string dir, int n, BatchQueueResult result)
    {
        // Written aside and moved, so the window never reads half a file.
        var temp = ResultFile(dir, n) + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result));
        File.Move(temp, ResultFile(dir, n), true);
    }

    /// <summary>
    /// Window side: sends command number <paramref name="n"/> and waits for its result. Null when the worker ended
    /// without answering (UAC declined, crashed); <paramref name="workerAlive"/> tells when to stop waiting.
    /// </summary>
    public static async Task<BatchQueueResult?> SendAsync(
        string dir,
        int n,
        string command,
        Func<bool> workerAlive,
        CancellationToken cancel = default
    )
    {
        var temp = CommandFile(dir, n) + ".tmp";
        await File.WriteAllTextAsync(temp, command, cancel);
        File.Move(temp, CommandFile(dir, n), true);
        if (command == "stop")
            return null;
        var file = ResultFile(dir, n);
        while (true)
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<BatchQueueResult>(await File.ReadAllTextAsync(file, cancel));
            // The worker may have answered just before exiting: look once more before giving up.
            if (!workerAlive())
                return File.Exists(file)
                    ? JsonSerializer.Deserialize<BatchQueueResult>(await File.ReadAllTextAsync(file, cancel))
                    : null;
            await Task.Delay(200, cancel);
        }
    }
}
