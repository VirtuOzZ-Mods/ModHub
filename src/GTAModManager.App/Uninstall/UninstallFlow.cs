using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GTAModManager.Core;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Uninstall;

public enum GameCleanup
{
    Cancel,

    /// <summary>Remove every mod and its traces (Restore Vanilla Game) in each detected game.</summary>
    Vanilla,

    /// <summary>Remove only Mod Hub's own components (the PED selector); other mods keep working.</summary>
    OwnOnly,

    /// <summary>Leave the game folders as they are.</summary>
    Keep,
}

/// <summary>Dialog and the final self-removal step of "Remove Mod Hub".</summary>
public static class UninstallFlow
{
    /// <summary>Set once removal has started: exit must not write anything new.</summary>
    public static bool Started { get; set; }

    public static GameCleanup AskGameCleanup(Window owner)
    {
        var result = GameCleanup.Cancel;
        var window = new Window
        {
            Owner = owner,
            Title = L.T("ui.uninstall.title"),
            Width = 580,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("Bg"),
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.uninstall.title"),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
            }
        );
        panel.Children.Add(
            new TextBlock
            {
                Text = L.T("ui.uninstall.gameQuestion"),
                Foreground = (Brush)Application.Current.FindResource("Muted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 16),
            }
        );
        Button Choice(string title, string detail, GameCleanup value)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(
                new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Opacity = 0.8,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                }
            );
            var button = new Button
            {
                Content = content,
                Style = (Style)Application.Current.FindResource("Ghost"),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 8),
            };
            button.Click += (_, _) =>
            {
                result = value;
                window.DialogResult = true;
            };
            return button;
        }
        panel.Children.Add(Choice(L.T("ui.uninstall.vanilla"), L.T("ui.uninstall.vanillaDetail"), GameCleanup.Vanilla));
        panel.Children.Add(Choice(L.T("ui.uninstall.ownOnly"), L.T("ui.uninstall.ownOnlyDetail"), GameCleanup.OwnOnly));
        panel.Children.Add(Choice(L.T("ui.uninstall.keepGame"), L.T("ui.uninstall.keepGameDetail"), GameCleanup.Keep));
        panel.Children.Add(
            new Button
            {
                Content = L.T("ui.common.cancel"),
                Style = (Style)Application.Current.FindResource("Ghost"),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0),
                IsCancel = true,
            }
        );
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    /// <summary>
    /// Folders the final step removes: the whole program folder in the release layout (marker next to the exe),
    /// otherwise (a developer build) only what ModHub wrote there itself.
    /// </summary>
    public static IReadOnlyList<string> FoldersToRemove()
    {
        bool release = File.Exists(Path.Combine(AppContext.BaseDirectory, AppPaths.Marker));
        if (release || AppPaths.UsesDocumentsFallback)
            return [AppPaths.Root];
        return [AppPaths.Data, AppPaths.Languages];
    }

    /// <summary>
    /// Deletes the program folder once this process has exited. Uses cmd.exe from System32 with no script file,
    /// so nothing is left behind; retries for about 40 seconds while files are still locked.
    /// Returns false when a path cannot be passed to cmd safely (the user then deletes the folder by hand).
    /// </summary>
    public static bool ScheduleRemoval(IReadOnlyList<string> folders, IReadOnlyList<string> files)
    {
        var targets = folders.Concat(files).ToList();
        if (targets.Any(p => p.IndexOfAny(['"', '%', '^', '&', '|', '<', '>', '!']) >= 0))
            return false;
        string removeFolders = string.Concat(folders.Select(f => $"rmdir /s /q \"{f}\" 2>nul & "));
        string removeFiles = string.Concat(files.Select(f => $"del /f /q \"{f}\" 2>nul & "));
        string done = string.Join(" ", targets.Select(t => $"if not exist \"{t}\""));
        string loop =
            $"for /l %i in (1,1,20) do @(ping -n 3 127.0.0.1 >nul & {removeFolders}{removeFiles}{done} exit /b 0)";
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var psi = new ProcessStartInfo(Path.Combine(system, "cmd.exe"))
        {
            Arguments = "/d /s /c \"" + loop + "\"",
            WorkingDirectory = system,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);
        return true;
    }
}
