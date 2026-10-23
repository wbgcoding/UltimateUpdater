namespace UltimateUpdater.Services.Cleanup;

/// <summary>One <see cref="DriverDeviceCandidate"/> as it crosses the pipe.</summary>
public sealed class DriverDeviceCandidateDto
{
    public string InstanceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string PackageName { get; set; } = string.Empty;

    public DateTimeOffset? LastConnected { get; set; }

    public DriverLastSeenKind LastConnectedKind { get; set; }

    public string? Provider { get; set; }

    public string? OriginalName { get; set; }

    public string? StorePath { get; set; }

    public string? Version { get; set; }

    public DateTimeOffset? Date { get; set; }

    public long SizeBytes { get; set; }

    public string? NewerVersion { get; set; }

    public DateTimeOffset? NewerDate { get; set; }
}
