using System.Windows;
using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>
/// Write access to the game folder: verified with a real write (at startup, on folder selection, before installing)
/// and a Fix button that grants Modify to the current user through gmm under UAC.
/// </summary>
public partial class MainWindow
{
    private async Task RefreshPermissionsAsync(GameInstall? game)
    {
        if (game is null)
        {
            PermissionBanner.Visibility = Visibility.Collapsed;
            return;
        }
        IReadOnlyList<string> failed;
        try
        {
            failed = await Task.Run(() => GamePermissions.UnwritableFolders(game.Root));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (Current?.Root != game.Root)
            return; // another game was selected while we were checking
        PermissionBanner.Visibility = failed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFixPermissions(object sender, RoutedEventArgs e)
    {
        if (Current is not { } game)
            return;
        const string title = "Mod Hub by VirtuOzZ";
        if (GamePermissions.WhyRefused(game.Root) is { } why)
        {
            MessageBox.Show(this, why, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string sid,
            user;
        try
        {
            sid = GamePermissions.CurrentUserSid();
            user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, L.Owned(ex.Message), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (
            MessageBox.Show(
                this,
                L.T("ui.permissions.folderWindowsUserWillGranted", game.Root, user),
                L.T("ui.permissions.gameFolderPermissions"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;

        PermissionFixButton.IsEnabled = false;
        SetBusy(
            true,
            L.T("ui.permissions.gameFolderPermissions"),
            L.T("ui.permissions.grantingModifyPermissionGameFolder")
        );
        int code;
        string output;
        try
        {
            (code, output) = await RunGmmAsync("fix-permissions", game.Root, sid);
        }
        finally
        {
            SetBusy(false);
            PermissionFixButton.IsEnabled = true;
        }

        // Declining the UAC prompt is not a failure: nothing changed and the warning stays.
        if (code == 1223)
        {
            await RefreshPermissionsAsync(game);
            return;
        }

        var failed = await Task.Run(() => GamePermissions.UnwritableFolders(game.Root));
        await RefreshPermissionsAsync(game);
        if (failed.Count == 0)
            MessageBox.Show(
                this,
                L.T("ui.permissions.doneGameFolderNowWritable"),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        else
            MessageBox.Show(
                this,
                L.T("ui.permissions.couldNotMakeGameFolder", string.Join("\n", failed), output.Trim()),
                title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
    }
}
