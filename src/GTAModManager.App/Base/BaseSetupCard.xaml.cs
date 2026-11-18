using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Base;

/// <summary>One visible setup step at a time; the selected game is captured before downloads or UAC.</summary>
public partial class BaseSetupCard : UserControl
{
    GameInstall? game;
    bool busy;
    public Func<string[], Task<(int code, string output)>>? RunGmm { get; set; }
    public event Action? Installed;
    public event Action<bool, string>? BusyChanged;
    public event Action<Uri, string>? OpenRequested;

    public BaseSetupCard() => InitializeComponent();

    /// <summary>RageOpenV from its authors' GitHub release (pinned, SHA-256 checked); Mod Hub does not ship it.</summary>
    public static async Task<string> DownloadRageOpenVAsync(string folder)
    {
        using var http = BaseDownloads.CreateClient();
        return await BaseDownloads.DownloadRageOpenVAsync(http, folder, CancellationToken.None);
    }

    public void Refresh(GameInstall? selected)
    {
        game = selected;
        if (selected is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        if (new ModModeService(selected).Inspect().WritesBlocked)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        var status = ModdingBase.Inspect(selected);
        var stage = ModdingBase.NextSetupStage(status);
        Visibility = stage == BaseSetupStage.Complete ? Visibility.Collapsed : Visibility.Visible;
        if (stage == BaseSetupStage.Complete)
            return;
        HeadingText.Text = L.T("ui.base.modComponentsGtaV", selected.Edition);
        Rows.Children.Clear();
        ShvdnChoice.Visibility =
            stage == BaseSetupStage.ScriptHookVDotNet && selected.Edition == GameEdition.Legacy
                ? Visibility.Visible
                : Visibility.Collapsed;
        if (ShvdnNightly.IsChecked != true && ShvdnEnhanced.IsChecked != true)
            ShvdnNightly.IsChecked = true;
        SourceButton.Visibility = stage == BaseSetupStage.ModsLoader ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.Content = stage switch
        {
            BaseSetupStage.ModsLoader => L.T(
                status.ModsLoaderUpdateAvailable ? "ui.base.updateRageopenv" : "ui.base.installRageopenv"
            ),
            BaseSetupStage.ScriptHookV => L.T("ui.base.installScriptHookV"),
            _ => L.T("ui.base.installScripthookvdotnet"),
        };
        DescriptionText.Text = stage switch
        {
            BaseSetupStage.ModsLoader => L.T(
                status.ModsLoaderUpdateAvailable
                    ? "ui.base.rageopenvUpdateDescription"
                    : "ui.base.rageopenvAsiNotInstalledAllows"
            ),
            BaseSetupStage.ScriptHookV => L.T("ui.base.scriptModsRequireScriptHook"),
            _ => L.T("ui.base.scripthookvdotnetNotInstalledModsNet"),
        };
        if (stage == BaseSetupStage.ModsLoader)
            Row(L.T("ui.base.afterInstallationWeLlSuggest"));
        else
        {
            if (!status.ScriptHookV)
                Row(L.T("ui.base.scriptHookVNotInstalled"));
            if (!status.AsiLoader)
                Row(L.T("ui.base.asiLoaderNotInstalledIncluded"));
            if (!status.Shvdn)
                Row(L.T("ui.base.scripthookvdotnetNotInstalled"));
        }
        InstallButton.IsEnabled = !busy;
        if (!busy)
            StatusText.Text = status.GameVersion is null ? "" : L.T("ui.base.gameBuild", status.GameVersion);
    }

    void Row(string text) =>
        Rows.Children.Add(
            new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            }
        );

    ShvdnVariant Variant(GameInstall target) =>
        target.Edition == GameEdition.Enhanced || ShvdnEnhanced.IsChecked == true
            ? ShvdnVariant.Enhanced
            : ShvdnVariant.Nightly;

    void OnSource(object sender, RoutedEventArgs e)
    {
        if (game is not { } target)
            return;
        var stage = ModdingBase.NextSetupStage(ModdingBase.Inspect(target));
        var url =
            stage == BaseSetupStage.ScriptHookV
                ? BaseDownloads.ScriptHookPage
                : new Uri("https://github.com/" + ModdingBase.ShvdnRepo(Variant(target)) + "/releases");
        OpenRequested?.Invoke(url, target.Root);
    }

