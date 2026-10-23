using System.Runtime.Versioning;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>MS Store / Xbox: winget cannot list msstore versions (winget-cli#2854), so this is one row.</summary>
[SupportedOSPlatform("windows")]
public sealed class MicrosoftStoreSource(LoggingService logging) : LauncherSourceBase(logging)
{
    public override string Id => "msstore";

    public override UpdateCategory Category => UpdateCategory.Store;

    protected override string DisplayName => "Microsoft Store & Xbox";

    // the Store is in-box on Win10/11, and this URI opens its downloads-and-updates page directly
    protected override string? ResolveOpenTarget() => "ms-windows-store://downloadsandupdates";
}
