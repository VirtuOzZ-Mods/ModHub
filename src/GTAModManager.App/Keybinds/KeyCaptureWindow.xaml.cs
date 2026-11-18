using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Keybinds;

/// <summary>Captures a new shortcut (keyboard or mouse), previews the binding and any overlaps.</summary>
public partial class KeyCaptureWindow : Window
{
    private readonly KeybindEntry _entry;
    private readonly IReadOnlyList<KeybindEntry> _all;
    private readonly VirtualKeyboard _keyboard = new();
    private KeyChord _chord;

    public KeyChord? Result { get; private set; }

    private static readonly Brush DirectBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x38, 0x3B));
    private static readonly Brush PotentialBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xD3, 0x5E));
    private static readonly Brush SelectedBg = new SolidColorBrush(Color.FromRgb(0x5A, 0x3A, 0x00));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00));

    public KeyCaptureWindow(KeybindEntry entry, IReadOnlyList<KeybindEntry> all)
    {
        InitializeComponent();
        _entry = entry;
        _all = all;
        _chord = entry.Chord;
        EntryTitle.Text = L.T("ui.keybinds.labelValue", entry.ModName, entry.Action);
        EntryInfo.Text = L.T(
            "ui.keybinds.bindingWithKey",
            entry.File,
            entry.Section,
            L.Join(" + ", entry.Params.Select(p => $"{p.Name}={p.Raw}"))
        );
        OldChord.Text = entry.Chord.ToString();
        foreach (var t in new[] { CtrlToggle, ShiftToggle, AltToggle })
            t.Style = (Style)FindResource("ModToggle");

        KeyboardHost.Content = _keyboard;
        _keyboard.VisualFor = vk =>
            _chord.Keys.Contains(vk) || KeybindConflictAnalyzer.Base(_chord.Vk) == vk
                ? new KeyVisual(SelectedBg, Accent, [Accent], "", Accent, 0, "")
                : null;
        _keyboard.KeyClicked += vk =>
        {
            if (KeyNames.ModifierFor(vk) != KeyMods.None && vk is not (0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5))
                return;
            SetChord(new KeyChord(vk, CurrentToggles()));
        };

        foreach (
            var s in KeybindConflictAnalyzer.SuggestFree(
                all.Where(x => x.Id != entry.Id).ToList(),
                allowModifiers: true,
                count: 10
            )
        )
        {
            var b = new Button
            {
                Content = s.ToString(),
                Style = (Style)FindResource("Ghost"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = L.T("ui.keybinds.availableAccordingKnownDataNo"),
            };
            b.Click += (_, _) => SetChord(s);
            Suggestions.Children.Add(b);
        }
        if (Suggestions.Children.Count > 0)
            Suggestions.Children.Insert(
                0,
                new TextBlock
                {
                    Text = L.T("ui.keybinds.availableAccordingKnownData"),
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 6),
                }
            );
        UpdatePreview();
    }

    private KeyMods CurrentToggles() =>
        (CtrlToggle.IsChecked == true ? KeyMods.Ctrl : 0)
        | (ShiftToggle.IsChecked == true ? KeyMods.Shift : 0)
        | (AltToggle.IsChecked == true ? KeyMods.Alt : 0);

    private void OnModifierToggle(object sender, RoutedEventArgs e)
    {
        if (!_chord.IsEmpty)
            SetChord(_chord with { Mods = CurrentToggles(), Sides = KeySide.None }, syncToggles: false);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (
            key
            is Key.LeftCtrl
                or Key.RightCtrl
                or Key.LeftShift
                or Key.RightShift
                or Key.LeftAlt
                or Key.RightAlt
                or Key.LWin
                or Key.RWin
        )
        {
            // A lone modifier: wait for the main key; if it is released, the modifier itself can be picked with the mouse.
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (KeyNames.Get(vk) is null)
            return;
        var mods = KeyMods.None;
        var sides = KeySide.None;
        if (Keyboard.IsKeyDown(Key.LeftCtrl))
        {
            mods |= KeyMods.Ctrl;
            sides |= KeySide.LCtrl;
        }
        if (Keyboard.IsKeyDown(Key.RightCtrl))
        {
            mods |= KeyMods.Ctrl;
            sides |= KeySide.RCtrl;
        }
        if (Keyboard.IsKeyDown(Key.LeftShift))
        {
            mods |= KeyMods.Shift;
            sides |= KeySide.LShift;
        }
        if (Keyboard.IsKeyDown(Key.RightShift))
        {
            mods |= KeyMods.Shift;
            sides |= KeySide.RShift;
        }
        if (Keyboard.IsKeyDown(Key.LeftAlt))
        {
            mods |= KeyMods.Alt;
            sides |= KeySide.LAlt;
        }
        if (Keyboard.IsKeyDown(Key.RightAlt))
        {
            mods |= KeyMods.Alt;
            sides |= KeySide.RAlt;
        }
        // Left/right side matters only for formats that distinguish it; otherwise plain Ctrl/Shift/Alt is written.
        bool sideAware = _entry.Chord.Sides != KeySide.None;
        SetChord(new KeyChord(vk, mods, sideAware ? sides : KeySide.None));
    }

    private void SetChord(KeyChord chord, bool syncToggles = true)
    {
        _chord = chord;
        if (syncToggles)
        {
            CtrlToggle.IsChecked = chord.Mods.HasFlag(KeyMods.Ctrl);
            ShiftToggle.IsChecked = chord.Mods.HasFlag(KeyMods.Shift);
            AltToggle.IsChecked = chord.Mods.HasFlag(KeyMods.Alt);
        }
        _keyboard.Refresh();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        NewChord.Text = _chord.ToString();
        var p = KeybindEditor.Preview(_entry, _chord, _all);
        PreviewText.Text = p.Allowed
            ? L.T("ui.keybinds.willWritten", L.Join("\n", p.Changes.Select(c => $"  {c.Param}: {c.Old}  →  {c.New}")))
            : "";
        ErrorText.Text = p.Allowed ? "" : p.Error ?? "";
        ConflictList.ItemsSource = p
            .Conflicts.Select(c =>
            {
                var other = c.A.Id == _entry.Id ? c.B : c.A;
                return new ConflictLine(
                    L.T(
                        "ui.keybinds.with",
                        (c.Kind == ConflictKind.Direct ? L.T("ui.keybinds.conflict") : L.T("ui.keybinds.possible")),
                        other.ModName,
                        other.Action,
                        other.Chord,
                        c.Reason
                    ),
                    c.Kind == ConflictKind.Direct ? DirectBrush : PotentialBrush
                );
            })
            .ToList();
        SaveButton.IsEnabled = p.Allowed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var p = KeybindEditor.Preview(_entry, _chord, _all);
        if (!p.Allowed)
            return;
        if (
            p.Conflicts.Any(c => c.Kind == ConflictKind.Direct)
            && MessageBox.Show(
                this,
                L.T("ui.keybinds.newShortcutConflictsAnotherAction"),
                L.T("ui.keybinds.conflict3"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            ) != MessageBoxResult.Yes
        )
            return;
        Result = _chord;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
