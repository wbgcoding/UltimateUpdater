namespace UltimateUpdater.Services.Cleanup;

/// <summary>One <see cref="DriverPackageCandidate"/> as it crosses the pipe.</summary>
public sealed class DriverPackageCandidateDto
{
    public string PackageName { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string ClassName { get; set; } = string.Empty;

    public string? Version { get; set; }

    public DateTimeOffset? Date { get; set; }

    public long SizeBytes { get; set; }

    public string? OriginalName { get; set; }

    public string? StorePath { get; set; }

    public string? NewerVersion { get; set; }

    public DateTimeOffset? NewerDate { get; set; }
}
