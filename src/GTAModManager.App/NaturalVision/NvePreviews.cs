using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GTAModManager.Core.Localization;
using GTAModManager.Core.NaturalVision;

namespace GTAModManager.App.NaturalVision;

internal sealed class NvePreviews(NveCatalog catalog)
{
    readonly CancellationTokenSource cancellation = new();
    readonly SemaphoreSlim gate = new(1);

    public void Cancel() => cancellation.Cancel();

    public Border Tile(NveComponent component)
    {
        var grid = new Grid();
        grid.Children.Add(new Image { Stretch = Stretch.UniformToFill });
        grid.Children.Add(
            new TextBlock
            {
                Text = component.Id == "reshade" ? "FX" : "NVE",
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 164, 151)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        );
        var tile = new Border
        {
            Width = 104,
            Height = 104,
            Margin = new Thickness(14, 14, 0, 14),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(24, 49, 42)),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Child = grid,
            Tag = component,
        };
        tile.Loaded += async (_, _) => await ShowAsync(tile, (NveComponent)tile.Tag);
        return tile;
    }

    public async Task ShowAsync(Border tile, NveComponent component)
    {
        tile.Tag = component;
        var grid = (Grid)tile.Child;
        var image = (Image)grid.Children[0];
        var label = (TextBlock)grid.Children[1];
        image.Source = null;
        label.Visibility = Visibility.Visible;
        if (!tile.IsLoaded || !component.Source.EndsWith(".oiv", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            var bitmap = await Task.Run(
                async () =>
                {
                    await gate.WaitAsync(cancellation.Token);
                    try
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        string key = Convert.ToHexString(
                            SHA256.HashData(
                                Encoding.UTF8.GetBytes(catalog.Source + "\n" + catalog.Stamp + "\n" + component.Source)
                            )
                        );
                        string path = Path.Combine(
                            GTAModManager.Core.AppPaths.Cache,
                            "NaturalVision previews",
                            key + ".png"
                        );
                        byte[]? data = null;
                        if (File.Exists(path) && new FileInfo(path).Length <= 4L << 20)
                            data = File.ReadAllBytes(path);
                        if (data is null)
                        {
                            using var bundle = new NveBundle(catalog.Source);
                            data = bundle.ReadIcon(component.Source, cancellation.Token);
                        }
                        if (data is null || data.Length == 0)
                            return null;
                        using var stream = new MemoryStream(data);
                        var result = new BitmapImage();
                        result.BeginInit();
                        result.CacheOption = BitmapCacheOption.OnLoad;
                        result.DecodePixelWidth = 208;
                        result.StreamSource = stream;
                        result.EndInit();
                        result.Freeze();
                        try
                        {
                            if (!File.Exists(path))
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                                File.WriteAllBytes(path, data);
                            }
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                        return result;
                    }
                    finally
                    {
                        gate.Release();
                    }
                },
                cancellation.Token
            );
            if (!cancellation.IsCancellationRequested && ReferenceEquals(tile.Tag, component) && bitmap is not null)
            {
                image.Source = bitmap;
                label.Visibility = Visibility.Collapsed;
                tile.ToolTip = L.T("ui.nve.authorSPreviewFromOiv");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* Optional artwork must never block installing the package. */
        }
    }
}
