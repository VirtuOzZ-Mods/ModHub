using GTAModManager.Core.Peds;

namespace GTAModManager.Core.Tests;

public sealed class PedReadmeParserTests
{
    [Fact]
    public void Cyberpunk_readme_preserves_authorship_credits_installation_and_social_provenance()
    {
        const string text = """
            Readme.txt
            GTA V - Cyberpunk Custom Female Ped -Fan Project-
            by alex189 & saldin93 (v1.0)
            --------------------
            Follow us:
            Facebook: alex189moddings
            Instagram: alex189mods
            Patreon: https://www.patreon.com/user?u=4791823
            5 Mods: https://www.gta5-mods.com/users/alex189/files
            --------------------
            Mod Feature:
            > Fully Rigged
            --------------------
            Credits:
            List of Games model used:
            Sudden Attack 2 - Cloths
            > Rigging, conversion & 3D Editing by alex189
            --------------------
            INSTALLATION - Add-on Method
            1. Put Vb into peds.rpf
            2. Female and False (Is Streamed)
            --------------------
            INSTALLATION - Replace Method
            1. Follow the author's replacement guide.
            --------------------
            © 2018 alex189
            """;
        var details = PedReadmeParser.Parse(text);
        Assert.Equal("GTA V - Cyberpunk Custom Female Ped -Fan Project-", details.Title);
        Assert.Equal("alex189 & saldin93 (v1.0)", details.Authors);
        Assert.Contains("List of Games model used:", details.Credits);
        Assert.Contains("Sudden Attack 2 - Cloths", details.Credits);
        Assert.Contains("> Rigging, conversion & 3D Editing by alex189", details.Credits);
        Assert.DoesNotContain("Fully Rigged", details.Credits);
        Assert.Contains("INSTALLATION - Add-on Method", details.Installation);
        Assert.Contains("INSTALLATION - Replace Method", details.Installation);
        Assert.Contains("Female and False (Is Streamed)", details.Installation);
        Assert.DoesNotContain("© 2018", details.Installation);
        Assert.Contains(details.Links, l => l.Url == "https://www.facebook.com/alex189moddings" && l.Inferred);
        Assert.Contains(details.Links, l => l.Url == "https://www.instagram.com/alex189mods" && l.Inferred);
        Assert.Contains(details.Links, l => l.Url == "https://www.patreon.com/user?u=4791823" && !l.Inferred);
        Assert.Contains(details.Links, l => l.Url == "https://www.gta5-mods.com/users/alex189/files" && !l.Inferred);
    }

    [Fact]
    public void Russian_headings_and_multiline_author_block_are_recognized_conservatively()
    {
        var details = PedReadmeParser.Parse(
            "Название модели\nАвторы:\nАвтор модели: Иван\nКонвертация: Пётр\n\nБлагодарности:\nСпасибо разработчикам игры.\n\nУстановка:\nСкопируйте выбранный комплект.\n\nОсобенности:\nНовая модель\n"
        );
        Assert.Equal("Название модели", details.Title);
        Assert.Contains("Автор модели: Иван", details.Authors);
        Assert.Contains("Конвертация: Пётр", details.Authors);
        Assert.Equal("Спасибо разработчикам игры.", details.Credits);
        Assert.Contains("Скопируйте выбранный комплект.", details.Installation);
        Assert.DoesNotContain("Новая модель", details.Installation);
    }

    [Fact]
    public void Links_are_safe_exact_deduplicated_and_keep_balanced_parentheses_and_query()
    {
        var details = PedReadmeParser.Parse(
            "Model\nAuthor: Example\nhttps://example.com/a_(b)?x=1&y=2.\n(https://example.com/a_(b)?x=1&y=2).\nhttps://user:secret@example.com/private\nhttp://example.org/guide#part!\njavascript:alert(1)\nfile:///C:/private\n"
        );
        Assert.Equal("Example", details.Authors);
        Assert.Single(details.Links, l => l.Url == "https://example.com/a_(b)?x=1&y=2");
        Assert.Contains(details.Links, l => l.Url == "http://example.org/guide#part");
        Assert.DoesNotContain(details.Links, l =>
            l.Url?.Contains("secret") == true
            || l.Url?.StartsWith("file:") == true
            || l.Url?.StartsWith("javascript:") == true
        );
    }

    [Fact]
    public void Profiles_do_not_invent_authors_and_ambiguous_handles_do_not_invent_links()
    {
        var details = PedReadmeParser.Parse(
            "Cyberpunk\\Readme.md\nModel Name\nInstagram: two people\nFacebook: ../bad\nhttps://www.gta5-mods.com/users/someone/files\nUnknown section\nFree text remains in the full README."
        );
        Assert.Equal("Model Name", details.Title);
        Assert.Empty(details.Authors);
        Assert.DoesNotContain(details.Links, l => l.Inferred);
        Assert.Single(details.Links);
    }

    [Fact]
    public void Empty_and_large_untrusted_readmes_are_bounded()
    {
        Assert.Equal(PedReadmeDetails.Empty, PedReadmeParser.Parse(" \n\t"));
        var details = PedReadmeParser.Parse(
            "Model\n"
                + string.Join('\n', Enumerable.Range(0, 500).Select(i => "https://example.com/" + i))
                + new string('x', 500000)
        );
        Assert.InRange(details.Links.Count, 1, 64);
        Assert.True(details.Title.Length <= 300);
        Assert.True(details.Credits.Length <= 65536);
        Assert.True(details.Installation.Length <= 131072);
    }
}
