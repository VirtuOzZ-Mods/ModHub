using System.Windows;
using System.Windows.Media;
using GTAModManager.Core.Localization;

namespace GTAModManager.App;

/// <summary>Drop Zone: the taglines rotate on every display, and the frame lights up while a file is dragged over it.</summary>
public partial class MainWindow
{
    // Irony in the spirit of the logo: suggestive, but never explicit.
    private static readonly string[] DropTeases =
    [
        L.T("ui.dropZone.goInstallHereICan"),
        L.T("ui.dropZone.donTShyDropOiv"),
        L.T("ui.dropZone.sizeDoesnTMatterUp"),
        L.T("ui.dropZone.weAlwaysTakePrecautionsBackup"),
        L.T("ui.dropZone.firstLittleWarmUpAnalysis"),
        L.T("ui.dropZone.nothingHappensUntilSayYes"),
        L.T("ui.dropZone.gentlyCarefullyWayBack"),
        L.T("ui.dropZone.dragHereSlowlyS"),
        L.T("ui.dropZone.weWonTAskWhere"),
        L.T("ui.dropZone.howManyModsCanOne"),
    ];

    private int _teaseIndex = Random.Shared.Next(DropTeases.Length);
    private static readonly Brush DropFrameIdle = Frozen("#77736B"),
        DropFrameHot = Frozen("#FF9900");

    private void OnDropZoneVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;
        _teaseIndex = (_teaseIndex + 1 + Random.Shared.Next(DropTeases.Length - 1)) % DropTeases.Length;
        DropTease.Text = L.T("ui.dropZone.text", DropTeases[_teaseIndex]);
    }

    private void OnDropZoneDragEnter(object sender, DragEventArgs e) => DropFrame.Stroke = DropFrameHot;

    private void OnDropZoneDragLeave(object sender, DragEventArgs e) => DropFrame.Stroke = DropFrameIdle;

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
