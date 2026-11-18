using GTAModManager.Core.Game;
using GTAModManager.Core.Mods;

namespace GTAModManager.Core.Tests;

/// <summary>Explicit game version tags in mod and package names, and the ordinary word "Enhanced" that is not a tag.</summary>
public sealed class EditionLabelTests
{
    [Theory]
    [InlineData("Los Santos Vice Map [Legacy]", GameEdition.Legacy)]
    [InlineData("DanceStyle Menu 1.0 [Legacy Version]", GameEdition.Legacy)]
    [InlineData("49ddd5-Los Santos Satellite Map (with Cayo Perico)  [Legacy Version]", GameEdition.Legacy)]
    [InlineData("NOS_1.9.5_Enhanced_Install", GameEdition.Enhanced)]
    [InlineData("NOS_1.9.5_Legacy_Install", GameEdition.Legacy)]
    [InlineData("Legacy", GameEdition.Legacy)]
    [InlineData("Enhanced", GameEdition.Enhanced)]
    [InlineData("Cool Car (Enhanced)", GameEdition.Enhanced)]
    [InlineData("Map Mod - Enhanced Edition", GameEdition.Enhanced)]
    [InlineData("Weapons for GTA V Enhanced", GameEdition.Enhanced)]
    [InlineData("Some Mod Gen9", GameEdition.Enhanced)]
    [InlineData("Nitro Overdrive System 1.9.5 — Purge Update | Legacy Edition 1.9", GameEdition.Legacy)]
    public void ExplicitLabels_AreRecognized(string name, GameEdition edition) =>
        Assert.Equal(edition, EditionLabel.Of(name));

    [Theory]
    [InlineData("Enhanced Native Trainer")] // a Legacy mod: "Enhanced" is just a word
    [InlineData("NaturalVision Enhanced - Next-Gen Graphics Update #3")]
    [InlineData("Enhanced Police Lights v2")]
    [InlineData("MPCharactersOverhaul_v2.4.2_NG")]
    [InlineData("NOS 1.9.5")]
    [InlineData("Mod for Legacy and Enhanced")] // both versions
    [InlineData("Legacy & Enhanced Data [Legacy] [Enhanced]")]
    public void NoOrBothLabels_AreNotAnEdition(string name) => Assert.Null(EditionLabel.Of(name));

    [Fact]
    public void Mismatch_OnlyForTheOtherEdition()
    {
        Assert.NotNull(EditionLabel.Mismatch("Vice Map [Legacy]", GameEdition.Enhanced, "Vice Map [Legacy].rar"));
        Assert.Null(EditionLabel.Mismatch("Vice Map [Legacy]", GameEdition.Legacy, "Vice Map [Legacy].rar"));
        Assert.Null(EditionLabel.Mismatch("Enhanced Native Trainer", GameEdition.Legacy, "ENT.zip"));
    }
}
