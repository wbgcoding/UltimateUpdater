using System.IO;
using System.Runtime.Versioning;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

[SupportedOSPlatform("windows")]
public sealed class EpicGamesSource(LoggingService logging) : LauncherSourceBase(logging)
{
    public override string Id => "epic";

    public override UpdateCategory Category => UpdateCategory.Games;

    protected override string DisplayName => "Epic Games Launcher";

    // Resolved the same way as the other client launchers: the registry first, the vendor default
    // (a deep path under Program Files) last. The launcher lives several folders below its uninstall
    // InstallLocation, so the fixed path, not that location, is what actually finds it.
    private static readonly LauncherDefinition Definition = new()
    {
        Id = "epic",
        DisplayName = "Epic Games Launcher",
        Executables = ["EpicGamesLauncher.exe"],
        UninstallDisplayNames = ["Epic Games Launcher"],
        FallbackPaths =
        [
            Path.Combine(ProgramFilesPaths.ProgramFilesX86,
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
            Path.Combine(ProgramFilesPaths.ProgramFiles,
                "Epic Games", "Launcher", "Portal", "Binaries", "Win64", "EpicGamesLauncher.exe"),
        ],
    };

    protected override string? ResolveOpenTarget()
    {
        // The client executable is the reliable target: launching it opens Epic (which then patches
        // titles itself), and, unlike the com.epicgames.launcher:// deep link, which does not open a
        // usable view from outside the client, it also gives the row Epic's real icon rather than a
        // flat brand mark.
        var executable = LauncherResolver.Resolve(Definition, WindowsRegistryView.Instance, File.Exists);
        if (executable is not null)
            return executable;

        // Installed but not where expected: fall back to the deep link, gated on the manifests folder
        // so no row appears when Epic is not on the machine at all.
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        return Directory.Exists(manifests) ? "com.epicgames.launcher://apps" : null;
    }
}
