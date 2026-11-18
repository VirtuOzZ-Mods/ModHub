using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace GTAModManager.App;

/// <summary>
/// Single instance: if a window is already open, an .oiv path from Explorer is passed to it
/// through a named pipe, and the second instance exits immediately.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "GTAModManager.SingleInstance";
    private static readonly string PipeName = InstanceName + "." + Environment.UserName;
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CatchUnexpectedErrors();
#if DEBUG
        // Debug builds copy old data instead of moving it, so a release build can still pick it up.
        GTAModManager.Core.AppDataMigration.Run(deleteSources: false);
#else
        GTAModManager.Core.AppDataMigration.Run();
#endif
        GTAModManager.Core.AppPaths.UseOwnTemp();
        var localization = GTAModManager.Core.Localization.LocalizationService.Current;
        localization.Reload(GTAModManager.Core.AppPaths.Languages);
        // First run (no saved choice): English. The user picks another language in Settings.
        localization.Select(GTAModManager.Core.Library.AppSettings.Load().LanguagePackId ?? "official.en");
        GTAModManager.App.Localization.LiveLocalization.Enable();
        var oiv = e.Args.FirstOrDefault(GTAModManager.App.MainWindow.IsSupportedFile);

        _mutex = new Mutex(true, InstanceName + "." + Environment.UserName, out bool first);
        if (!first)
        {
            if (TrySendToRunning(oiv ?? ""))
            {
                Shutdown();
                return;
            }
            // The first instance is not responding: continue as usual.
        }

        var window = new MainWindow();
        window.InitializeLocalizationSettings();
        MainWindow = window;
        window.Show();
        _pipeCts = new CancellationTokenSource();
        _ = ListenAsync(window, _pipeCts.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCts?.Cancel();
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException) { }
        GTAModManager.Core.AppPaths.CleanTemp();
        if (!GTAModManager.App.Uninstall.UninstallFlow.Started)
            GTAModManager.Core.Library.AppSettings.Load().MirrorToBackups();
        base.OnExit(e);
    }

    /// <summary>
    /// An unexpected error must not close Mod Hub silently: the user sees what happened and where the report is.
    /// Errors on the UI thread are reported and the app keeps running (game changes are transactions, so an
    /// interrupted step is rolled back by the journal). Errors on other threads cannot be stopped by .NET from
    /// ending the process, but they are still written to Data\Logs and shown first.
    /// </summary>
    private void CatchUnexpectedErrors()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportUnexpected(args.Exception, fatal: false);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ReportUnexpected(ex, fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            WriteCrashReport(args.Exception);
        };
    }

    private static int _reporting;

    private static void ReportUnexpected(Exception ex, bool fatal)
    {
        string? report = WriteCrashReport(ex);
        // One dialog at a time: a failure inside a repeated event (rendering, timers) must not stack windows.
        if (Interlocked.Exchange(ref _reporting, 1) == 1)
            return;
        try
        {
            string text;
            try
            {
                text = GTAModManager.Core.Localization.L.T(
                    fatal ? "ui.errors.fatal" : "ui.errors.unexpected",
                    GTAModManager.Core.Localization.L.Owned(ex.Message),
                    report ?? "—"
                );
            }
            catch (Exception)
            {
                text = $"Unexpected error: {ex.Message}\n\nReport: {report ?? "—"}";
            }
            var owner = Current?.MainWindow is { IsLoaded: true } window ? window : null;
            if (owner is not null)
                MessageBox.Show(owner, text, "Mod Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                MessageBox.Show(text, "Mod Hub", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception) { } // Showing the error must never raise another one.
        finally
        {
            Interlocked.Exchange(ref _reporting, 0);
        }
    }

    /// <summary>Writes Data\Logs\crash-*.txt; returns its path, or null when even that failed.</summary>
    private static string? WriteCrashReport(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(GTAModManager.Core.AppPaths.Logs);
            string path = Path.Combine(
                GTAModManager.Core.AppPaths.Logs,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt"
            );
            File.WriteAllText(
                path,
                $"{GTAModManager.App.MainWindow.BuildLabel()}\n{DateTime.Now:O}\n{Environment.OSVersion}\n\n{ex}\n"
            );
            // Keep the folder small: the 20 newest reports are enough for a bug report.
            foreach (var old in new DirectoryInfo(GTAModManager.Core.AppPaths.Logs)
                .GetFiles("crash-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(20))
                old.Delete();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes Mod Hub and starts it again a few seconds later (after this instance has released the single-instance
    /// mutex). cmd.exe runs the delay; nothing is written to disk.
    /// </summary>
    public static void RestartLater()
    {
        var exe = Environment.ProcessPath!;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (exe.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!']) < 0)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(Path.Combine(system, "cmd.exe"))
                {
                    Arguments = $"/d /s /c \"ping -n 4 127.0.0.1 >nul & start \"\" \"{exe}\"\"",
                    WorkingDirectory = system,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
        Current.Shutdown();
    }

    private static bool TrySendToRunning(string path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client);
            w.WriteLine(path);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task ListenAsync(MainWindow window, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous
                );
                await server.WaitForConnectionAsync(ct);
                using var r = new StreamReader(server);
                var path = (await r.ReadLineAsync(ct))?.Trim() ?? "";
                await Dispatcher.InvokeAsync(async () => await window.OpenPackageFromShellAsync(path));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }
}
