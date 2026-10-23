namespace UltimateUpdater.Services.Cleanup;

/// <summary>One broken registry reference, as it crosses the pipe; mirrors <see cref="RegistryFinding"/>.</summary>
public sealed class RegistryFindingDto
{
    public RegistryCleanupCategory Category { get; set; }

    public string KeyPath { get; set; } = string.Empty;

    public string? ValueName { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string BrokenTarget { get; set; } = string.Empty;
}
