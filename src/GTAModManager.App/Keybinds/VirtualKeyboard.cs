using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GTAModManager.App.Keybinds;

/// <summary>How to render a key: background, owner stripes, status icon, binding count, tooltip.</summary>
public sealed record KeyVisual(
    Brush Background,
    Brush Border,
    IReadOnlyList<Brush> Stripes,
    string Glyph,
    Brush GlyphBrush,
    int Count,
    string Tooltip
);

/// <summary>
/// Virtual keyboard with the physical layout (main block, F row, navigation, arrows, NumPad).
/// Drawn in key-width units and scaled by a Viewbox, so it is never clipped in small windows.
/// NumPad Enter has the same code as Enter (a Windows limitation), so it shows the same state.
/// </summary>
public sealed class VirtualKeyboard : UserControl
{
    private const double U = 44; // key size in pixels before scaling
    private readonly Canvas _canvas = new() { Width = 22.6 * U, Height = 6.6 * U };
    private readonly Dictionary<int, List<Border>> _keys = [];
    private int _selected;

    public event Action<int>? KeyClicked;
    public Func<int, KeyVisual?>? VisualFor { get; set; }

    private static readonly Brush KeyBg = new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x33));
    private static readonly Brush KeyBorder = new SolidColorBrush(Color.FromRgb(0x14, 0x16, 0x19));
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00));
    private static readonly Brush Label = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4));

    public VirtualKeyboard()
    {
        Content = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            Child = _canvas,
        };
        foreach (var k in Layout())
            AddKey(k);
    }

    public int Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Refresh();
        }
    }

    public void Refresh()
    {
        foreach (var (vk, borders) in _keys)
        foreach (var b in borders)
            Paint(b, vk);
    }

    private sealed record KeyDef(int Vk, string Text, double X, double Y, double W = 1, double H = 1);

    private void AddKey(KeyDef k)
    {
        var label = new TextBlock
        {
            Text = k.Text,
            Foreground = Label,
            FontSize = k.Text.Length > 4 ? 10.5 : 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            // Wrap only at spaces ("Caps Lock"); a single word ("Pause", "PgUp") is not broken but shrunk to the key width.
            TextWrapping = k.Text.Contains(' ') ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        if (k.Text.Contains(' '))
            label.MaxWidth = k.W * U - 10; // A Viewbox gives infinite width, so wrapping width is set explicitly
        var labelBox = new Viewbox
        {
            Child = label,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(3, 0, 3, 4),
        };
        var glyph = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(4, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var count = new TextBlock
        {
            FontSize = 10,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 2, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var stripes = new UniformGrid
        {
            Rows = 1,
            Height = 5,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 4, 4),
        };
        var grid = new Grid();
        grid.Children.Add(labelBox);
        grid.Children.Add(glyph);
        grid.Children.Add(count);
        grid.Children.Add(stripes);

        var b = new Border
        {
            Width = k.W * U - 4,
            Height = k.H * U - 4,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            Child = grid,
            Cursor = Cursors.Hand,
            Tag = k.Vk,
        };
        b.MouseLeftButtonUp += (_, _) => KeyClicked?.Invoke(k.Vk);
        Canvas.SetLeft(b, k.X * U + 2);
        Canvas.SetTop(b, k.Y * U + 2);
        _canvas.Children.Add(b);
        if (!_keys.TryGetValue(k.Vk, out var list))
            _keys[k.Vk] = list = [];
        list.Add(b);
        Paint(b, k.Vk);
    }

    private void Paint(Border b, int vk)
    {
        var grid = (Grid)b.Child;
        var glyph = (TextBlock)grid.Children[1];
        var count = (TextBlock)grid.Children[2];
        var stripes = (UniformGrid)grid.Children[3];
        var v = VisualFor?.Invoke(vk);
        b.Background = Keycap(v?.Background ?? KeyBg);
        b.BorderBrush = vk == _selected ? SelectedBorder : v?.Border ?? KeyBorder;
        // Raised key cap: the bottom edge is thicker so the key sits above the board.
        b.BorderThickness = vk == _selected ? new Thickness(2.5, 2.5, 2.5, 3.5) : new Thickness(1.5, 1.5, 1.5, 3);
        glyph.Text = v?.Glyph ?? "";
        glyph.Foreground = v?.GlyphBrush ?? Brushes.White;
        count.Text = v is { Count: > 1 } ? "×" + v.Count : "";
        stripes.Children.Clear();
        foreach (var s in v?.Stripes ?? [])
            stripes.Children.Add(
                new Border
                {
                    Background = s,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1, 0, 1, 0),
                }
            );
        b.ToolTip = v?.Tooltip is { Length: > 0 } t ? t : null;
    }

    private static readonly Dictionary<Color, Brush> KeycapCache = [];

    /// <summary>Glossy key cap from the state colour: lighter at the top, darker at the bottom, with a highlight band in the middle.</summary>
    private static Brush Keycap(Brush state)
    {
        if (state is not SolidColorBrush { Color: var c })
            return state;
        if (KeycapCache.TryGetValue(c, out var cached))
            return cached;
        static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb(
                a.A,
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t)
            );
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        brush.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.16), 0));
        brush.GradientStops.Add(new GradientStop(Mix(c, Colors.White, 0.06), 0.48));
        brush.GradientStops.Add(new GradientStop(c, 0.52));
        brush.GradientStops.Add(new GradientStop(Mix(c, Colors.Black, 0.28), 1));
        brush.Freeze();
        return KeycapCache[c] = brush;
    }

    private static List<KeyDef> Layout()
    {
        var k = new List<KeyDef>();
        void Row(double y, double x, params (int vk, string text, double w)[] keys)
        {
            foreach (var (vk, text, w) in keys)
            {
                k.Add(new KeyDef(vk, text, x, y, w));
                x += w;
            }
        }
        // F row
        k.Add(new(0x1B, "Esc", 0, 0));
        for (int i = 0; i < 4; i++)
            k.Add(new(0x70 + i, "F" + (i + 1), 2 + i, 0));
        for (int i = 0; i < 4; i++)
            k.Add(new(0x74 + i, "F" + (i + 5), 6.5 + i, 0));
        for (int i = 0; i < 4; i++)
            k.Add(new(0x78 + i, "F" + (i + 9), 11 + i, 0));
        k.Add(new(0x2C, "PrtSc", 15.3, 0));
        k.Add(new(0x91, "ScrLk", 16.3, 0));
        k.Add(new(0x13, "Pause", 17.3, 0));
        // Main block
        Row(
            1.3,
            0,
            (0xC0, "`", 1),
            (0x31, "1", 1),
            (0x32, "2", 1),
            (0x33, "3", 1),
            (0x34, "4", 1),
            (0x35, "5", 1),
            (0x36, "6", 1),
            (0x37, "7", 1),
            (0x38, "8", 1),
            (0x39, "9", 1),
            (0x30, "0", 1),
            (0xBD, "-", 1),
            (0xBB, "=", 1),
            (0x08, "Backspace", 2)
        );
        Row(
            2.3,
            0,
            (0x09, "Tab", 1.5),
            ('Q', "Q", 1),
            ('W', "W", 1),
            ('E', "E", 1),
            ('R', "R", 1),
            ('T', "T", 1),
            ('Y', "Y", 1),
            ('U', "U", 1),
            ('I', "I", 1),
            ('O', "O", 1),
            ('P', "P", 1),
            (0xDB, "[", 1),
            (0xDD, "]", 1),
            (0xDC, "\\", 1.5)
        );
        Row(
            3.3,
            0,
            (0x14, "Caps Lock", 1.75),
            ('A', "A", 1),
            ('S', "S", 1),
            ('D', "D", 1),
            ('F', "F", 1),
            ('G', "G", 1),
            ('H', "H", 1),
            ('J', "J", 1),
            ('K', "K", 1),
            ('L', "L", 1),
            (0xBA, ";", 1),
            (0xDE, "'", 1),
            (0x0D, "Enter", 2.25)
        );
        Row(
            4.3,
            0,
            (0xA0, "Shift", 2.25),
            ('Z', "Z", 1),
            ('X', "X", 1),
            ('C', "C", 1),
            ('V', "V", 1),
            ('B', "B", 1),
            ('N', "N", 1),
            ('M', "M", 1),
            (0xBC, ",", 1),
            (0xBE, ".", 1),
            (0xBF, "/", 1),
            (0xA1, "Shift", 2.75)
        );
        Row(
            5.3,
            0,
            (0xA2, "Ctrl", 1.25),
            (0x5B, "Win", 1.25),
            (0xA4, "Alt", 1.25),
            (0x20, "Space", 6.25),
            (0xA5, "Alt", 1.25),
            (0x5C, "Win", 1.25),
            (0x5D, "Menu", 1.25),
            (0xA3, "Ctrl", 1.25)
        );
        // Navigation and arrows
        Row(1.3, 15.3, (0x2D, "Ins", 1), (0x24, "Home", 1), (0x21, "PgUp", 1));
        Row(2.3, 15.3, (0x2E, "Del", 1), (0x23, "End", 1), (0x22, "PgDn", 1));
        k.Add(new(0x26, "↑", 16.3, 4.3));
        Row(5.3, 15.3, (0x25, "←", 1), (0x28, "↓", 1), (0x27, "→", 1));
        // NumPad
        Row(1.3, 18.6, (0x90, "Num Lock", 1), (0x6F, "/", 1), (0x6A, "*", 1), (0x6D, "-", 1));
        Row(2.3, 18.6, (0x67, "7", 1), (0x68, "8", 1), (0x69, "9", 1));
        k.Add(new(0x6B, "+", 21.6, 2.3, 1, 2));
        Row(3.3, 18.6, (0x64, "4", 1), (0x65, "5", 1), (0x66, "6", 1));
        Row(4.3, 18.6, (0x61, "1", 1), (0x62, "2", 1), (0x63, "3", 1));
        k.Add(new(0x0D, "Enter", 21.6, 4.3, 1, 2));
        Row(5.3, 18.6, (0x60, "0", 2), (0x6E, ".", 1));
        return k;
    }
}
