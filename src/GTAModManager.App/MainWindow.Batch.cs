using System.IO;
using System.Windows;
using GTAModManager.App.Installation;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    /// <summary>
    /// Several mods dropped at once: analyze them, put the prevailing kind first and install them one by one through
    /// gmm (each is its own installation with its own undo), as the batch window drives it.
    /// </summary>
    async Task InstallBatchAsync(IReadOnlyList<string> paths)
    {
        var g = Current;
        if (g is null || !CanChangePackages(g))
            return;
        if (g.IsRunning())
        {
            RunningBanner.Visibility = Visibility.Visible;
            return;
        }
        if (_settings.BackupChoicePending && !ShowBackupSetup())
            return;
        if (_settings.BackupDir is not null && _settings.OpenLibrary() is null)
        {
            MessageBox.Show(
                this,
                L.T("ui.main.backupFolderUnavailableConnectDrive", _settings.BackupDir),
                L.T("ui.common.backups"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        BatchPlan plan;
        SetBusy(true, L.T("ui.batch.analyzing", paths.Count), "");
        try
        {
            plan = await Task.Run(() =>
                DropBatch.Plan(paths, g, (i, path) =>
                    Dispatcher.BeginInvoke(() =>
                        StatusText.Text = L.T("ui.batch.analyzingItem", i + 1, paths.Count, Path.GetFileName(path))
                    )
                )
            );
        }
        finally
        {
            SetBusy(false);
        }

        _ = RefreshPermissionsAsync(g);
        await using var runner = new QueueRunner(g.Root, plan.Items);
        var window = new BatchInstallWindow(this, plan, runner);
        window.ShowDialog();
        RefreshHistory();
        RefreshBackupInfo();
        RefreshGameControls();
        if (window.Installed > 0)
        {
            StatusText.Text = L.T("ui.batch.statusInstalled", window.Installed, plan.Items.Count);
            _ = MyModsView.ScanAsync(g, force: true);
        }
        if (window.OpenSeparately is { } item)
            await AnalyzeAsync(item.Path);
    }

    /// <summary>
    /// The window's side of "gmm mod-queue": the worker starts on the first install (one UAC prompt for the whole
    /// queue) and receives "install N" / "undo N" commands; it can only act on the items listed here.
    /// </summary>
    private sealed class QueueRunner : IBatchRunner, IAsyncDisposable
    {
        private readonly string _root;
        private readonly IReadOnlyList<BatchItem> _items;
        private readonly string _dir = Path.Combine(
            Path.GetTempPath(),
            "ModHub",
            "queue-" + Guid.NewGuid().ToString("N")
        );
        private int _session;

        // Each worker gets its own folder: command numbers start over and nothing of an ended worker is reused.
        private string Channel => Path.Combine(_dir, _session.ToString());
        private Task<(int code, string output)>? _worker;
        private int _command;

        public QueueRunner(string root, IReadOnlyList<BatchItem> items)
        {
            _root = root;
            _items = items;
        }

        private void Start()
        {
            _session++;
            _command = 0;
            Directory.CreateDirectory(Channel);
            var request = Path.Combine(Channel, "request.json");
            File.WriteAllText(
                request,
                System.Text.Json.JsonSerializer.Serialize(
                    new BatchQueueRequest
                    {
                        ControlDir = Channel,
                        Items = _items.Select(i => new BatchQueueEntry { Path = i.Path, Verb = i.Verb }).ToList(),
                    }
                )
            );
            _worker = RunGmmAsync("mod-queue", _root, request);
        }

        private async Task<(BatchQueueResult? Result, string Ended)> SendAsync(string command)
        {
            // A worker that ended (idle for an hour during a long pause) is started again for the next install;
            // what it installed earlier can no longer be undone from here, only from History.
            if (_worker is null || _worker.IsCompleted && command.StartsWith("install", StringComparison.Ordinal))
                Start();
            var result = await BatchChannel.SendAsync(Channel, ++_command, command, () => !_worker!.IsCompleted);
            if (result is not null)
                return (result, "");
            // No answer: the worker is gone (UAC declined, crashed); its output says why.
            var (_, output) = await _worker!;
            return (null, output.Trim().Length > 0 ? output.Trim() : L.T("ui.batch.workerEnded"));
        }

        public async Task<(bool Ok, string Message, bool Fatal)> InstallAsync(BatchItem item)
        {
            var (result, ended) = await SendAsync("install " + Index(item));
            return result is null ? (false, ended, true) : (result.Ok, result.Message, false);
        }

        public async Task<(bool Ok, string Message)> UndoAsync(BatchItem item)
        {
            var (result, ended) = await SendAsync("undo " + Index(item));
            return result is null ? (false, ended) : (result.Ok, result.Message);
        }

        private int Index(BatchItem item) => _items.Select((x, i) => (x, i)).First(p => ReferenceEquals(p.x, item)).i;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_worker is { IsCompleted: false })
                {
                    await BatchChannel.SendAsync(Channel, ++_command, "stop", () => false);
                    await _worker;
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(_dir, true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
