using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// The controls of one elevated job reach the worker in the order the state was decided in, also when the
/// pause is only replayed after the job went out and the send of it is slow.
/// </summary>
public class ElevatedJobHandleOrderTests
{
    private sealed class Wire
    {
        private readonly object _lock = new();
        public List<string> Sent { get; } = [];
        public TaskCompletionSource PauseMayLand { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Send(string id, string action, string? sourceId, string? itemId)
        {
            // a pause that is slow to reach the pipe, as when the send gate is busy
            if (action == "pause")
                await PauseMayLand.Task;

            lock (_lock)
                Sent.Add(action);
        }
    }

    [Fact]
    public async Task A_resume_never_overtakes_the_pause_replayed_after_the_attach()
    {
        var wire = new Wire();
        var handle = new ElevatedJobHandle();

        await handle.PauseAsync();
        handle.Attach(wire.Send, "job");
        var resume = handle.ResumeAsync();

        Assert.False(resume.IsCompleted);
        wire.PauseMayLand.SetResult();
        await resume.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["pause", "resume"], wire.Sent);
    }

    [Fact]
    public async Task A_resume_asked_before_the_attach_leaves_nothing_to_replay()
    {
        var wire = new Wire();
        wire.PauseMayLand.SetResult();
        var handle = new ElevatedJobHandle();

        await handle.PauseAsync();
        await handle.ResumeAsync();
        handle.Attach(wire.Send, "job");
        await handle.PauseAsync();
        await handle.ResumeAsync();

        Assert.Equal(["pause", "resume"], wire.Sent);
    }

    [Fact]
    public async Task A_pause_held_before_the_attach_is_replayed_before_a_stop_asked_before_it()
    {
        var wire = new Wire();
        wire.PauseMayLand.SetResult();
        var handle = new ElevatedJobHandle();

        await handle.PauseAsync();
        await handle.CancelAsync();
        await handle.CancelItemAsync("winget", "a");
        handle.Attach(wire.Send, "job");

        Assert.Equal(["pause", "cancel-item", "cancel"], wire.Sent);
    }
}
