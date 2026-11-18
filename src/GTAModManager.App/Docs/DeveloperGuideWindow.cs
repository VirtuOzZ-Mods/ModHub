using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Docs;

/// <summary>
/// Settings → Developer guide. The guide is docs/developer/developer-guide.{ru,en}.md, embedded in the app, so the
/// repository and the program show the same text. Russian UI opens the Russian guide, any other language the English
/// one. Only the Markdown the guide uses is rendered: headings, paragraphs, lists, code blocks, `code` and **bold**.
/// </summary>
public static partial class DeveloperGuideWindow
{
    public static void Show(Window owner)
    {
        string language = LocalizationService.Current.Culture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        string markdown = Load(language);
        var document = Render(markdown);
        var window = new Window
        {
            Owner = owner,
            Title = L.T("ui.devDocs.title"),
            Width = 920,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Res("Bg"),
            Content = new FlowDocumentScrollViewer
            {
                Document = document,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Foreground = Res("Text"),
            },
        };
        window.Show();
    }

    public static string Load(string language)
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream($"DeveloperGuide.developer-guide.{language}.md")
            ?? throw new FileNotFoundException("Developer guide is not embedded.", language);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 14,
            LineHeight = 22,
            PagePadding = new Thickness(36, 28, 36, 36),
            Foreground = Res("Text"),
            Background = Res("Bg"),
        };
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        List? list = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
                return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
            AddInlines(p.Inlines, string.Join(" ", paragraph));
            doc.Blocks.Add(p);
            paragraph.Clear();
        }
        void FlushList()
        {
            if (list is null)
                return;
            doc.Blocks.Add(list);
            list = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```"))
            {
                FlushParagraph();
                FlushList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("```"); i++)
                    code.Add(lines[i]);
                doc.Blocks.Add(
                    new Paragraph(new Run(string.Join("\n", code)))
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 13,
                        LineHeight = 19,
                        Background = Res("Card"),
                        Padding = new Thickness(14, 10, 14, 10),
                        Margin = new Thickness(0, 4, 0, 14),
                    }
                );
                continue;
            }
            var heading = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
            if (heading.Success)
            {
                FlushParagraph();
                FlushList();
                int level = heading.Groups[1].Length;
                var h = new Paragraph
                {
                    FontFamily = new FontFamily("Bahnschrift SemiBold, Bahnschrift, Segoe UI"),
                    FontSize = level switch
                    {
                        1 => 26,
                        2 => 20,
                        _ => 16.5,
                    },
                    Margin = new Thickness(0, level == 1 ? 0 : 14, 0, 8),
                    Foreground = level == 2 ? Res("Accent") : Res("Text"),
                };
                AddInlines(h.Inlines, heading.Groups[2].Value);
                doc.Blocks.Add(h);
                continue;
            }
            var item = Regex.Match(line, @"^\s*(?:-|\d+\.)\s+(.*)$");
            if (item.Success)
            {
                FlushParagraph();
                list ??= new List
                {
                    MarkerStyle = Regex.IsMatch(line, @"^\s*\d+\.") ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 12),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                AddInlines(p.Inlines, item.Groups[1].Value);
                list.ListItems.Add(new ListItem(p));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                FlushList();
                continue;
            }
            if (list is not null && line.StartsWith("  ") && list.ListItems.LastListItem?.Blocks.LastBlock is Paragraph last)
            {
                // Continuation of a list item.
                last.Inlines.Add(new Run(" "));
                AddInlines(last.Inlines, line.Trim());
                continue;
            }
            FlushList();
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        FlushList();
        return doc;
    }

    /// <summary>Inline markup: `code` and **bold**.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Groups["code"].Success)
                inlines.Add(
                    new Run(m.Groups["code"].Value)
                    {
                        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 13,
                        Background = Res("Card"),
                    }
                );
            else if (m.Groups["bold"].Success)
                inlines.Add(new Bold(new Run(m.Groups["bold"].Value)));
            else
                inlines.Add(new Run(m.Value));
        }
    }

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*|[^`*]+|[`*]")]
    private static partial Regex InlineToken();

    private static Brush Res(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Transparent;
}
