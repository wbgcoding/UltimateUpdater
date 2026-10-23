namespace UltimateUpdater.Services;

/// <summary>
/// Runs a fire-and-forget async operation so a thrown exception is never silently lost: the task is
/// awaited here, every exception except cancellation is caught and logged with the caller-supplied name
/// (so the log line says which background operation failed), and cancellation is treated as a normal way
/// for background work to end, not a failure to report.
/// </summary>
public static class Background
{
    public static async Task Run(Task task, LoggingService logging, string name)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logging.Error($"Background task '{name}' failed", ex);
        }
    }
}
