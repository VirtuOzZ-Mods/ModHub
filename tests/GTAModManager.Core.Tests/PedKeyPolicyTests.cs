using GTAModManager.Core.Keybinds;

namespace GTAModManager.Core.Tests;

public class PedKeyPolicyTests
{
    [Theory]
    [InlineData(0x26, KeyMods.None)]
    [InlineData(0x60, KeyMods.None)]
    [InlineData(0x0d, KeyMods.None)]
    [InlineData(0x4b, KeyMods.Ctrl)]
    public void Selector_rejects_menu_navigation_keys_and_chords(int vk, KeyMods mods)
    {
        var entry = Assert.Single(KeybindScanner.ParseFile("GMMPedSelector.ini", "[Menu]\nMenuKey=L ; Default L\n"));
        var preview = KeybindEditor.Preview(entry, new KeyChord(vk, mods), [entry]);
        Assert.False(preview.Allowed);
    }

    [Fact]
    public void Other_mods_keep_existing_chord_support()
    {
        var entry = Assert.Single(KeybindScanner.ParseFile("Other.ini", "[Menu]\nMenuKey=L\n"));
        Assert.True(KeybindEditor.Preview(entry, new KeyChord(0x26), [entry]).Allowed);
    }
}
