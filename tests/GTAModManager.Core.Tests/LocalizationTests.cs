using System.Globalization;
using System.Text.Json;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Tests;

public sealed class LocalizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "modhub-l10n-" + Guid.NewGuid());

    public LocalizationTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    private void Pack(string id, string culture, object strings) =>
        File.WriteAllText(
            Path.Combine(directory, id + ".json"),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    id,
                    culture,
                    displayName = id,
                    author = "Test",
                    version = "1",
                    strings,
                }
            )
        );

    [Fact]
    public void Discovers_alternatives_without_changing_process_culture()
    {
        var original = CultureInfo.CurrentCulture;
        Pack("community", "uk-UA", new { greeting = "Привіт {0}" });
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.True(service.Select("community"));
        Assert.Equal("Привіт Alex", service.Text("greeting", "Alex"));
        Assert.Same(original, CultureInfo.CurrentCulture);
        Assert.False(service.Select("absent"));
        Assert.Equal("absent", service.Text("absent"));
    }

    [Theory]
    [InlineData("uk-UA", 1, "one")]
    [InlineData("uk-UA", 2, "few")]
    [InlineData("uk-UA", 11, "many")]
    [InlineData("uk-UA", 14, "many")]
    [InlineData("uk-UA", -22, "few")]
    [InlineData("ru-RU", 25, "many")]
    [InlineData("ru-RU", 1.5, "other")]
    [InlineData("en-US", 21, "other")]
    [InlineData("en-US", 1, "one")]
    public void Applies_language_plural_rules(string culture, double count, string expected)
    {
        Pack(
            "community",
            culture,
            new
            {
                count = new
                {
                    one = "one",
                    few = "few",
                    many = "many",
                    other = "other",
                },
            }
        );
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("community");
        Assert.Equal(expected, service.Text("count", count));
    }

    [Fact]
    public void Only_explicit_tokens_are_described_and_nested_arguments_rerender()
    {
        Pack("a", "en-US", new { outer = "Value {0}", inner = "First" });
        Pack("b", "uk-UA", new { outer = "Значення {0}", inner = "Другий" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("a");
        var first = service.Text("inner");
        var second = service.Text("inner");
        Assert.NotSame(first, second);
        Assert.Null(service.Describe("First"));
        var token = service.Describe(service.Text("outer", first));
        Assert.NotNull(token);
        service.Select("b");
        var rendered = service.Render(token!);
        Assert.Equal("Значення Другий", rendered);
        Assert.NotNull(service.Describe(rendered));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"id\":\"bad\",\"culture\":\"en-US\",\"displayName\":\"Bad\",\"strings\":{}}")]
    [InlineData(
        "{\"schemaVersion\":1,\"id\":\"bad\",\"culture\":\"not_a_culture\",\"displayName\":\"Bad\",\"strings\":{}}"
    )]
    [InlineData(
        "{\"schemaVersion\":1,\"id\":\"bad\",\"culture\":\"en\",\"displayName\":\"Bad\",\"strings\":{\"x\":\"one\",\"x\":\"two\"}}"
    )]
    [InlineData(
        "{\"schemaVersion\":1,\"id\":\"bad\",\"culture\":\"en\",\"displayName\":\"Bad\",\"strings\":{\"x\":{\"one\":\"one\"}}}"
    )]
    [InlineData("broken json")]
    public void Rejects_invalid_packs_with_diagnostics(string json)
    {
        File.WriteAllText(Path.Combine(directory, "bad.json"), json);
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.DoesNotContain(service.Packs, x => x.Id == "bad");
        Assert.NotEmpty(service.Diagnostics);
    }

    [Fact]
    public void Malformed_format_is_safe_and_reports_diagnostic()
    {
        Pack("broken", "en-US", new { value = "Bad {0" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("broken");
        Assert.Equal("value", service.Text("value", 4));
        Assert.NotEmpty(service.Diagnostics);
    }

    [Fact]
    public void Reload_notifies_and_falls_back_when_selection_disappears()
    {
        Pack("community", "uk-UA", new { x = "x" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("community");
        var events = 0;
        service.LanguageChanged += (_, _) => events++;
        File.Delete(Path.Combine(directory, "community.json"));
        service.Reload(directory);
        Assert.NotEqual("community", service.SelectedId);
        Assert.Equal(1, events);
    }

    [Fact]
    public void Exported_template_is_loadable_and_does_not_replace_official_pack()
    {
        var service = new LocalizationService();
        service.ExportTemplate(Path.Combine(directory, "template.json"));
        service.Reload(directory);
        Assert.Contains(service.Packs, p => p.Id == "custom");
        Assert.Equal("official.ru", service.SelectedId);
    }

    [Fact]
    public void Empty_text_does_not_capture_external_empty_values_or_throw()
    {
        Pack("empty", "en-US", new { empty = "" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("empty");
        Assert.Equal("", service.Text("empty"));
        Assert.Equal("", service.Text("empty"));
        Assert.Null(service.Describe(string.Empty));
    }

    [Fact]
    public void Rejects_duplicate_ids_and_protected_official_ids()
    {
        Pack("official.ru", "en-US", new { hijack = "Wrong" });
        Pack("first", "en-US", new { x = "First" });
        File.Copy(Path.Combine(directory, "first.json"), Path.Combine(directory, "second.json"));
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.Single(service.Packs, p => p.Id == "first");
        Assert.True(service.Packs.Single(p => p.Id == "official.ru").IsBuiltIn);
        Assert.True(service.Diagnostics.Count >= 2);
    }

    [Theory]
    [InlineData("Wrong {1}")]
    [InlineData("Wrong {0} {1}")]
    [InlineData("Wrong")]
    public void Incompatible_placeholder_sets_fall_back_to_official_source(string translation)
    {
        Pack("alternative", "en", new Dictionary<string, string> { ["cli.shared.error"] = translation });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("alternative");
        Assert.Equal("Error: Alex", service.Text("cli.shared.error", "Alex", "Extra"));
        Assert.Contains(service.Diagnostics, d => d.Contains("Placeholder indices"));
    }

    [Fact]
    public void Partial_packs_use_source_and_disappearing_pack_uses_same_culture_official()
    {
        Pack("alternative", "uk-UA", new { example = "Example" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("alternative");
        Assert.Equal("Помилка: Alex", service.Text("cli.shared.error", "Alex"));
        File.Delete(Path.Combine(directory, "alternative.json"));
        service.Reload(directory);
        Assert.Equal("official.uk", service.SelectedId);
    }

    [Fact]
    public void Rejects_size_key_count_and_string_length_limits()
    {
        File.WriteAllText(Path.Combine(directory, "oversize.json"), new string(' ', 2 * 1024 * 1024 + 1));
        Pack("longstring", "en", new { value = new string('x', 32769) });
        Pack("manykeys", "en", Enumerable.Range(0, 15001).ToDictionary(i => "key" + i, _ => "value"));
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.DoesNotContain(service.Packs, p => p.Id is "longstring" or "manykeys");
        Assert.Contains(service.Diagnostics, d => d.Contains("2 MB"));
        Assert.Contains(service.Diagnostics, d => d.Contains("32768"));
        Assert.Contains(service.Diagnostics, d => d.Contains("too many keys"));
    }

    [Fact]
    public void Embedded_source_and_template_retain_full_key_set()
    {
        var service = new LocalizationService();
        Assert.True(service.Packs.Single(p => p.Id == "official.ru").Strings.Count > 100);
        service.ExportTemplate(Path.Combine(directory, "template.json"));
        service.Reload(directory);
        Assert.Equal(
            service.Packs.Single(p => p.Id == "official.ru").Strings.Keys.Order(),
            service.Packs.Single(p => p.Id == "custom").Strings.Keys.Order()
        );
    }

    [Fact]
    public void Joined_tokens_rerender_while_external_data_and_separator_remain_untouched()
    {
        Pack("a", "en", new { inner = "First", outer = "Value {0}" });
        Pack("b", "uk", new { inner = "Другий", outer = "Значення {0}" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("a");
        var joined = service.Join(" | ", new string?[] { service.Text("inner"), "First", null });
        var descriptor = service.Describe(service.Text("outer", joined));
        service.Select("b");
        Assert.Equal("Значення Другий | First | ", service.Render(descriptor!));
    }

    [Fact]
    public void Raw_key_limit_includes_malformed_entries()
    {
        Pack("invalidmany", "en", Enumerable.Range(0, 15001).ToDictionary(i => "key" + i, _ => "Invalid {"));
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.DoesNotContain(service.Packs, p => p.Id == "invalidmany");
    }

    [Fact]
    public void Owned_adapter_translates_only_explicit_whole_messages_and_preserves_invariant_source()
    {
        Pack(
            "translated",
            "en",
            new Dictionary<string, string>
            {
                ["core.shared.sameVariantWasSelectedMore"] = "One option selected repeatedly.",
                ["cli.shared.error"] = "Error: {0}",
            }
        );
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("translated");
        const string original = "Один вариант выбран несколько раз.";
        Assert.Equal("One option selected repeatedly.", service.Owned(original));
        Assert.Equal("Один вариант выбран несколько раз.", original);
        Assert.Null(service.Describe(original));
        Assert.Equal("Readme: " + original, service.Owned("Readme: " + original));
        var token = service.Describe(service.Owned("Ошибка: User mod name"));
        Assert.NotNull(token);
        Assert.Equal("Error: User mod name", service.Render(token!));
        service.Select("official.ru");
        Assert.Equal("Ошибка: User mod name", service.Render(token!));
    }

    [Fact]
    public void Owned_multiline_cli_output_retains_line_endings_and_foreign_lines()
    {
        Pack(
            "translated",
            "en",
            new Dictionary<string, string>
            {
                ["cli.shared.error"] = "Error: {0}",
                ["core.shared.sameVariantWasSelectedMore"] = "Repeated selection.",
            }
        );
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("translated");
        var result = service.Owned("Ошибка: My mod\r\nReadme user text\nОдин вариант выбран несколько раз.\r\n");
        Assert.Equal("Error: My mod\r\nReadme user text\nRepeated selection.\r\n", result);
        var descriptor = service.Describe(result);
        Assert.NotNull(descriptor);
        service.Select("official.ru");
        Assert.Equal(
            "Ошибка: My mod\r\nReadme user text\nОдин вариант выбран несколько раз.\r\n",
            service.Render(descriptor!)
        );
    }

    [Fact]
    public void Owned_formatted_captures_stay_plain_and_weak_or_unapproved_templates_are_excluded()
    {
        Pack(
            "translated",
            "en",
            new Dictionary<string, string>
            {
                ["core.probe.allKeysFoundSAes"] = "Found in {0:F1} seconds.",
                ["core.library.sizeKilobytes"] = "Size {0}",
                ["ui.common.close"] = "Close",
            }
        );
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("translated");
        Assert.Equal(
            "Found in 1,2 seconds.",
            service.Owned("Все ключи найдены за 1,2 с: AES, 101 NG-ключ, 272 таблицы, LUT.")
        );
        Assert.Equal("32 КБ", service.Owned("32 КБ"));
        Assert.Null(service.Describe("32 КБ"));
        Assert.Equal("Закрыть", service.Owned("Закрыть"));
        Assert.Null(service.Describe("Закрыть"));
    }

    [Fact]
    public void Truncation_and_trim_reapply_after_language_changes_without_marking_foreign_data()
    {
        Pack("short", "en", new { inner = "Hi  " });
        Pack("long", "uk", new { inner = "Довгий текст   " });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("short");
        var trimmed = service.TrimEnd(service.Text("inner"));
        var truncated = service.Truncate(trimmed, 4);
        Assert.Equal("Hi", truncated);
        var descriptor = service.Describe(truncated);
        Assert.NotNull(descriptor);
        service.Select("long");
        Assert.Equal("Довг…", service.Render(descriptor!));
        Assert.Equal("raw…", service.Truncate("raw foreign", 3));
        Assert.Null(service.Describe(service.Truncate("raw foreign", 3)));
        Assert.Null(service.Describe(service.TrimEnd("foreign  ")));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Truncate("value", -1));
    }

    [Fact]
    public void Source_renders_nested_composition_and_transforms_in_russian_without_selection_or_culture_changes()
    {
        Pack("custom", "en", new Dictionary<string, string> { ["cli.shared.error"] = "Error: {0}" });
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("custom");
        var culture = CultureInfo.CurrentCulture;
        var events = 0;
        service.LanguageChanged += (_, _) => events++;
        var nested = service.Text("cli.shared.error", "mod");
        var joined = service.Join(" | ", new[] { nested, "External mod" });
        var outer = service.TrimEnd(service.Text("cli.shared.error", joined));
        Assert.Equal("Error: Error: mod | External mod", outer);
        Assert.Equal("Ошибка: Ошибка: mod | External mod", service.SourceText(outer));
        Assert.Equal("Ошибка…", service.SourceText(service.Truncate(nested, 6)));
        Assert.Equal("custom", service.SelectedId);
        Assert.Equal(0, events);
        Assert.Same(culture, CultureInfo.CurrentCulture);
        Assert.Equal("foreign", service.SourceText("foreign"));
        Assert.Null(service.Describe(service.SourceText(outer)));
    }

    [Fact]
    public void Owned_adapter_recognizes_legacy_persisted_queue_messages()
    {
        Pack(
            "custom",
            "en",
            new Dictionary<string, string> { ["ui.common.loadingBuiltBrowser"] = "Downloading in browser…" }
        );
        var service = new LocalizationService();
        service.Reload(directory);
        service.Select("custom");
        Assert.Equal("Downloading in browser…", service.Owned("Загрузка во встроенном браузере…"));
    }

    [Theory]
    [InlineData("Unsafe {0,2147483647}")]
    [InlineData("Unsafe {0,-4097}")]
    [InlineData("Unsafe {0:D999999999}")]
    [InlineData("Unsafe {0:F257}")]
    [InlineData("Unsafe {128}")]
    public void Unsafe_composite_formats_are_dropped_before_rendering(string translation)
    {
        const string key = "cli.shared.error";
        Pack("unsafe", "en", new Dictionary<string, string> { [key] = translation, ["safe"] = "Safe {0,10:D2}" });
        var service = new LocalizationService();
        service.Reload(directory);
        var pack = Assert.Single(service.Packs, p => p.Id == "unsafe");
        Assert.False(pack.Strings.ContainsKey(key));
        Assert.True(pack.Strings.ContainsKey("safe"));
        service.Select("unsafe");
        Assert.Equal("Error: Alex", service.Text(key, "Alex"));
        Assert.NotEmpty(service.Diagnostics);
    }

    [Theory]
    [InlineData("author", 257)]
    [InlineData("version", 129)]
    public void Oversized_optional_metadata_is_rejected(string property, int length)
    {
        var metadata = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1,
            ["id"] = "oversizedmeta",
            ["culture"] = "en",
            ["displayName"] = "Meta",
            ["strings"] = new { value = "Safe" },
            [property] = new string('x', length),
        };
        File.WriteAllText(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(metadata));
        var service = new LocalizationService();
        service.Reload(directory);
        Assert.DoesNotContain(service.Packs, p => p.Id == "oversizedmeta");
        Assert.NotEmpty(service.Diagnostics);
    }
}