    async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (busy || game is not { } target || RunGmm is null)
            return;
        var owner = Window.GetWindow(this);
        var status = ModdingBase.Inspect(target);
        var stage = ModdingBase.NextSetupStage(status);
        if (stage == BaseSetupStage.Complete)
        {
            Refresh(game);
            return;
        }
        if (target.IsRunning())
        {
            MessageBox.Show(owner, L.T("ui.common.closeGtaVBeforeInstalling"));
            return;
        }
        string title = stage switch
        {
            BaseSetupStage.ModsLoader => "RageOpenV",
            BaseSetupStage.ScriptHookV => "Script Hook V + ASI Loader",
            _ => "ScriptHookVDotNet",
        };
        string source =
            stage == BaseSetupStage.ModsLoader ? L.T("ui.base.fixedRageopenvAsiBundledModhub")
            : stage == BaseSetupStage.ScriptHookV ? L.T("ui.base.scriptHookVWillDownloaded")
            : L.T("ui.base.selectedScripthookvdotnetWillDownloadedFrom");
        if (
            MessageBox.Show(
                owner,
                L.T("ui.base.installGtaV", source, title, target.Edition, target.Root),
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            ) != MessageBoxResult.Yes
        )
            return;
        busy = true;
        InstallButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        var folder = Path.Combine(Path.GetTempPath(), "ModHub", "base-" + Guid.NewGuid().ToString("N"));
        var files = new List<string>();
        bool success = false;
        try
        {
            var args = new List<string> { "base-install", target.Root };
            if (stage == BaseSetupStage.ModsLoader)
            {
                StatusText.Text = L.T("ui.base.downloadingRageopenv");
                args.AddRange(["--rageopenv", await DownloadRageOpenVAsync(folder)]);
            }
            else
            {
                using var http = BaseDownloads.CreateClient();
                if (stage == BaseSetupStage.ScriptHookV)
                {
                    StatusText.Text = L.T("ui.base.downloadingScriptHookVFrom");
                    var zip = await BaseDownloads.DownloadScriptHookAsync(http, folder, CancellationToken.None);
                    files.Add(zip);
                    if (!ModdingBase.ScriptHookSupports(zip, status.GameVersion))
                        throw new InvalidOperationException(L.T("ui.base.scriptHookVDoesNot", status.GameVersion));
                    args.AddRange(["--shv", zip]);
                }
                else
                {
                    var variant = Variant(target);
                    StatusText.Text = L.T("ui.base.downloadingScripthookvdotnetFromOfficialRelease");
                    var zip = await BaseDownloads.DownloadShvdnAsync(http, variant, folder, CancellationToken.None);
                    files.Add(zip);
                    args.AddRange(["--shvdn", variant == ShvdnVariant.Nightly ? "nightly" : "enhanced", zip]);
                }
            }
            StatusText.Text = L.T("ui.base.installingGtaVApproveAdministrator", target.Edition);
            BusyChanged?.Invoke(true, L.T("ui.base.installing", title));
            var result = await RunGmm(args.ToArray());
            if (result.code != 0)
                throw new InvalidOperationException(result.output.Trim());
            success = true;
            foreach (var file in files)
                File.Delete(file);
        }
        catch (Exception ex)
            when (ex
                    is IOException
                        or HttpRequestException
                        or InvalidOperationException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or System.Text.Json.JsonException
                        or OperationCanceledException
            )
        {
            MessageBox.Show(
                owner,
                success
                    ? L.T("ui.base.installedDownloadedArchiveHasBeen", title, L.Owned(ex.Message))
                    : L.T("ui.base.couldNotInstall", title, L.Owned(ex.Message)),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        finally
        {
            busy = false;
            Progress.Visibility = Visibility.Collapsed;
            BusyChanged?.Invoke(false, "");
            Refresh(game);
            Installed?.Invoke();
            if (success && game?.Root.Equals(target.Root, StringComparison.OrdinalIgnoreCase) == true)
                StatusText.Text = L.T("ui.base.installed", title);
            // Only our own successfully used files are deleted; failed downloads remain for inspection.
            try
            {
                if (success && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    Directory.Delete(folder);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
