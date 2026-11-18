using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using GTAModManager.App.Localization;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Inventory;
using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Library;
using GTAModManager.Core.Localization;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        if (Environment.GetCommandLineArgs().Contains("--script-libraries"))
            return LibraryChecks();
        if (Environment.GetCommandLineArgs().Contains("--recommendations"))
            return RecommendationChecks();
        var directory = Path.Combine(Path.GetTempPath(), "modhub-ui-localization-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var application = new Application();
            LiveLocalization.Enable();
            var beforeCulture = CultureInfo.CurrentCulture.Name;
            const string key = "settings.language.title";
            File.WriteAllText(
                Path.Combine(directory, "test.json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        id = "harness.en",
                        culture = "en",
                        displayName = "Harness English",
                        author = "Tests",
                        version = "1",
                        strings = new Dictionary<string, string>
                        {
                            [key] = "Interface language",
                            ["ui.common.error"] = "UI error",
                            ["cli.shared.error2"] = "Command error",
                            // A malicious/inappropriate translation must not become a parser's regex.
                            ["core.harness.fakeSizeParser"] = "not a size parser",
                        },
                    }
                )
            );
            var service = LocalizationService.Current;
            service.Reload(directory);
            Check(service.Select("official.ru"), "Russian base pack exists");
            var original = L.T(key);
            Check(original != key, "Base catalog contains the settings key");
            var plain = new string(original.ToCharArray());
            var label = new TextBlock { Text = original };
            var foreign = new TextBlock { Text = plain };
            var replaced = new TextBlock { Text = L.T(key) };
            var otherKey = new TextBlock { Text = L.T("ui.common.error") };
            var newlyTracked = new TextBlock { Text = new string(L.T("ui.common.error").ToCharArray()) };
            var input = new TextBox { Text = original };
            var button = new Button { Content = original, ToolTip = L.T(key) };
            var expander = new Expander { Header = L.T(key) };
            var inline = LiveLocalization.CreateRun(L.T(key));
            var window = new Window { Title = L.T(key) };
            AutomationProperties.SetName(button, L.T(key));
            var source = new Line { Text = L.T(key) };
            var bound = new TextBlock();
            bound.SetBinding(TextBlock.TextProperty, new Binding(nameof(Line.Text)) { Source = source });
            var markup = new TextBlock();
            markup.SetBinding(TextBlock.TextProperty, TrExtension.CreateBinding(key));
            var date = new TextBlock { Text = L.T("format.date.readable", new DateTime(2026, 1, 12)) };
            var rating = new TextBlock { Text = L.T("format.rating", 3.5) };
            var row = new TextBlock { Text = L.Join(" · ", L.T(key), L.Truncate(L.T(key), 8)) };
            foreach (
                var target in new DependencyObject[]
                {
                    label,
                    foreign,
                    replaced,
                    otherKey,
                    newlyTracked,
                    input,
                    button,
                    expander,
                    inline,
                    window,
                    bound,
                    markup,
                    date,
                    rating,
                    row,
                }
            )
                LiveLocalization.Track(target);
            replaced.Text = new string(replaced.Text.ToCharArray());
            otherKey.Text = L.T("cli.shared.error2");
            newlyTracked.Text = L.T("cli.shared.error2");
            input.Text = "User's unsaved text";
            var renderedLabel = new TextBlock { Text = L.T(key) };
            var renderedButton = new Button { Content = L.T(key) };
            var collapsedLabel = new TextBlock { Text = L.T(key), Visibility = Visibility.Collapsed };
            var menuLabel = new MenuItem { Header = L.T(key) };
            renderedButton.ContextMenu = new ContextMenu { Items = { menuLabel } };
            var renderedForeign = new TextBlock { Text = plain };
            var renderedBound = new TextBlock();
            renderedBound.SetBinding(TextBlock.TextProperty, new Binding(nameof(Line.Text)) { Source = source });
            var screenshotKeys = new[]
            {
                "settings.language.title",
                "settings.language.openFolder",
                "settings.language.reload",
                "settings.language.template",
                "ui.keybinds.availableAccordingKnownData2",
                "ui.common.myMods",
                "ui.inventory.saveList",
                "ui.main.specifyGameFolderManually",
            };
            var screenshotLabels = screenshotKeys.Select(k => new TextBlock { Text = L.T(k) }).ToArray();
            var panel = new StackPanel
            {
                Children = { renderedLabel, renderedButton, collapsedLabel, renderedForeign, renderedBound },
            };
            foreach (var item in screenshotLabels)
                panel.Children.Add(item);
            window.Content = panel;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Left = -5000;
            window.Show();
            window.UpdateLayout();
            Pump();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Check(service.Select("harness.en"), "Custom pack is selectable");
            Pump();
            Check(renderedLabel.Text == "Interface language", "Rendered programmatic heading updates after loading");
            Check(
                (string)renderedButton.Content == "Interface language",
                "Rendered styled button updates after loading"
            );
            Check(collapsedLabel.Text == "Interface language", "Unopened collapsed page updates");
            Check((string)menuLabel.Header == "Interface language", "Unopened context menu updates");
            Check(renderedForeign.Text == plain, "Rendered foreign text stays untouched");
            Check(
                renderedBound.Text == "Interface language"
                    && BindingOperations.IsDataBound(renderedBound, TextBlock.TextProperty),
                "Rendered model binding stays active"
            );
            Check(label.Text == "Interface language", "Programmatic TextBlock updates immediately");
            Check(foreign.Text == plain, "Foreign string with identical source text remains untouched");
            Check(
                replaced.Text == plain,
                "Equal-text external replacement does not retain old localization provenance"
            );
            Check(
                otherKey.Text == "Command error",
                "Equal-text replacement with a different key uses the new descriptor"
            );
            Check(newlyTracked.Text == "Command error", "Equal-text replacement can become explicitly localized");
            Check(input.Text == "User's unsaved text", "Edited text is not overwritten");
            Check((string)button.Content == "Interface language", "Button content updates");
            Check((string)button.ToolTip == "Interface language", "Tooltip updates");
            Check(AutomationProperties.GetName(button) == "Interface language", "Accessibility label updates");
            Check((string)expander.Header == "Interface language", "Header updates");
            Check(inline.Text == "Interface language", "Inline text updates");
            Check(window.Title == "Interface language", "Window title updates");
            Check(
                bound.Text == "Interface language" && BindingOperations.IsDataBound(bound, TextBlock.TextProperty),
                "Existing data binding survives language change"
            );
            Check(markup.Text == "Interface language", "Resource markup binding updates");
            Check(
                date.Text == "12 Jan 2026" && rating.Text == "3.5",
                "Display numbers and dates reformat using selected culture"
            );
            Check(
                row.Text == "Interface language · Interfac…",
                "Joined queue-like status and truncation update in place"
            );
            source.Text = "External model text";
            Pump();
            Check(bound.Text == "External model text", "Existing data binding continues to receive new values");
            Check(service.Select("official.ru"), "Can return to Russian");
            Pump();
            Check(label.Text == original, "Language can switch back without recreating controls");
            Check(bound.Text == "External model text", "External bound value stays untouched");
            var catalog = DiscoverCatalog.LoadBuiltIn();
            var cachedSubtitle = catalog.Categories.First().Subtitle;
            var cachedCard = new GTAModManager.App.Discover.ModCardViewModel(
                catalog.Entries.First(e => e.Kind is not null && e.Compat?.Note is not null)
            );
            foreach (var language in new[] { "official.en", "official.uk", "official.ru", "official.uk" })
            {
                service.Select(language);
                Pump();
                for (var i = 0; i < screenshotKeys.Length; i++)
                    Check(
                        screenshotLabels[i].Text == L.T(screenshotKeys[i]),
                        $"Screenshot label updates to {language}: {screenshotKeys[i]}"
                    );
                Check(
                    L.Display(cachedSubtitle) == L.T("catalog.category.essentials.subtitle"),
                    "Cached catalog description renders in current language"
                );
                Check(
                    cachedCard.Kind == L.T($"catalog.entry.{cachedCard.Entry.Id}.kind"),
                    "Cached card type renders in current language"
                );
                Check(
                    cachedCard.Note == L.T($"catalog.entry.{cachedCard.Entry.Id}.note"),
                    "Cached compatibility note renders in current language"
                );
            }
            Check(
                L.T("ui.main.specifyGameFolderManually") == "Вказати папку гри вручну…",
                "Ukrainian manual-folder wording"
            );
            service.Select("official.en");
            foreach (
                var (name, expected) in new[]
                {
                    ("База для моддинга: RageOpenV", "Modding essentials: RageOpenV"),
                    ("Дополнительно: NOSUnderground.Graphics.asi", "Additional: NOSUnderground.Graphics.asi"),
                    ("Установка GMM PED Selector", "Installing Ped Selector"),
                }
            )
            {
                var group = new ModGroup
                {
                    Name = name,
                    Kind = "",
                    Origin = ModOrigin.ModHub,
                };
                Check(
                    group.DisplayName == expected && group.Name == name,
                    "Legacy operation title translates only for display"
                );
            }
            var archive = new ModGroup
            {
                Name = "Правки архивов игры: update\\update.rpf",
                Kind = "",
                GeneratedName = true,
            };
            Check(
                archive.DisplayName == "Game archive edits: update\\update.rpf",
                "Generated external archive group translates"
            );
            var userNamed = new ModGroup
            {
                Name = archive.Name,
                Kind = "Подписано вами",
                Origin = ModOrigin.External,
            };
            Check(
                userNamed.DisplayName == userNamed.Name,
                "User-defined name matching a program template stays unchanged"
            );
            Check(L.Display(plain) == plain, "Cached foreign text is never matched by content");
            var fallbackVariant = new GTAModManager.Core.Installation.GameConfigVariant(
                "gameconfig.xml",
                "",
                "По умолчанию",
                "Стандартный",
                null,
                null
            );
            Check(
                fallbackVariant.PresetLabel == L.T("core.installation.default2")
                    && fallbackVariant.TrafficLabel == L.T("core.installation.default"),
                "Generated GameConfig options translate"
            );
            var authoredVariant = new GTAModManager.Core.Installation.GameConfigVariant(
                "more mods\\Стандартный\\gameconfig.xml",
                "",
                "more mods",
                "Стандартный",
                null,
                null
            );
            Check(
                authoredVariant.PresetLabel == "more mods" && authoredVariant.TrafficLabel == "Стандартный",
                "Author GameConfig folders remain untouched"
            );
            Check(CultureInfo.CurrentCulture.Name == beforeCulture, "Process parsing culture does not change");
            service.Select("harness.en");
            Check(BackupLibrary.ParseSize("25 гб") == 25L * (1L << 30), "Language pack cannot change size parsing");
            Check(KeyNames.Find("Пробел")?.Vk == 0x20, "Language pack cannot change legacy key aliases");
            var oldSettings = JsonSerializer.Deserialize<AppSettings>(
                "{\"Legacy\":\"C:\\\\Game\",\"LastEdition\":\"Legacy\"}"
            )!;
            Check(
                oldSettings.LanguagePackId is null && oldSettings.LastEdition == "Legacy",
                "Old settings remain compatible"
            );
            window.Close();
            application.Shutdown();
            Console.WriteLine($"UI localization checks passed: {_assertions} assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static int RecommendationChecks()
    {
        var directory = Directory.CreateTempSubdirectory("modhub-recommendation-ui-").FullName;
        try
        {
            GTAModManager.Core.Game.GameInstall Game(string name, string exe)
            {
                string root = Path.Combine(directory, name);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, exe), "test");
                return GTAModManager.Core.Game.GameInstall.Open(root);
            }
            var legacy = Game("legacy", "GTA5.exe");
            var enhanced = Game("enhanced", "GTA5_Enhanced.exe");
            var catalog = DiscoverCatalog.LoadBuiltIn();
            var assembly = typeof(GTAModManager.App.Discover.ModCardViewModel).Assembly;
            using var resourceStream = assembly.GetManifestResourceStream(
                assembly.GetManifestResourceNames().Single(n => n.EndsWith(".g.resources"))
            )!;
            using var resources = new System.Resources.ResourceReader(resourceStream);
            var images = resources
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(p => (string)p.Key, p => p.Value);
            foreach (var profile in GTAModManager.Core.Installation.RecommendedMods.Catalog)
            {
                var entry = catalog.Entries.Single(e => e.Id == profile.Id);
                var card = new GTAModManager.App.Discover.ModCardViewModel(entry);
                Check(
                    entry.Source == MetaSource.Gta5Mods,
                    "Existing GTA5-Mods statistics and author links are preserved"
                );
                var tagged = new GTAModManager.App.Discover.ModCardViewModel(entry with { Cover = null });
                using (var provider = new ModMetadataProvider(Path.Combine(directory, "cache")))
                    tagged.Apply(
                        new CachedMetadata
                        {
                            Id = entry.Id,
                            Url = entry.Url,
                            Data = new ModMetadata
                            {
                                Compat = new CompatInfo { Enhanced = true },
                                Downloads = 12345,
                            },
                        },
                        provider
                    );
                Check(
                    tagged.Compat.Enhanced == entry.Compat!.Enhanced,
                    "Website tags cannot promote unverified edition compatibility"
                );
                Check(!string.IsNullOrEmpty(entry.Cover), "Recommendation has an offline cover");
                var image = System.Windows.Media.Imaging.BitmapFrame.Create(
                    (Stream)images["discover/assets/covers/" + entry.Cover]!
                );
                Check(image.PixelWidth > 100 && image.PixelHeight > 100, "Author cover decodes without network");
                card.SetGame(legacy);
                Check(
                    card.InstallVisibility == (entry.Install == "download" ? Visibility.Visible : Visibility.Collapsed),
                    "Legacy shows supported installs, save stays page-only"
                );
                card.SetGame(enhanced);
                Check(
                    card.InstallVisibility == (profile.Enhanced ? Visibility.Visible : Visibility.Collapsed),
                    "Enhanced only shows author-confirmed installs"
                );
                Check(
                    card.InstallEnabled == profile.Enhanced,
                    "Hidden or unsupported recommendation cannot be invoked"
                );
                card.SetGame(null);
                Check(!card.InstallEnabled, "Recommendation requires a selected game");
            }
            Console.WriteLine($"Recommendation UI checks passed: {_assertions} assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static int LibraryChecks()
    {
        var directory = Directory.CreateTempSubdirectory("modhub-library-ui-").FullName;
        try
        {
            GTAModManager.Core.Game.GameInstall Create(string folder, string exe)
            {
                string path = Path.Combine(directory, folder);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, exe), "test");
                return GTAModManager.Core.Game.GameInstall.Open(path);
            }
            var legacy = Create("legacy", "GTA5.exe");
            var enhanced = Create("enhanced", "GTA5_Enhanced.exe");
            foreach (var component in GTAModManager.Core.Installation.ScriptLibraries.Catalog)
            {
                var entry = DiscoverCatalog.LoadBuiltIn().Entries.Single(e => e.Id == component.Id);
                Check(entry.Cover == component.Id + ".png", "Library has an explicit bundled preview");
                var card = new GTAModManager.App.Discover.ModCardViewModel(entry);
                var assembly = typeof(GTAModManager.App.Discover.ModCardViewModel).Assembly;
                using (
                    var resourceStream = assembly.GetManifestResourceStream(
                        assembly.GetManifestResourceNames().Single(n => n.EndsWith(".g.resources"))
                    )!
                )
                using (var resources = new System.Resources.ResourceReader(resourceStream))
                {
                    var imageStream =
                        resources
                            .Cast<System.Collections.DictionaryEntry>()
                            .Single(p => (string)p.Key == "discover/assets/covers/" + entry.Cover)
                            .Value as Stream;
                    var image = System.Windows.Media.Imaging.BitmapFrame.Create(imageStream!);
                    Check(image.PixelWidth > 100 && image.PixelHeight > 100, "Bundled preview decodes without network");
                }
                Check(
                    card.CoverStretch == System.Windows.Media.Stretch.Uniform,
                    "Portrait preview stays fully visible"
                );
                card.SetGame(legacy);
                Check(
                    card.InstallVisibility == Visibility.Visible && card.InstallEnabled,
                    "Legacy offers the verified library"
                );
                Check(
                    card.VersionText == component.Tag && card.VersionVisibility == Visibility.Visible,
                    "Card shows the pinned installable release"
                );
                card.SetGame(enhanced);
                Check(card.InstallEnabled == component.Enhanced, "Enhanced cannot invoke unsupported library install");
                Check(
                    card.InstallVisibility == (component.Enhanced ? Visibility.Visible : Visibility.Collapsed),
                    "Enhanced hides unsupported install buttons"
                );
                card.SetGame(legacy);
                card.InstallState = "Downloading";
                Check(!card.InstallEnabled, "Busy library cannot start twice");
                card.InstallState = null;
                card.SetGame(null);
                Check(
                    !card.InstallEnabled && card.InstallVisibility == Visibility.Collapsed,
                    "No selected game cannot install a library"
                );
            }
            Console.WriteLine($"Script library UI checks passed: {_assertions} assertions.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Check(bool result, string description)
    {
        if (!result)
            throw new InvalidOperationException(description);
        _assertions++;
    }

    private sealed class Line : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new(nameof(Text)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
