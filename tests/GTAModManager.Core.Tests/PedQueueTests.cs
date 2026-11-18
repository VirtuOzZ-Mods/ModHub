using GTAModManager.Core.Peds;

namespace GTAModManager.Core.Tests;

public class PedQueueTests
{
    static PedArchiveAnalysis Archive(string path, string spawn = "hero") =>
        new(path, [new("one", spawn, "default", PedLayout.Normal, [], [])], "readme", [], []);

    static PedSelection Choice(string name = "Hero") => new("one", name, PedGender.Female, PedLayout.Normal);

    [Fact]
    public void Multiple_archives_keep_choices_and_duplicate_drop_keeps_same_item()
    {
        var queue = new PedInstallQueue();
        var first = queue.Add(Archive("a.zip"));
        queue.Configure(first, [Choice("Custom name")]);
        var second = queue.Add(Archive("b.zip", "other"));
        queue.Configure(second, [Choice()]);
        Assert.Same(first, queue.Add(Archive("A.zip")));
        Assert.Equal(2, queue.Items.Count);
        var requests = queue.CreateRequests();
        Assert.Equal("Custom name", requests[0].Selections[0].DisplayName);
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public void Only_successful_items_are_cleared_and_failure_can_be_reconfigured()
    {
        var queue = new PedInstallQueue();
        var first = queue.Add(Archive("a.zip"));
        queue.Configure(first, [Choice()]);
        var second = queue.Add(Archive("b.zip", "other"));
        queue.Configure(second, [Choice()]);
        queue.ApplyOutcomes([new(first.ArchivePath, true, "ok"), new(second.ArchivePath, false, "Conflict")]);
        Assert.Same(second, Assert.Single(queue.Items));
        Assert.Equal(PedQueueStatus.Failed, second.State);
        Assert.Equal("Conflict", second.Message);
        queue.Configure(second, [Choice("Changed")]);
        Assert.Single(queue.CreateRequests());
    }

    [Fact]
    public void Unconfigured_or_empty_selection_cannot_start_installation()
    {
        var queue = new PedInstallQueue();
        var item = queue.Add(Archive("a.zip"));
        Assert.Throws<InvalidOperationException>(() => queue.CreateRequests());
        Assert.Throws<ArgumentException>(() => queue.Configure(item, []));
    }

    [Fact]
    public void Duplicate_game_names_across_archives_are_rejected_before_installation()
    {
        var queue = new PedInstallQueue();
        var a = queue.Add(Archive("a.zip"));
        var b = queue.Add(Archive("b.zip", "HERO"));
        queue.Configure(a, [Choice()]);
        queue.Configure(b, [Choice()]);
        Assert.Throws<InvalidOperationException>(() => queue.CreateRequests());
    }

    [Fact]
    public void Unselected_items_stay_in_queue_and_are_not_installed()
    {
        var queue = new PedInstallQueue();
        var a = queue.Add(Archive("a.zip"));
        queue.Configure(a, [Choice()]);
        var b = queue.Add(Archive("b.zip", "other"));
        b.Selected = false;
        Assert.Single(queue.CreateRequests());
        queue.ApplyOutcomes([new(a.ArchivePath, true, "ok")]);
        Assert.Same(b, Assert.Single(queue.Items));
    }
}
