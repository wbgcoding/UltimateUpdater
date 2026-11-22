namespace UltimateUpdater.Services.Sources;

/// <summary>One package a tool lists as installed. <paramref name="RequiredByOthers"/>: true when another installed
/// package needs it, false when none does, null when the tool cannot say.</summary>
public sealed record InstalledPackage(string Id, string Name, string? Version, bool? RequiredByOthers = null);
