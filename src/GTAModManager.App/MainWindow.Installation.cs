using System.Windows;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    void InitializeInstallation()
    {
        RefreshArchiveSettings();
        FilesView.JobCompleted += (_, job) => FinishRecommendationInstall(job.Source);
        FilesView.RunGmm = args => RunGmmAsync(args);
        FilesView.GameSelected += game =>
            (game.Edition == Core.Game.GameEdition.Legacy ? LegacyOption : EnhancedOption).IsChecked = true;
        FilesView.BusyChanged += (busy, title) => SetBusy(busy, title, L.T("ui.installation.doNotStartGameUntil"));
        FilesView.CancelDownloadRequested += id => BrowserView.CancelDownload(id);
        FilesView.Changed += () =>
        {
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
            FilesView.BaseCard.Refresh(Current);
        };
        FilesView.OpenRequested += async (uri, root) =>
        {
            BrowserView.DownloadGameRoot = root;
            BrowserView.OriginUrl = uri.AbsoluteUri;
            FilesHost.Visibility = Visibility.Collapsed;
            BrowserHost.Visibility = Visibility.Visible;
            if (!await BrowserView.OpenAsync(uri))
            {
                BrowserHost.Visibility = Visibility.Collapsed;
                FilesHost.Visibility = Visibility.Visible;
                Discover.BrowserView.OpenExternal(uri);
            }
        };
        BrowserView.DownloadStarted += r => FilesView.DownloadChanged(r);
        BrowserView.DownloadFailed += r => FilesView.DownloadChanged(r, true);
        BrowserView.DownloadCompleted += async r =>
        {
            if (DiscoverView.FindEntry(null, r.SourceUrl) is not null)
                RememberRecommendationInstall(r.File);
            if (await TryNaturalVisionAsync(r.File, r))
            {
                FilesView.HandOffDownload(r);
                return;
            }
            await ShowFilesViewAsync();
            if (!EnsureReady())
                return;
            await FilesView.DownloadCompletedAsync(r);
        };
        BrowserView.CloseRequested += () =>
        {
            if (InstallTab.IsChecked == true)
                InstallPage.Visibility = Visibility.Visible;
        };
    }

    void RefreshArchiveSettings()
    {
        var settings = Core.Library.AppSettings.Load();
        ArchiveKeepCheck.IsChecked = settings.KeepDownloadedArchives;
        ArchiveFolderInfo.Text = settings.ArchiveLibrary ?? L.T("ui.installation.ifNoFolderSelectedSaved");
    }

    void OnArchiveRetentionChanged(object sender, RoutedEventArgs e)
    {
        var settings = Core.Library.AppSettings.Load();
        settings.KeepDownloadedArchives = ArchiveKeepCheck.IsChecked == true;
        settings.Save();
        _settings = settings;
    }

    void OnChooseArchiveLibrary(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.T("ui.installation.downloadedArchiveLibrary") };
        if (dialog.ShowDialog(this) != true)
            return;
        var settings = Core.Library.AppSettings.Load();
        settings.ArchiveLibrary = dialog.FolderName;
        settings.Save();
        _settings = settings;
        RefreshArchiveSettings();
    }
}
