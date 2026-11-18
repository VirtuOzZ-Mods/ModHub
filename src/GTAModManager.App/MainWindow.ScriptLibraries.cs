using System.IO;
using System.Net.Http;
using GTAModManager.App.Discover;
using GTAModManager.Core.Base;
using GTAModManager.Core.Game;
using GTAModManager.Core.Installation;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    async Task InstallScriptLibraryAsync(ModCardViewModel card, GameInstall game)
    {
        try
        {
            var component = ScriptLibraries.Get(card.Entry.Id);
            if (!component.Supports(game.Edition))
                throw new InvalidOperationException(L.T("libraries.unsupported", component.Title, game.Edition));
            card.InstallState = L.T("ui.common.downloading");
            var downloads = new ManagedDownloads();
            var receipt = downloads.Begin(
                component.Asset,
                $"https://github.com/{component.Repository}/releases/tag/{component.Tag}",
                game.Root
            );
            using var http = BaseDownloads.CreateClient();
            await BaseDownloads.DownloadScriptLibraryAsync(
                http,
                component.Id,
                Path.GetDirectoryName(receipt.File)!,
                CancellationToken.None
            );
            receipt = downloads.Complete(receipt.Id);
            RememberRecommendationInstall(receipt.File);
            await ShowFilesViewAsync();
            await FilesView.AddAsync([receipt.File], game.Root, receipt.Id, receipt.SourceUrl, component.Id);
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
            System.Windows.MessageBox.Show(this, L.Owned(ex.Message), card.Title);
        }
        finally
        {
            card.InstallState = null;
        }
    }
}
