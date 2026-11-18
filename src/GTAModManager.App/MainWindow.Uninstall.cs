using System.IO;
using System.Windows;
using GTAModManager.App.Uninstall;
using GTAModManager.Core;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using GTAModManager.Core.Peds;

namespace GTAModManager.App;

/// <summary>
/// Settings → Remove Mod Hub: optionally cleans the games, keeps or deletes the backups (with the service folders
/// next to the games and a copy of the settings), drops the .oiv association and deletes the program folder on exit.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// "Mod Hub 2026.11.17-2035 · 881ed7b": the label scripts\publish-portable.ps1 stamps into the build
    /// (a trailing + means it was built with uncommitted changes). Developer builds show the plain version.
    /// </summary>
    public static string BuildLabel()
    {
        var info = typeof(MainWindow)
            .Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()
            ?.InformationalVersion;
        return "Mod Hub " + (info ?? typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?");
    }

    private void OnDeveloperGuide(object sender, RoutedEventArgs e)
    {
        try
        {
            Docs.DeveloperGuideWindow.Show(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.Owned(ex.Message), L.T("ui.devDocs.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
            return;
        var games = _games.Values.ToList();
        if (games.FirstOrDefault(g => g.IsRunning()) is { } running)
        {
            MessageBox.Show(this, L.T("ui.uninstall.closeGame", running.Edition), L.T("ui.uninstall.title"));
            return;
        }

        var cleanup = UninstallFlow.AskGameCleanup(this);
        if (cleanup == GameCleanup.Cancel)
            return;
        if (cleanup == GameCleanup.Keep)
        {
            // Mods switched off by the toggle stay renamed (*.gmm-off) in the game: turn them back on first.
            var off = games.Where(g => new ModModeService(g).Inspect().State != ModModeState.Modded).ToList();
            if (off.Count > 0)
            {
                MessageBox.Show(
                    this,
                    L.T("ui.uninstall.modsSwitchedOff", string.Join(", ", off.Select(g => g.Edition))),
                    L.T("ui.uninstall.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }
        }

        var settings = AppSettings.Load();
        var serviceFolders = games.Select(g => g.WorkDir).Where(Directory.Exists).Distinct().ToList();
        var backupFolders = (settings.BackupDir is { } b && Directory.Exists(b) ? [b] : new List<string>())
            .Concat(serviceFolders)
            .ToList();
        bool keepBackups = true;
        if (backupFolders.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                L.T("ui.uninstall.keepBackupsQuestion", string.Join("\n", backupFolders)),
                L.T("ui.uninstall.title"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Yes
            );
            if (answer == MessageBoxResult.Cancel)
                return;
            keepBackups = answer == MessageBoxResult.Yes;
        }
        if (
            keepBackups
            && settings.BackupDir is { } inside
            && Path.GetFullPath(inside).StartsWith(AppPaths.Root + "\\", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Backups of old portable builds live in Data\Backups and would go away with the program folder.
            MessageBox.Show(
                this,
                L.T("ui.uninstall.backupsInsideProgram", inside),
                L.T("ui.uninstall.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            return;
        }

        var programFolders = UninstallFlow.FoldersToRemove();
        var confirm = MessageBox.Show(
            this,
            L.T(
                "ui.uninstall.confirm",
                cleanup switch
                {
                    GameCleanup.Vanilla => L.T("ui.uninstall.vanilla"),
                    GameCleanup.OwnOnly => L.T("ui.uninstall.ownOnly"),
                    _ => L.T("ui.uninstall.keepGame"),
                },
                keepBackups ? L.T("ui.uninstall.backupsKept") : L.T("ui.uninstall.backupsDeleted"),
                string.Join("\n", programFolders)
            ),
            L.T("ui.uninstall.title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel
        );
        if (confirm != MessageBoxResult.OK)
            return;

        // 1. Games. A failed step stops removal: Mod Hub stays so the user can retry or undo.
        SetBusy(true, L.T("ui.uninstall.title"), L.T("ui.uninstall.cleaningGames"));
        try
        {
            foreach (var game in games)
            {
                (int code, string output) result = cleanup switch
                {
                    GameCleanup.Vanilla when keepBackups => await RunGmmAsync("vanilla-restore", game.Root),
                    GameCleanup.Vanilla => await RunGmmAsync("vanilla-restore", game.Root, "--delete"),
                    GameCleanup.OwnOnly
                        when game.Edition == GameEdition.Legacy && new PedSelectorComponent(game).IsInstalled =>
                        await RunGmmAsync("peds-selector-remove", game.Root),
                    _ => (0, ""),
                };
                if (result.code != 0)
                {
                    SetBusy(false);
                    MessageBox.Show(
                        this,
                        L.T("ui.uninstall.gameFailed", game.Edition, L.Owned(result.output.Trim())),
                        L.T("ui.uninstall.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        // 2. Registry, backups and service folders.
        if (FileAssociation.Check() != FileAssociation.State.NotRegistered)
            FileAssociation.Unregister();
        var leftovers = new List<string>();
        if (keepBackups)
            settings.MirrorToBackups();
        else
        {
            foreach (var folder in backupFolders)
                try
                {
                    Directory.Delete(folder, true);
                    var parent = Path.GetDirectoryName(folder);
                    if (
                        parent is not null
                        && Path.GetFileName(parent) == WorkDirMigration.FolderName
                        && !Directory.EnumerateFileSystemEntries(parent).Any()
                    )
                        Directory.Delete(parent);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    leftovers.Add(folder);
                }
        }

        // 3. The program folder, after exit.
        UninstallFlow.Started = true;
        var files = programFolders.Contains(AppPaths.Root) ? [] : new[] { AppPaths.SettingsFile };
        if (!UninstallFlow.ScheduleRemoval(programFolders, files))
            leftovers.AddRange(programFolders);
        if (AppPaths.UsesDocumentsFallback)
            leftovers.Add(AppPaths.ResolveRoot(AppContext.BaseDirectory)); // read-only, e.g. inside Program Files
        MessageBox.Show(
            this,
            leftovers.Count == 0
                ? L.T("ui.uninstall.done")
                : L.T("ui.uninstall.doneWithLeftovers", string.Join("\n", leftovers)),
            L.T("ui.uninstall.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );
        Application.Current.Shutdown();
    }
}
