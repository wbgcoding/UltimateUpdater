using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Background thread mode lowers CPU, disk and memory priority for the calling thread only, so a thread
/// that goes back to the pool while still in it would slow down unrelated work. The scope must therefore
/// end on the very thread that began it.
/// </summary>
public class BackgroundWorkTests
{
    private sealed class FakeNative(bool beginSucceeds = true) : IThreadModeNative
    {
        public List<(string Call, int Thread)> Calls { get; } = [];

        public bool Begin()
        {
            lock (Calls)
                Calls.Add(("begin", Environment.CurrentManagedThreadId));
            return beginSucceeds;
        }

        public void End()
        {
            lock (Calls)
                Calls.Add(("end", Environment.CurrentManagedThreadId));
        }
    }

    [Fact]
    public void Enter_begins_and_ends_on_the_same_thread()
    {
        var native = new FakeNative();

        using (BackgroundWork.Enter(native))
        {
        }

        Assert.Equal(["begin", "end"], native.Calls.Select(c => c.Call));
        Assert.Equal(native.Calls[0].Thread, native.Calls[1].Thread);
    }

    [Fact]
    public void A_refused_begin_is_never_ended()
    {
        // Ending a mode that never began would fail too, and worse, could end a mode someone else began.
        var native = new FakeNative(beginSucceeds: false);

        using (BackgroundWork.Enter(native))
        {
        }

        Assert.Equal(["begin"], native.Calls.Select(c => c.Call));
    }

    [Fact]
    public void Disposing_on_another_thread_is_refused_and_still_ends_nothing_there()
    {
        var native = new FakeNative();
        var scope = BackgroundWork.Enter(native);
        var beginThread = Environment.CurrentManagedThreadId;

        // A plain thread joined synchronously: an await could resume this test on a different thread
        // than the one the scope began on.
        InvalidOperationException? other = null;
        var thread = new Thread(() =>
        {
            try
            {
                scope.Dispose();
            }
            catch (InvalidOperationException ex)
            {
                other = ex;
            }
        });
        thread.Start();
        thread.Join();

        Assert.NotNull(other);
        Assert.DoesNotContain(native.Calls, c => c.Call == "end" && c.Thread != beginThread);

        scope.Dispose(); // back on the owning thread: now it ends
        Assert.Equal(["begin", "end"], native.Calls.Select(c => c.Call));
    }

    [Fact]
    public async Task ScanAsync_runs_its_walk_inside_background_mode_and_leaves_it_before_the_thread_returns()
    {
        var native = new FakeNative();
        BackgroundWork.NativeForTest.Value = native;
        try
        {
            var service = new CleanupService(new LoggingService());
            var target = new CleanupTarget("t", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
            {
                Directories = [Path.Combine(Path.GetTempPath(), "wu-bg-missing-" + Guid.NewGuid())],
            };

            await service.ScanAsync(target, CancellationToken.None);
        }
        finally
        {
            BackgroundWork.NativeForTest.Value = null;
        }

        Assert.Equal(["begin", "end"], native.Calls.Select(c => c.Call));
        Assert.Equal(native.Calls[0].Thread, native.Calls[1].Thread);
    }

    [Fact]
    public async Task CleanAsync_does_not_use_background_mode()
    {
        // Only the measuring pass is lowered; the user waits on the deleting pass.
        var native = new FakeNative();
        BackgroundWork.NativeForTest.Value = native;
        try
        {
            var service = new CleanupService(new LoggingService());
            var target = new CleanupTarget("t", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
                RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
            {
                Directories = [Path.Combine(Path.GetTempPath(), "wu-bg-missing-" + Guid.NewGuid())],
            };

            await service.CleanAsync(target, progress: null, CancellationToken.None);
        }
        finally
        {
            BackgroundWork.NativeForTest.Value = null;
        }

        Assert.Empty(native.Calls);
    }
}
