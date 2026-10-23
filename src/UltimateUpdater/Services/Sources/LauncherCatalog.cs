using System.IO;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The launcher clients this app knows how to find. A client is listed only where there is something
/// real to look for; an invented key would produce a row for software that is not installed at all.
/// </summary>
public static class LauncherCatalog
{
    public static IReadOnlyList<LauncherDefinition> Known()
    {
        var programFiles = ProgramFilesPaths.ProgramFiles;
        var programFilesX86 = ProgramFilesPaths.ProgramFilesX86;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        return
        [
            new LauncherDefinition
            {
                Id = "battlenet",
                DisplayName = "Battle.net",
                Executables = ["Battle.net Launcher.exe"],
                UninstallDisplayNames = ["Battle.net"],
                FallbackPaths =
                [
                    Path.Combine(programFilesX86, "Battle.net", "Battle.net Launcher.exe"),
                    Path.Combine(programFiles, "Battle.net", "Battle.net Launcher.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "ea",
                DisplayName = "EA app",
                Executables = ["EADesktop.exe", "Origin.exe"],
                // the EA app replaced Origin, which recorded its own client path under this key
                ExecutablePathValues = [new RegistryValue(@"HKLM\SOFTWARE\WOW6432Node\Origin", "ClientPath")],
                UninstallDisplayNames = ["EA app", "Origin"],
                FallbackPaths =
                [
                    Path.Combine(programFiles, "Electronic Arts", "EA Desktop", "EA Desktop", "EADesktop.exe"),
                    Path.Combine(programFilesX86, "Origin", "Origin.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "ubisoft",
                DisplayName = "Ubisoft Connect",
                Executables = ["UbisoftConnect.exe", "upc.exe", "UplayWebCore.exe"],
                InstallDirectoryValues = [new RegistryValue(@"HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir")],
                UninstallDisplayNames = ["Ubisoft Connect", "Uplay"],
                FallbackPaths =
                [
                    Path.Combine(programFilesX86, "Ubisoft", "Ubisoft Game Launcher", "UbisoftConnect.exe"),
                    Path.Combine(programFilesX86, "Ubisoft", "Ubisoft Game Launcher", "UplayWebCore.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "rockstar",
                DisplayName = "Rockstar Games Launcher",
                Executables = ["Launcher.exe"],
                InstallDirectoryValues = [new RegistryValue(@"HKLM\SOFTWARE\WOW6432Node\Rockstar Games\Launcher", "InstallFolder")],
                UninstallDisplayNames = ["Rockstar Games Launcher"],
                FallbackPaths =
                [
                    Path.Combine(programFiles, "Rockstar Games", "Launcher", "Launcher.exe"),
                    Path.Combine(programFilesX86, "Rockstar Games", "Launcher", "Launcher.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "riot",
                DisplayName = "Riot Client",
                Executables = ["RiotClientServices.exe"],
                UninstallDisplayNames = ["Riot Client"],
                // Riot's own default is the drive root, not Program Files: a Program-Files-only guess
                // would miss real installs.
                FallbackPaths =
                [
                    Path.Combine(systemDrive, "Riot Games", "Riot Client", "RiotClientServices.exe"),
                    Path.Combine(programData, "Riot Games", "Riot Client", "RiotClientServices.exe"),
                    Path.Combine(programFiles, "Riot Games", "Riot Client", "RiotClientServices.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "amazongames",
                DisplayName = "Amazon Games",
                // installs per user, so it is never under Program Files and never in the machine hive
                Executables = ["Amazon Games.exe"],
                UninstallDisplayNames = ["Amazon Games"],
                FallbackPaths = [Path.Combine(localAppData, "Amazon Games", "App", "Amazon Games.exe")],
            },
            new LauncherDefinition
            {
                Id = "itch",
                DisplayName = "itch.io",
                // per-user as well, and versioned: the uninstall entry's DisplayIcon is what points at
                // the current app-<version> folder.
                Executables = ["itch.exe"],
                UninstallDisplayNames = ["itch"],
                FallbackPaths = [Path.Combine(localAppData, "itch", "itch.exe")],
            },
            new LauncherDefinition
            {
                Id = "minecraft",
                DisplayName = "Minecraft Launcher",
                // only the standalone installer is findable; the Store edition of the launcher has no
                // executable path of its own and is covered by the Microsoft Store row.
                Executables = ["MinecraftLauncher.exe"],
                UninstallDisplayNames = ["Minecraft Launcher"],
                FallbackPaths =
                [
                    Path.Combine(programFilesX86, "Minecraft Launcher", "MinecraftLauncher.exe"),
                    Path.Combine(programFiles, "Minecraft Launcher", "MinecraftLauncher.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "paradox",
                DisplayName = "Paradox Launcher",
                // the games start the bootstrapper, which then updates and starts the versioned launcher
                Executables = ["bootstrapper-v2.exe", "Paradox Launcher.exe"],
                UninstallDisplayNames = ["Paradox Launcher"],
                FallbackPaths =
                [
                    Path.Combine(programFilesX86, "Paradox Interactive", "launcher", "bootstrapper-v2.exe"),
                    Path.Combine(programFiles, "Paradox Interactive", "launcher", "bootstrapper-v2.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "roblox",
                DisplayName = "Roblox",
                // per-user and versioned (…\Roblox\Versions\version-<hash>\), so there is no stable
                // path to fall back to: the uninstall entry is the only thing that points at the
                // copy currently installed.
                Executables = ["RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe"],
                UninstallDisplayNames = ["Roblox Player", "Roblox"],
            },
            new LauncherDefinition
            {
                Id = "hoyoplay",
                DisplayName = "HoYoPlay",
                // HoYoverse's launcher (Genshin, Honkai, Zenless); replaced the older "miHoYo Launcher".
                // The install directory is chosen by the user, so the uninstall entry is what finds it.
                Executables = ["HYP.exe", "launcher.exe"],
                UninstallDisplayNames = ["HoYoPlay", "miHoYo Launcher", "miHoYoLauncher"],
                FallbackPaths =
                [
                    Path.Combine(programFiles, "HoYoPlay", "launcher.exe"),
                    Path.Combine(programFiles, "miHoYo", "HoYoPlay", "launcher.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "wargaming",
                DisplayName = "Wargaming.net Game Center",
                Executables = ["wgc.exe"],
                UninstallDisplayNames = ["Wargaming.net Game Center"],
                FallbackPaths =
                [
                    Path.Combine(programData, "Wargaming.net", "GameCenter", "wgc.exe"),
                    Path.Combine(programFilesX86, "Wargaming.net", "GameCenter", "wgc.exe"),
                ],
            },
            new LauncherDefinition
            {
                Id = "garena",
                DisplayName = "Garena",
                Executables = ["Garena.exe", "GarenaMessenger.exe"],
                UninstallDisplayNames = ["Garena", "Garena PC"],
                FallbackPaths = [Path.Combine(programFilesX86, "Garena", "Garena.exe")],
            },

            // Dev-tool clients: same "detect the client, open it, it patches itself" shape as the game
            // launchers above, just a different Category. None of these three are installed on this dev
            // box, so the default paths/uninstall names below are the vendors' documented defaults rather
            // than something read off a real install (see README's "A note on newer sources").
            new LauncherDefinition
            {
                Id = "jetbrains-toolbox",
                DisplayName = "JetBrains Toolbox",
                Category = UpdateCategory.DeveloperTools,
                Executables = ["jetbrains-toolbox.exe"],
                UninstallDisplayNames = ["JetBrains Toolbox"],
                FallbackPaths = [Path.Combine(localAppData, "JetBrains", "Toolbox", "bin", "jetbrains-toolbox.exe")],
            },
            new LauncherDefinition
            {
                Id = "docker-desktop",
                DisplayName = "Docker Desktop",
                Category = UpdateCategory.DeveloperTools,
                Executables = ["Docker Desktop.exe"],
                UninstallDisplayNames = ["Docker Desktop"],
                FallbackPaths = [Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe")],
            },
            new LauncherDefinition
            {
                Id = "unity-hub",
                DisplayName = "Unity Hub",
                Category = UpdateCategory.DeveloperTools,
                Executables = ["Unity Hub.exe"],
                UninstallDisplayNames = ["Unity Hub"],
                // Older installers used the machine-wide Program Files path; 2023+ installers moved to a
                // per-user install. Both are real defaults depending on when the user installed it.
                FallbackPaths =
                [
                    Path.Combine(programFiles, "Unity Hub", "Unity Hub.exe"),
                    Path.Combine(localAppData, "Programs", "Unity Hub", "Unity Hub.exe"),
                ],
            },
        ];
    }
}
