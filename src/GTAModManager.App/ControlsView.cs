using System.Windows;
using System.Windows.Media;
using GTAModManager.Core.Mods;

namespace GTAModManager.App;

/// <summary>Renders one part of a key binding: a key as a key cap, connectors and hints as plain text.</summary>
public sealed record KeyChipView(
    string Text,
    Brush Background,
    Brush Border,
    Thickness BorderThickness,
    Thickness Padding,
    Brush Foreground,
    double FontSize,
    FontWeight Weight
)
{
    private static readonly Brush KeyBg = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    private static readonly Brush KeyBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    public static KeyChipView From(KeyToken t) =>
        t.Kind switch
        {
            KeyTokenKind.Key => new(
                t.Text,
                KeyBg,
                KeyBorder,
                new Thickness(1, 1, 1, 2),
                new Thickness(8, 2, 8, 2),
                Brushes.White,
                13,
                FontWeights.SemiBold
            ),
            KeyTokenKind.Hint => new(
                t.Text,
                Brushes.Transparent,
                Brushes.Transparent,
                new Thickness(0),
                new Thickness(0, 2, 0, 2),
                Muted,
                12,
                FontWeights.Normal
            ),
            _ => new(
                t.Text,
                Brushes.Transparent,
                Brushes.Transparent,
                new Thickness(0),
                new Thickness(0, 2, 0, 2),
                Muted,
                12.5,
                FontWeights.Normal
            ),
        };
}

public sealed record ControlRowView(List<KeyChipView> Keys, string Action);

public sealed record ControlGroupView(string Title, List<ControlRowView> Rows, List<string> Notes)
{
    public Visibility TitleVisibility => string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;

    public static ControlGroupView From(ControlGroup g) =>
        new(
            g.Title,
            g.Rows.Select(r => new ControlRowView(r.Keys.Select(KeyChipView.From).ToList(), r.Action)).ToList(),
            g.Notes
        );
}
