using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

[SupportedOSPlatform("windows")]
public sealed class GogGalaxySource(LoggingService logging) : LauncherSourceBase(logging)
{
    public override string Id => "gog";

    public override UpdateCategory Category => UpdateCategory.Games;

    protected override string DisplayName => "GOG Galaxy";

    protected override string? ResolveOpenTarget()
    {
        var client = ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client")
                     ?? ReadRegistryString(Registry.LocalMachine, @"SOFTWARE\GOG.com\GalaxyClient\paths", "client");

        if (client is null || !Directory.Exists(client))
            return null;

        return FirstExistingPath(Path.Combine(client, "GalaxyClient.exe"));
    }
}
