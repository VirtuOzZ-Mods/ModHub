using System.IO;
using System.Windows;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using Microsoft.Win32;

namespace GTAModManager.App;

/// <summary>Backup setup prompt: folder plus size limit, or opt out. Saves the choice to AppSettings.</summary>
public partial class BackupSetupWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _gameRoots;
    private bool _folderLocked;

    public BackupSetupWindow(AppSettings settings, IReadOnlyList<string> gameRoots)
    {
        InitializeComponent();
        _settings = settings;
        _gameRoots = gameRoots;

        var lib = settings.OpenLibrary();
        if (lib is not null)
        {
            FolderBox.Text = lib.Root;
            var gb = lib.QuotaBytes / (double)(1L << 30);
            if (gb >= 1)
            {
                QuotaBox.Text = gb.ToString("0.##");
                UnitGb.IsChecked = true;
            }
            else
            {
                QuotaBox.Text = (lib.QuotaBytes >> 20).ToString();
                UnitMb.IsChecked = true;
            }
            // The folder cannot change while it holds live backups: earlier installs would lose the ability to be undone.
            _folderLocked = lib.Records().Any(r => !r.Undone);
            if (_folderLocked)
            {
                FolderBox.IsEnabled = false;
                BrowseButton.IsEnabled = false;
            }
        }
        else
        {
            // Default suggestion: next to the game, in the ModHub service folder on the game's drive
            // (X:\ModHub\Backups). Without a game: the drive with the most free space, preferably not the system drive.
            string? driveRoot = gameRoots.Select(Path.GetPathRoot).FirstOrDefault(r => !string.IsNullOrEmpty(r));
            driveRoot ??= DriveInfo
                .GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderBy(d => d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault()
                ?.RootDirectory.FullName;
            FolderBox.Text =
                driveRoot is null
                    ? ""
                    : Path.Combine(driveRoot, GTAModManager.Core.Game.WorkDirMigration.FolderName, "Backups");
        }
        if (settings.BackupsDeclined)
            NoOption.IsChecked = true;
        Validate();
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (YesPanel is null)
            return;
        YesPanel.Visibility = YesOption.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    private void OnInputChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            Validate();
    }

    private async void OnPruneBackups(object sender, RoutedEventArgs e) => await MaintainBackupsAsync(false);

    private async void OnResetBackups(object sender, RoutedEventArgs e) => await MaintainBackupsAsync(true);

    private async Task MaintainBackupsAsync(bool reset)
    {
        try
        {
            var library = BackupLibrary.TryOpen(FolderBox.Text.Trim())
                ?? throw new IOException(L.T("backup.manage.unavailable"));
            if (
                reset
                && MessageBox.Show(
                    this,
                    L.T("backup.manage.confirm", library.Root),
                    L.T("backup.manage.reset"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No
                ) != MessageBoxResult.Yes
            )
                return;
            IsEnabled = false;
            PruneBackupsButton.IsEnabled = ResetBackupsButton.IsEnabled = SaveButton.IsEnabled = false;
            long freed = await Task.Run(() =>
            {
                if (!reset)
                    return library.PruneUnused();
                long bytes = library.UsedBytes();
                library.Reset();
                return bytes;
            });
            _folderLocked = library.Records().Any(r => !r.Undone);
            FolderBox.IsEnabled = BrowseButton.IsEnabled = !_folderLocked;
            Validate();
            MessageBox.Show(
                this,
                L.T(reset ? "backup.manage.resetDone" : "backup.manage.pruned", BackupLibrary.FormatSize(freed)),
                L.T("ui.common.done")
            );
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(
                this,
                L.Owned(ex.Message),
                L.T("ui.backupSetupWindow.modBackups"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        finally
        {
            IsEnabled = true;
            PruneBackupsButton.IsEnabled = ResetBackupsButton.IsEnabled = true;
            Validate();
        }
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = L.T("ui.backupSetupWindow.backupFolderCanCreateOne"),
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : Path.GetPathRoot(FolderBox.Text),
        };
        if (dlg.ShowDialog(this) == true)
            FolderBox.Text = dlg.FolderName;
    }

    private long? Quota()
    {
        var unit = UnitMb.IsChecked == true ? "MB" : "GB";
        return BackupLibrary.ParseSize(QuotaBox.Text + unit);
    }

    /// <summary>Validates the input; returns an error message or null.</summary>
    private string? Validate()
    {
        string? error = null;
        if (YesOption.IsChecked == true)
        {
            var path = FolderBox.Text.Trim();
            string? root = null;
            try
            {
                root = Path.IsPathFullyQualified(path) ? Path.GetPathRoot(path) : null;
            }
            catch (ArgumentException) { }
            if (root is null)
                error = L.T("ui.backupSetupWindow.enterFullFolderPathExample");
            else if (
                _gameRoots.Any(g =>
                    Path.GetFullPath(path).StartsWith(g.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        Path.GetFullPath(path).TrimEnd('\\'),
                        g.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
                error = L.T("ui.backupSetupWindow.backupFolderCannotInsideGame");

            long free = 0;
            if (root is not null)
            {
                try
                {
                    free = new DriveInfo(root).AvailableFreeSpace;
                }
                catch (Exception) { }
                FolderInfo.Text = L.T(
                    "ui.backupSetupWindow.text",
                    (
                        Directory.Exists(path)
                            ? L.T("ui.backupSetupWindow.folderExists")
                            : L.T("ui.backupSetupWindow.folderWillCreated")
                    ),
                    (free > 0 ? L.T("ui.backupSetupWindow.freeDiskSpace", BackupLibrary.FormatSize(free)) : "")
                );
            }

            var q = Quota();
            if (q is null)
                error ??= L.T("ui.backupSetupWindow.limitMustNumberExample25");
            else if (q < 50L << 20)
                error ??= L.T("ui.backupSetupWindow.limitMustAtLeast50");
            else
                QuotaInfo.Text =
                    free > 0 && q > free
                        ? L.T("ui.backupSetupWindow.limitExceedsAvailableSpaceBackups", BackupLibrary.FormatSize(free))
                        : L.T(
                            "ui.backupSetupWindow.onlyChangedFilesStoredIdentical",
                            BackupLibrary.FormatSize(q.Value)
                        );
            if (_folderLocked)
                FolderInfo.Text = L.T("ui.backupSetupWindow.folderContainsBackupsActiveInstallations");
        }
        ErrorBox.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? "";
        SaveButton.IsEnabled = error is null;
        return error;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Validate() is not null)
            return;
        try
        {
            if (YesOption.IsChecked == true)
            {
                var path = Path.GetFullPath(FolderBox.Text.Trim());
                Directory.CreateDirectory(path);
                var probe = Path.Combine(path, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                // A folder used by an earlier (removed or reinstalled) Mod Hub: offer its saved settings.
                bool restore =
                    !string.Equals(path, _settings.BackupDir, StringComparison.OrdinalIgnoreCase)
                    && SettingsMirror.Exists(path)
                    && MessageBox.Show(
                        this,
                        L.T("ui.backupSetupWindow.restoreSettingsQuestion", path),
                        L.T("ui.common.backups"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    ) == MessageBoxResult.Yes;
                var lib = BackupLibrary.TryOpen(path) ?? BackupLibrary.Create(path, Quota()!.Value);
                lib.SetQuota(Quota()!.Value);
                if (restore)
                {
                    SettingsMirror.Restore(path);
                    MessageBox.Show(
                        this,
                        L.T("ui.backupSetupWindow.settingsRestoredRestart"),
                        L.T("ui.common.backups"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information
                    );
                    App.RestartLater();
                    return;
                }
                _settings.BackupDir = lib.Root;
                _settings.BackupsDeclined = false;
            }
            else
            {
                _settings.BackupDir = null;
                _settings.BackupsDeclined = true;
            }
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorBox.Visibility = Visibility.Visible;
            ErrorText.Text = L.T("ui.backupSetupWindow.couldNotUseFolder", L.Owned(ex.Message));
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => DialogResult = false;
}
