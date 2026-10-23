using System.IO;
using System.Runtime.Versioning;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The vendor clients that expose NO per-title update URI (verified), so the row opens
/// the client executable, which patches itself and its titles on launch. WHERE that executable is
/// installed is asked of the registry rather than assumed; see LauncherResolver and LauncherCatalog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ClientLauncherSource(
    LoggingService logging,
    LauncherDefinition definition,
    IRegistryView registry) : LauncherSourceBase(logging)
{
    public override string Id => definition.Id;

    public override UpdateCategory Category => definition.Category;

    protected override string DisplayName => definition.DisplayName;

    protected override string? ResolveOpenTarget() => LauncherResolver.Resolve(definition, registry, File.Exists);

    public static IReadOnlyList<IUpdateSource> CreateKnown(LoggingService logging) =>
        LauncherCatalog.Known()
            .Select(known => (IUpdateSource)new ClientLauncherSource(logging, known, WindowsRegistryView.Instance))
            .ToList();
}
