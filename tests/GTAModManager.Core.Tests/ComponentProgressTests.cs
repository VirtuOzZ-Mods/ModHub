using System.Text;
using GTAModManager.Core.NaturalVision;
using GTAModManager.Core.Processes;

namespace GTAModManager.Core.Tests;

public sealed class ComponentProgressTests
{
    [Fact]
    public void Progress_round_trips_partial_package_count_without_localized_log_parsing()
    {
        var original = new NveProgress(NveProgressPhase.Installing, "HD Part SEVEN", 7, 12, 6);
        Assert.True(NveProgress.TryParse(original.ToLine(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.False(NveProgress.TryParse("NVE_PROGRESS:old human log", out _));
        Assert.False(NveProgress.TryParse(NveProgress.Prefix + "{broken", out _));
        Assert.False(
            NveProgress.TryParse(new NveProgress(NveProgressPhase.Installing, "Bad", 13, 12, 6).ToLine(), out _)
        );
        Assert.False(NveProgress.TryParse(new NveProgress(NveProgressPhase.Installed, "Bad", 2, 3, 4).ToLine(), out _));
        Assert.False(NveProgress.TryParse(new NveProgress((NveProgressPhase)999, "Bad", 1, 3, 0).ToLine(), out _));
    }

    [Fact]
    public async Task Elevated_log_is_relayed_before_exit_with_split_utf8_lines_and_final_tail()
    {
        string path = Path.Combine(Path.GetTempPath(), "gmm-live-" + Guid.NewGuid().ToString("N") + ".log");
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new SignalingWriter();
        var relay = LiveLogRelay.RelayAsync(path, exit.Task, output);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            byte[] line = Encoding.UTF8.GetBytes("Пакет 7 из 12\n");
            // Split the very first Cyrillic character across two writes.
            await file.WriteAsync(line.AsMemory(0, 1));
            await file.FlushAsync();
            await Task.Delay(150);
            Assert.False(output.Written.Task.IsCompleted);
            await file.WriteAsync(line.AsMemory(1));
            await file.FlushAsync();
            await output.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(exit.Task.IsCompleted);
            Assert.Equal("Пакет 7 из 12\n", output.ToString());
            await file.WriteAsync(Encoding.UTF8.GetBytes("последний фрагмент"));
            await file.FlushAsync();
            exit.SetResult();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Пакет 7 из 12\nпоследний фрагмент", output.ToString());
        }
        finally
        {
            exit.TrySetResult();
            await relay.WaitAsync(TimeSpan.FromSeconds(5));
            File.Delete(path);
        }
    }

    sealed class SignalingWriter : StringWriter
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteAsync(string? value)
        {
            base.Write(value);
            Written.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
