using System.Windows.Media;
using GTAModManager.Core.Discover;
using GTAModManager.Core.Localization;

namespace GTAModManager.App.Discover;

/// <summary>Label, brand color and tooltip of an author link, shared by Recommendations and My Mods.</summary>
public static class AuthorLinks
{
    public static LinkView View(AuthorLink l)
    {
        var (label, color, tip) = l.Kind switch
        {
            LinkKind.YouTube => ("YouTube", 0xE52D27, L.T("ui.discover.authorSYoutube")),
            LinkKind.Discord => ("Discord", 0x5865F2, L.T("ui.discover.authorSDiscord")),
            LinkKind.Patreon => ("Patreon", 0xF96854, L.T("ui.discover.authorSPatreon")),
            LinkKind.GitHub => ("GitHub", 0xC9D1D9, "GitHub"),
            LinkKind.Steam => ("Steam", 0x66C0F4, "Steam"),
            LinkKind.SocialClub => ("Social Club", 0xF5A623, "Rockstar Games Social Club"),
            LinkKind.Twitch => ("Twitch", 0x9146FF, "Twitch"),
            LinkKind.Twitter => ("X", 0xE7E9EA, "X (Twitter)"),
            LinkKind.Instagram => ("Instagram", 0xE1306C, "Instagram"),
            LinkKind.TikTok => ("TikTok", 0x25F4EE, "TikTok"),
            LinkKind.KoFi => ("Ko-fi", 0x29ABE0, "Ko-fi"),
            LinkKind.Boosty => ("Boosty", 0xF15F2C, "Boosty"),
            _ => (L.T("ui.discover.website"), 0xFF9900, L.T("ui.discover.authorSWebsite")),
        };
        return new(label, "", Brushes[color], l.Url, tip);
    }

    static readonly Dictionary<int, Brush> Brushes = new[]
    {
        0xE52D27,
        0x5865F2,
        0xF96854,
        0xC9D1D9,
        0x66C0F4,
        0xF5A623,
        0x9146FF,
        0xE7E9EA,
        0xE1306C,
        0x25F4EE,
        0x29ABE0,
        0xF15F2C,
        0xFF9900,
    }.ToDictionary(
        c => c,
        c =>
        {
            var b = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            b.Freeze();
            return (Brush)b;
        }
    );
}
