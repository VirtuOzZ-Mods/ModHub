using System.Text;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Tests;

public sealed class DiscoverCatalogLocalizationTests
{
    [Fact]
    public void Built_in_curated_copy_has_tokens_and_preserves_third_party_and_machine_fields()
    {
        using var stream = typeof(DiscoverCatalog).Assembly.GetManifestResourceStream(
            "GTAModManager.Discover.catalog.json"
        )!;
        var raw = DiscoverCatalog.Parse(stream);
        var builtin = DiscoverCatalog.LoadBuiltIn();
        var service = LocalizationService.Current;
        Assert.Equal(raw.Version, builtin.Version);
        foreach (var pair in raw.Categories.Zip(builtin.Categories))
        {
            Assert.Equal(pair.First.Id, pair.Second.Id);
            Assert.Equal(pair.First.DefaultSort, pair.Second.DefaultSort);
            Assert.Equal(pair.First.Hero, pair.Second.Hero);
            Assert.Equal($"catalog.category.{pair.First.Id}.title", service.Describe(pair.Second.Title)?.Key);
            if (pair.First.Subtitle.Length > 0)
                Assert.Equal($"catalog.category.{pair.First.Id}.subtitle", service.Describe(pair.Second.Subtitle)?.Key);
        }
        foreach (var pair in raw.Entries.Zip(builtin.Entries))
        {
            Assert.Equal(pair.First, pair.Second with { Kind = pair.First.Kind, Compat = pair.First.Compat });
            Assert.Null(service.Describe(pair.Second.Title));
            if (!string.IsNullOrEmpty(pair.First.Kind))
                Assert.Equal($"catalog.entry.{pair.First.Id}.kind", service.Describe(pair.Second.Kind!)?.Key);
            if (!string.IsNullOrEmpty(pair.First.Compat?.Note))
                Assert.Equal($"catalog.entry.{pair.First.Id}.note", service.Describe(pair.Second.Compat!.Note!)?.Key);
        }
    }

    [Fact]
    public void External_parse_with_builtin_ids_preserves_all_provided_words_without_tokens()
    {
        const string json = """
            {"version":1,"categories":[{"id":"essentials","title":"External title","subtitle":"External description"}],
             "entries":[{"id":"scripthookv","category":"essentials","title":"Author title","author":"Author","url":"https://example.com/mod",
               "kind":"Author kind","compat":{"legacy":true,"note":"Author note"}}]}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var parsed = DiscoverCatalog.Parse(stream);
        Assert.Equal("External title", parsed.Categories.Single().Title);
        Assert.Equal("External description", parsed.Categories.Single().Subtitle);
        Assert.Equal("Author kind", parsed.Entries.Single().Kind);
        Assert.Equal("Author note", parsed.Entries.Single().Compat!.Note);
        Assert.Null(LocalizationService.Current.Describe(parsed.Categories.Single().Title));
        Assert.Null(LocalizationService.Current.Describe(parsed.Entries.Single().Compat!.Note!));
    }
}
