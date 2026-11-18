using System.Globalization;
using System.Text;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Tests;

public sealed class OfficialLanguageCoverageTests
{
    [Theory]
    [InlineData("official.en")]
    [InlineData("official.uk")]
    public void Every_official_translation_has_all_source_keys_and_renders_without_fallback(string id)
    {
        var service = new LocalizationService();
        var source = service.Packs.Single(pack => pack.Id == "official.ru");
        var translated = service.Packs.Single(pack => pack.Id == id);
        Assert.Equal(source.Strings.Keys.Order(), translated.Strings.Keys.Order());
        Assert.True(service.Select(id));
        foreach (var (key, value) in translated.Strings)
        {
            var template = value.GetString()!;
            var format = CompositeFormat.Parse(template);
            var args = Enumerable.Repeat<object?>("sample", format.MinimumArgumentCount).ToArray();
            Assert.Equal(
                string.Format(CultureInfo.GetCultureInfo(translated.Culture), template, args),
                service.Text(key, args)
            );
        }
        Assert.Empty(service.Diagnostics);
    }

    [Theory]
    [InlineData("official.en", "Unsupported language pack schema version.")]
    [InlineData("official.uk", "Непідтримувана версія схеми мовного пакета.")]
    public void Own_validation_errors_use_the_selected_language(string id, string expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "modhub-translation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "bad.json"), "{\"schemaVersion\":99}");
            var service = new LocalizationService();
            service.Reload(folder);
            service.Select(id);
            Assert.Contains(service.LocalizedDiagnostics, message =>
                message.Contains(expected, StringComparison.Ordinal)
            );
            Assert.Contains(service.Diagnostics, message =>
                message.Contains("Unsupported schemaVersion.", StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
