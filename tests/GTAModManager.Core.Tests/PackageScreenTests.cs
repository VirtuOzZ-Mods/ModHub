using GTAModManager.Core.Keybinds;
using GTAModManager.Core.Mods;
using Xunit;

namespace GTAModManager.Core.Tests;

/// <summary>Package screen: bilingual descriptions and the hotkeys a package's .ini files bring.</summary>
public class PackageScreenTests
{
    [Fact]
    public void Marker_lines_split_a_bilingual_description_and_keep_the_preamble()
    {
        const string text =
            "Nitrous Oxide 1.9.5\n\n[ENG]\nAdds nitrous to every car. Press X to use it while driving fast.\n\n"
            + "[RU]\nДобавляет закись азота в любую машину. Нажмите X во время быстрой езды.";
        var parts = DescriptionLanguages.Split(text);
        Assert.Equal(2, parts.Count);
        var en = parts.Single(p => p.Language == "en");
        var ru = parts.Single(p => p.Language == "ru");
        Assert.StartsWith("Nitrous Oxide 1.9.5", en.Text);
        Assert.Contains("Adds nitrous", en.Text);
        Assert.DoesNotContain("закись", en.Text);
        Assert.Contains("закись азота", ru.Text);
        Assert.StartsWith("Nitrous Oxide 1.9.5", ru.Text);
    }

    [Fact]
    public void A_title_line_then_en_and_ru_markers_like_the_nos_package()
    {
        const string text =
            "N.O.S. 1.9.5 — Purge Update | GTA V Legacy\n\n[EN]\nAdds purge and cooling, new flame styles and a digital speedometer. Install into the mods folder.\n"
            + "UPDATE: removes the previous version first.\n\n[RU]\nДобавлены продувка и охлаждение, новые стили пламени и цифровой спидометр. Устанавливайте в папку mods.\n"
            + "ОБНОВЛЕНИЕ: сначала удаляет прошлую версию.";
        var parts = DescriptionLanguages.Split(text);
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.StartsWith("N.O.S. 1.9.5 — Purge Update", p.Text));
        Assert.Contains("UPDATE:", parts.Single(p => p.Language == "en").Text);
        Assert.Contains("ОБНОВЛЕНИЕ:", parts.Single(p => p.Language == "ru").Text);
        Assert.DoesNotContain("ОБНОВЛЕНИЕ", parts.Single(p => p.Language == "en").Text);
    }

    [Fact]
    public void Two_blocks_of_paragraphs_in_different_scripts_are_split_without_markers()
    {
        const string text =
            "This mod replaces the police cars with a modern fleet and adds new sirens.\n\nInstall it with OpenIV.\n\n"
            + "Этот мод заменяет полицейские машины современным автопарком и добавляет новые сирены.\n\nУстановка через OpenIV.";
        var parts = DescriptionLanguages.Split(text);
        Assert.Equal(["en", "ru"], parts.Select(p => p.Language));
        Assert.EndsWith("Install it with OpenIV.", parts[0].Text);
    }

    [Theory]
    [InlineData("Simple English description of a car mod with a few features and no translation at all.")]
    [InlineData("Описание мода на русском языке, где встречаются слова GTA V, OpenIV и Script Hook V.")]
    [InlineData(
        "English paragraph number one with enough letters.\n\nРусский абзац номер один с достаточным числом букв.\n\n"
            + "English paragraph number two with enough letters."
    )]
    public void A_single_language_or_interleaved_text_is_left_whole(string text)
    {
        var parts = DescriptionLanguages.Split(text);
        Assert.Single(parts);
        Assert.Equal(text, parts[0].Text);
    }

    [Fact]
    public void Hotkeys_of_a_package_ini_become_a_read_only_controls_group()
    {
        var group = PackageControls.FromIni(
            "[Controls]\nNitroKey=X\nMenuKey=Ctrl+F5\n; volume is not a key\nVolume=5\n",
            @"scripts\NOS.ini",
            new ModRef("", "NOS")
        );
        Assert.NotNull(group);
        Assert.Equal(@"scripts\NOS.ini", group.Title);
        Assert.Equal(2, group.Rows.Count);
        Assert.Contains(group.Rows, r => r.Keys.Count == 1 && r.Keys[0].Text == "X");
        Assert.Contains(group.Rows, r => r.Keys.Select(k => k.Text).SequenceEqual(["Ctrl", "+", "F5"]));
    }

    [Fact]
    public void An_ini_without_keys_gives_no_group() =>
        Assert.Null(PackageControls.FromIni("[Main]\nVolume=5\nEnabled=true\n", "x.ini", new ModRef("", "x")));
}
