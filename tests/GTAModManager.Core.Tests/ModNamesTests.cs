using GTAModManager.Core.Mods;
using Xunit;

namespace GTAModManager.Core.Tests;

public class ModNamesTests
{
    [Theory]
    [InlineData("a9db86-Lara_V_1.5a", "Lara V 1.5a")]
    [InlineData("821791-Ada_V_v1.5", "Ada V v1.5")]
    [InlineData("d6dd92-CyberpunkGirlCustom_v1.1", "CyberpunkGirlCustom v1.1")]
    [InlineData("deface-tool", "deface-tool")]
    [InlineData("HeapAdjuster-ini", "HeapAdjuster-ini")]
    [InlineData("123456-", "123456-")]
    [InlineData("NOS 1.9.5 Legacy", "NOS 1.9.5 Legacy")]
    public void Upload_prefix_and_underscores_are_hidden(string raw, string shown) =>
        Assert.Equal(shown, ModNames.Pretty(raw));
}
