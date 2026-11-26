namespace UltimateUpdater.Services.Processes;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool TimedOut { get; init; }

    /// <summary>The run ended while processes the tool started were still alive (the tree wait gave up on an
    /// idle leftover). They may still use what the run set up, such as its temp folder.</summary>
    public bool TreeLeftRunning { get; init; }
}
