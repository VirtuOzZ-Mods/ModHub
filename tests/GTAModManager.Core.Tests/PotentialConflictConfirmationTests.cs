using GTAModManager.Core.Game;
using GTAModManager.Core.Keybinds;

namespace GTAModManager.Core.Tests;

public sealed class PotentialConflictConfirmationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gmm-key-ack-" + Guid.NewGuid().ToString("N"));

    public PotentialConflictConfirmationTests()
    {
        Directory.CreateDirectory(root);
    }

    public void Dispose() => Directory.Delete(root, true);

    GameInstall Game(string name, bool enhanced = false)
    {
        var p = Path.Combine(root, name);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, enhanced ? "GTA5_Enhanced.exe" : "GTA5.exe"), "stub");
        return GameInstall.Open(p);
    }

    KeybindConflict Conflict() =>
        new(
            ConflictKind.Potential,
            new()
            {
                Id = "a",
                ModId = "a",
                ModName = "A",
                Action = "Menu",
                Chord = new(76),
            },
            new()
            {
                Id = "b",
                ModId = "b",
                ModName = "B",
                Action = "Menu",
                Chord = new(76),
            },
            "Unknown context"
        );

    [Fact]
    public void Confirmation_survives_reload_but_not_assignment_change()
    {
        var game = Game("one");
        var c = Conflict();
        new PotentialConflictConfirmations(game, root).Confirm(c);
        var reload = new PotentialConflictConfirmations(game, root);
        Assert.True(reload.IsConfirmed(c));
        Assert.False(reload.IsConfirmed(c with { A = c.A with { Chord = new(77) } }));
        Assert.Empty(reload.Filter([c]));
    }

    [Fact]
    public void Installations_and_editions_are_independent_and_direct_conflicts_are_kept()
    {
        var game = Game("one");
        var c = Conflict();
        var store = new PotentialConflictConfirmations(game, root);
        store.Confirm(c);
        Assert.False(new PotentialConflictConfirmations(Game("two"), root).IsConfirmed(c));
        Assert.False(new PotentialConflictConfirmations(Game("enhanced", true), root).IsConfirmed(c));
        Assert.Single(store.Filter([c with { Kind = ConflictKind.Direct }]));
        Assert.Throws<ArgumentException>(() => store.Confirm(c with { Kind = ConflictKind.Direct }));
        store.Reset();
        Assert.False(new PotentialConflictConfirmations(game, root).IsConfirmed(c));
    }
}
