namespace UltimateUpdater.Services.Cleanup;

/// <summary>One folder the leftover scanner found, as it crosses the pipe, the worker does the real
/// filesystem walk, so the app-side UI needs this shape rather than just a count.</summary>
public sealed class LeftoverCandidateDto
{
    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTimeOffset NewestWriteTime { get; set; }
}
