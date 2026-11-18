using System.Text.RegularExpressions;
using Xunit;

namespace GTAModManager.Core.Tests;

/// <summary>The developer guide (Settings → Developer guide) exists in Russian and English and the two stay in step.</summary>
public class DeveloperGuideTests
{
    [Fact]
    public void Russian_and_english_guides_have_the_same_sections()
    {
        string dir = Path.Combine(RepoRoot(), "docs", "developer");
        string ru = File.ReadAllText(Path.Combine(dir, "developer-guide.ru.md"));
        string en = File.ReadAllText(Path.Combine(dir, "developer-guide.en.md"));
        Assert.Equal(Headings(ru).Count, Headings(en).Count);
        Assert.Equal(Regex.Matches(ru, "^```", RegexOptions.Multiline).Count, Regex.Matches(en, "^```", RegexOptions.Multiline).Count);
        // Every code identifier mentioned in Russian is mentioned in English too.
        Assert.Empty(InlineCode(ru).Except(InlineCode(en)));
    }

    private static HashSet<string> InlineCode(string text) =>
        Regex
            .Matches(Regex.Replace(text, "^```.*?^```", "", RegexOptions.Multiline | RegexOptions.Singleline), "`([^`\n]+)`")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

    private static List<string> Headings(string text) =>
        Regex.Matches(text, "^#{1,3} .*$", RegexOptions.Multiline).Select(m => m.Value).ToList();

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "GTAModManager.slnx")))
                return dir.FullName;
        throw new DirectoryNotFoundException("GTAModManager.slnx not found above the test folder.");
    }
}
