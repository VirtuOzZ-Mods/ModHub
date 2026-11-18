using System.IO;
using System.Text.Json;
using System.Windows;
using GTAModManager.App.Inventory;
using GTAModManager.Core.Dlc;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

public partial class MainWindow
{
    /// <summary>
    /// Engine sound table from My Mods: all chosen changes (new sounds, originals back) run in one elevated
    /// "engine-sound-batch" call, one transaction per car.
    /// </summary>
    async Task EngineSoundAsync(string? carPack, string? sound)
    {
        var game = Current;
        if (game is null || !CanChangePackages(game))
            return;
        string title = L.T("ui.engineSound.title");
        IReadOnlyList<InstalledPack> packs;
        IReadOnlyList<SoundTarget> stock;
        IReadOnlyDictionary<string, string> originals;
        try
        {
            SetBusy(true, L.T("ui.engineSound.reading"), "");
            (packs, stock, originals) = await Task.Run(() =>
                (EngineSounds.Packs(game), EngineSounds.StockTargets(game), EngineSounds.LoadOriginals(game))
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.Owned(ex.Message), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            SetBusy(false);
        }
        var history = MyModsView.Report?.Groups.SelectMany(g => g.SoundAssignments).ToList() ?? [];
        var batch = EngineSoundWindow.Show(this, game, packs, stock, history, originals, carPack, sound);
        if (batch is null)
            return;

        string directory = Path.Combine(Path.GetTempPath(), "ModHub");
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "engine-sound-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(batch));
            SetBusy(
                true,
                L.T("ui.engineSound.applying", batch.Assign.Count + batch.Undo.Count),
                L.T("ui.common.approveAdministratorAccessPrompt")
            );
            var (_, output) = await RunGmmAsync("engine-sound-batch", game.Root, file);
            var result = Parse<EngineSoundBatchResult>(output, "ENGINE_SOUND_BATCH_RESULT:");
            SetBusy(false);
            RefreshHistory();
            RefreshBackupInfo();
            RefreshGameControls();
            MessageBox.Show(
                this,
                L.Owned(result is null ? output.Trim() : string.Join("\n", result.Messages)),
                title,
                MessageBoxButton.OK,
                result?.Success == true ? MessageBoxImage.Information : MessageBoxImage.Warning
            );
            await MyModsView.ScanAsync(game, force: true);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            MessageBox.Show(this, L.Owned(ex.Message), title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    static T? Parse<T>(string output, string prefix)
        where T : class
    {
        var line = output
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        return line is null ? null : JsonSerializer.Deserialize<T>(line[prefix.Length..]);
    }
}
