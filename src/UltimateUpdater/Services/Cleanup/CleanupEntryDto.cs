namespace UltimateUpdater.Services.Cleanup;

/// <summary>One <see cref="CleanupEntry"/> as it crosses the pipe.</summary>
public sealed class CleanupEntryDto
{
    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public long Bytes { get; set; }

    public int Files { get; set; }

    public DateTimeOffset OldestWriteTime { get; set; }

    public DateTimeOffset NewestWriteTime { get; set; }

    public CleanupEntryType Type { get; set; }

    /// <summary>Only a recycle-bin row has one (see <see cref="CleanupEntry.OriginalPath"/>). That target
    /// never crosses this pipe today (it needs no administrator) but a mapping that drops a field would
    /// fail silently the day it does.</summary>
    public string? OriginalPath { get; set; }
}
