using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GTAModManager.App.Localization;
using GTAModManager.Core.Game;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;
using Microsoft.Win32;

namespace GTAModManager.App;

public partial class MainWindow
{
    private ComboBox? _languagePacks;
    private TextBlock? _languageStatus;
    private bool _updatingLanguage;
    private static string LanguagesDirectory => GTAModManager.Core.AppPaths.Languages;

    public void InitializeLocalizationSettings()
    {
        if (_languagePacks is not null || BackupPage.Content is not StackPanel settingsPanel)
            return;
        var body = new StackPanel();
        body.Children.Add(
            new TextBlock
            {
                Text = L.T("settings.language.title"),
                FontSize = 17.5,
                FontWeight = FontWeights.SemiBold,
            }
        );
        body.Children.Add(
            new TextBlock
            {
                Text = L.T("settings.language.description"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
                Margin = new Thickness(0, 4, 0, 12),
            }
        );
        _languagePacks = new ComboBox
        {
            MaxWidth = 540,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 280,
        };
        _languagePacks.SelectionChanged += OnLanguageSelected;
        body.Children.Add(_languagePacks);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        AddLanguageButton(actions, "settings.language.openFolder", OpenLanguagesFolder);
        AddLanguageButton(actions, "settings.language.reload", ReloadLanguagePacks);
        AddLanguageButton(actions, "settings.language.template", ExportLanguageTemplate);
        body.Children.Add(actions);
        _languageStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        body.Children.Add(_languageStatus);
        settingsPanel.Children.Insert(1, new Border { Style = (Style)FindResource("CardBorder"), Child = body });
        RefreshLanguageChoices();
        LocalizationService.Current.LanguageChanged += OnLanguagePackChanged;
        Closed += (_, _) => LocalizationService.Current.LanguageChanged -= OnLanguagePackChanged;
    }

    private void OnLanguagePackChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshLanguageChoices);
            return;
        }
        RefreshLanguageChoices();
    }

    private void AddLanguageButton(Panel parent, string key, Action action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Ghost"),
            Content = L.T(key),
            Margin = new Thickness(0, 0, 8, 6),
        };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private void RefreshLanguageChoices()
    {
        if (_languagePacks is null || _languageStatus is null)
            return;
        _updatingLanguage = true;
        try
        {
            var service = LocalizationService.Current;
            var choices = service.Packs.Select(pack => new LanguageChoice(pack.Id, pack.DisplayName)).ToArray();
            _languagePacks.ItemsSource = choices;
            _languagePacks.DisplayMemberPath = nameof(LanguageChoice.Label);
            _languagePacks.SelectedValuePath = nameof(LanguageChoice.Id);
            _languagePacks.SelectedValue = service.SelectedId;
            var selected = service.Packs.FirstOrDefault(pack => pack.Id == service.SelectedId);
            var source = service.Packs.FirstOrDefault(pack => pack.Id == "official.ru");
            var translated = selected?.Strings.Keys.Count(key => source?.Strings.ContainsKey(key) == true) ?? 0;
            var hint =
                source is not null && translated < source.Strings.Count
                    ? L.T("settings.language.incomplete")
                    : L.T("settings.language.customHint");
            var coverage = L.T("settings.language.coverage", translated, source?.Strings.Count ?? 0);
            var report =
                service.Diagnostics.Count > 0
                    ? L.T("settings.language.diagnostics", L.Join(Environment.NewLine, service.LocalizedDiagnostics))
                    : "";
            _languageStatus.Text = L.Join(
                Environment.NewLine,
                new[] { hint, coverage, report }.Where(text => text.Length > 0)
            );
        }
        finally
        {
            _updatingLanguage = false;
        }
    }

    private void OnLanguageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingLanguage || _languagePacks?.SelectedItem is not LanguageChoice choice)
            return;
        var service = LocalizationService.Current;
        var previous = service.SelectedId;
        try
        {
            // Merge into fresh settings: do not overwrite archive/backup choices saved by another screen.
            var saved = AppSettings.Load();
            saved.LanguagePackId = choice.Id;
            saved.Save();
            _settings.LanguagePackId = choice.Id;
            service.Select(choice.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            service.Select(previous);
            RefreshLanguageChoices();
            MessageBox.Show(
                this,
                L.T("settings.language.saveError", L.Owned(ex.Message)),
                L.T("settings.language.title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
    }

    private void ReloadLanguagePacks()
    {
        try
        {
            LocalizationService.Current.Reload(LanguagesDirectory);
            var saved = AppSettings.Load();
            saved.LanguagePackId = LocalizationService.Current.SelectedId;
            saved.Save();
            _settings.LanguagePackId = saved.LanguagePackId;
            RefreshLanguageChoices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowLanguageError(ex);
        }
    }

    private void OpenLanguagesFolder()
    {
        try
        {
            Directory.CreateDirectory(LanguagesDirectory);
            Process.Start(new ProcessStartInfo(LanguagesDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowLanguageError(ex);
        }
    }

    private void ExportLanguageTemplate()
    {
        var dialog = new SaveFileDialog
        {
            Title = L.T("settings.language.templateTitle"),
            FileName = "my-translation.json",
            DefaultExt = ".json",
            Filter = "JSON (*.json)|*.json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            LocalizationService.Current.ExportTemplate(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowLanguageError(ex);
        }
    }

    private void ShowLanguageError(Exception ex) =>
        MessageBox.Show(
            this,
            L.T("settings.language.error", L.Owned(ex.Message)),
            L.T("settings.language.title"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning
        );

    private sealed record LanguageChoice(string Id, string Label);
}
