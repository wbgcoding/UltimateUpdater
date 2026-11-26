namespace UltimateUpdater.Services.Sources.Windows;

/// <summary>A started asynchronous Windows Update operation (a download or install job), as far as the
/// polling loop needs it.</summary>
internal interface IWuAsyncJob
{
    bool IsCompleted { get; }

    /// <summary>Asks the agent to stop the operation; the job still has to be ended afterwards.</summary>
    void RequestAbort();

    /// <summary>Collects the operation's result (<c>EndDownload</c>/<c>EndInstall</c>).</summary>
    object? End();
}

/// <summary>
/// A Stop whose abort the agent did not honour in time: the job is given up and goes on in the background, so the
/// update may still change state after the Stop. It is a cancellation (callers that only care about "stopped" catch
/// <see cref="OperationCanceledException"/>) but a distinct one, so the source can tell it from a clean abort.
/// </summary>
internal sealed class WuOperationStillRunningException : OperationCanceledException
{
    public WuOperationStillRunningException(CancellationToken token)
        : base("The Windows Update operation did not stop in time and is still finishing in the background.", token)
    {
    }
}

/// <summary>
/// Drives a <see cref="IWuAsyncJob"/> to its end. The synchronous WUA calls cannot be interrupted, so a Stop
/// pressed during a long download only took effect after it; this polls with the token instead and, on a
/// cancel, asks the agent to abort and collects the job, all within a bounded time so the elevated worker
/// never hangs on a transfer that will not stop.
/// </summary>
internal static class WuAsyncRunner
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long an aborted job gets to wind down before it is given up on (and keeps running in the
    /// agent, unobserved). Short enough for a Stop to end the row in the 15 s the product promises.</summary>
    public static readonly TimeSpan DefaultAbortGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Returns the job's result once it completed. When <paramref name="cancellationToken"/> fires first:
    /// <c>RequestAbort</c>, wait up to <paramref name="abortGrace"/> for the job to finish, then <c>End</c>
    /// it (skipped when it never finished, since ending an unfinished job blocks) and throw
    /// <see cref="OperationCanceledException"/>; a job that outlived the grace throws the distinct
    /// <see cref="WuOperationStillRunningException"/> instead.
    /// </summary>
    public static object? Run(IWuAsyncJob job, CancellationToken cancellationToken,
        TimeSpan? pollInterval = null, TimeSpan? abortGrace = null, Action<string>? log = null)
    {
        var poll = pollInterval ?? DefaultPollInterval;
        var grace = abortGrace ?? DefaultAbortGrace;

        while (!job.IsCompleted)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (!Abort(job, poll, grace, log))
                    throw new WuOperationStillRunningException(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
            }

            // The token's wait handle, never Thread.Sleep: a Stop wakes the loop at once.
            cancellationToken.WaitHandle.WaitOne(poll);
        }

        return job.End();
    }

    /// <summary>A job the agent already tore down can fail its own status read; after an abort that means
    /// "over", and the failing <c>End</c> is then absorbed like any other.</summary>
    private static bool CompletedAfterAbort(IWuAsyncJob job)
    {
        try
        {
            return job.IsCompleted;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return true;
        }
    }

    /// <summary>Asks for the abort and waits the grace out. False = the job is still running and left to the agent.</summary>
    private static bool Abort(IWuAsyncJob job, TimeSpan poll, TimeSpan grace, Action<string>? log)
    {
        try
        {
            job.RequestAbort();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // An install past its point of no return refuses to abort; the wait below then simply runs out.
            log?.Invoke($"windowsupdate: the agent refused the abort request ({ex.Message}).");
        }

        var deadline = DateTime.UtcNow + grace;
        using var pause = new ManualResetEventSlim(false);
        while (!CompletedAfterAbort(job) && DateTime.UtcNow < deadline)
            pause.Wait(poll < TimeSpan.FromMilliseconds(100) ? poll : TimeSpan.FromMilliseconds(100));

        if (!CompletedAfterAbort(job))
        {
            log?.Invoke($"windowsupdate: the aborted operation did not end within {grace.TotalSeconds:0} s, it is left to finish in the background.");
            return false;
        }

        try
        {
            job.End();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // The result of an aborted job carries nothing the caller needs; the abort itself is the answer.
            log?.Invoke($"windowsupdate: ending the aborted operation reported: {ex.Message}");
        }

        return true;
    }
}
