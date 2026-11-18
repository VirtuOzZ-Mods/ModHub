using GTAModManager.Core.Game;
using GTAModManager.Core.Localization;

namespace GTAModManager.Core.Keybinds;

/// <summary>
/// Standard GTA V keyboard controls, used only for hints and overlap detection.
/// This is the DEFAULT layout: the player's actual keys may differ (rebinding in the game settings),
/// and ModHub has no reliable way to read the user's control settings yet, so every entry
/// is marked as default. The table is intentionally short: well-known bindings only.
/// Legacy and Enhanced share these default keyboard bindings; ModHub never changes game settings.
/// </summary>
public static class GameKeybindProvider
{
    public const string ModId = "gtav";
    public const string Note = "Стандартное управление по умолчанию — ваши настройки в игре могут отличаться.";

    public static List<KeybindEntry> Defaults(GameEdition edition)
    {
        var name = edition == GameEdition.Enhanced ? "GTA V Enhanced" : "GTA V Legacy";
        var list = new List<KeybindEntry>();
        void Add(int vk, string action, KeybindContext ctx, KeyMods mods = KeyMods.None, KeySide side = KeySide.None) =>
            list.Add(
                new KeybindEntry
                {
                    Id = $"game|{ctx}|{action}",
                    ModId = ModId,
                    ModName = name,
                    Action = action,
                    Chord = new KeyChord(vk, mods, side),
                    Format = KeybindFormat.Game,
                    Status = KeybindStatus.Recognized,
                    StatusNote = Note,
                    Context = ctx,
                    Editable = false,
                    EditBlockReason = L.T("core.keybinds.gameControlsChangedGtaV"),
                }
            );

        // On foot
        Add('W', L.T("core.keybinds.forward"), KeybindContext.OnFoot);
        Add('S', L.T("core.keybinds.back"), KeybindContext.OnFoot);
        Add('A', L.T("core.keybinds.left"), KeybindContext.OnFoot);
        Add('D', L.T("core.keybinds.right"), KeybindContext.OnFoot);
        Add(0xA0, L.T("core.keybinds.sprint"), KeybindContext.OnFoot);
        Add(0x20, L.T("core.keybinds.jump"), KeybindContext.OnFoot);
        Add(0xA2, L.T("core.keybinds.stealthMovement"), KeybindContext.OnFoot);
        Add('Q', L.T("core.keybinds.takeCover"), KeybindContext.OnFoot);
        Add('R', L.T("core.keybinds.reload"), KeybindContext.OnFoot);
        Add('E', L.T("core.keybinds.action"), KeybindContext.OnFoot);
        Add('F', L.T("core.keybinds.enterVehicle"), KeybindContext.OnFoot);
        Add(0x09, L.T("core.keybinds.weaponWheel"), KeybindContext.OnFoot);
        // In a vehicle
        Add('W', L.T("core.keybinds.accelerate"), KeybindContext.Vehicle);
        Add('S', L.T("core.keybinds.brakeReverse"), KeybindContext.Vehicle);
        Add('A', L.T("core.keybinds.steerLeft"), KeybindContext.Vehicle);
        Add('D', L.T("core.keybinds.steerRight"), KeybindContext.Vehicle);
        Add(0x20, L.T("core.keybinds.handbrake"), KeybindContext.Vehicle);
        Add('E', L.T("core.keybinds.horn"), KeybindContext.Vehicle);
        Add('F', L.T("core.keybinds.exitVehicle"), KeybindContext.Vehicle);
        Add('H', L.T("core.keybinds.headlights"), KeybindContext.Vehicle);
        Add('C', L.T("core.keybinds.lookBehind"), KeybindContext.Vehicle);
        // Everywhere
        Add('V', L.T("core.keybinds.changeCamera"), KeybindContext.Global);
        Add(0x26, L.T("core.keybinds.phone"), KeybindContext.Global);
        Add(0x1B, L.T("core.keybinds.pause"), KeybindContext.Global);
        Add('P', L.T("core.keybinds.pauseMenuMap"), KeybindContext.Global);
        return list;
    }
}
