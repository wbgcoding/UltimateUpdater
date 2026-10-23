namespace UltimateUpdater.Services;

/// <summary>
/// What a source is called when a person reads it. The ids are lowercase handles for settings keys,
/// history entries and the install protocol; showing them raw made the table say "windowsupdate".
/// Product names are not localized; where the product itself is written in lower case (winget, npm, pip)
/// that spelling is the correct one and is kept. The few names that are ordinary nouns around a product
/// ("VS Code extensions") come from the resources instead, see <see cref="NameKeys"/>.
/// </summary>
internal static class SourceNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winget"] = "winget",
        ["windowsupdate"] = "Windows Update",
        ["msstore"] = "Microsoft Store",
        ["chocolatey"] = "Chocolatey",
        ["scoop"] = "Scoop",
        ["pip"] = "pip",
        ["npm"] = "npm",
        ["pnpm"] = "pnpm",
        ["dotnet-tool"] = ".NET Tools",
        ["psgallery"] = "PowerShell Gallery",
        ["gem"] = "RubyGems",
        ["composer"] = "Composer",
        ["rustup"] = "rustup",
        ["vcpkg"] = "vcpkg",
        ["mise"] = "mise",
        ["android-sdk"] = "Android SDK",
        ["android-studio"] = "Android Studio SDK",
        ["gh-extension"] = "GitHub CLI extensions",
        ["pipx"] = "pipx",
        ["vscode-extension"] = "VS Code extensions",
        ["wsl"] = "Windows Subsystem for Linux",
        ["deno"] = "Deno",
        ["bun"] = "Bun",
        ["uv"] = "uv",
        ["uv-tool"] = "uv tools",
        ["gcloud"] = "Google Cloud CLI",
        ["ollama"] = "Ollama models",
        ["flutter"] = "Flutter SDK",
        ["volta"] = "Volta",
        ["pyenv-win"] = "pyenv-win",
        ["steam"] = "Steam",
        ["epic"] = "Epic Games",
        ["gog"] = "GOG Galaxy",
        ["battlenet"] = "Battle.net",
        ["ea"] = "EA app",
        ["ubisoft"] = "Ubisoft Connect",
        ["rockstar"] = "Rockstar Games",
        ["riot"] = "Riot Client",
        ["amazongames"] = "Amazon Games",
        ["itch"] = "itch.io",
        ["minecraft"] = "Minecraft Launcher",
        ["paradox"] = "Paradox Launcher",
        ["roblox"] = "Roblox",
        ["hoyoplay"] = "HoYoPlay",
        ["wargaming"] = "Wargaming.net Game Center",
        ["garena"] = "Garena",
        ["nvidia-driver"] = "NVIDIA",
        ["github-release"] = "GitHub Releases",
        ["jetbrains-toolbox"] = "JetBrains Toolbox",
        ["docker-desktop"] = "Docker Desktop",
        ["unity-hub"] = "Unity Hub",
    };

    /// <summary>
    /// The sources whose name is a plain noun ("models", "extensions") rather than a product name, so a
    /// German table does not say "Ollama models". <see cref="Names"/> still lists them, as the English
    /// fallback and for <see cref="IsKnownId"/>.
    /// </summary>
    private static readonly Dictionary<string, string> NameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dotnet-tool"] = "SourceNameDotnetTool",
        ["gh-extension"] = "SourceNameGhExtension",
        ["vscode-extension"] = "SourceNameVsCodeExtension",
        ["wsl"] = "SourceNameWsl",
        ["uv-tool"] = "SourceNameUvTool",
        ["ollama"] = "SourceNameOllama",
        ["github-release"] = "SourceNameGitHubRelease",
    };

    /// <summary>Falls back to the id, so a source added later is readable rather than missing. Display
    /// text only: settings, history and the install protocol keep storing the id.</summary>
    public static string Display(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return string.Empty;

        return NameKeys.TryGetValue(sourceId, out var key)
            ? LocalizationService.Instance[key]
            : Names.GetValueOrDefault(sourceId, sourceId);
    }

    /// <summary>Whether this is a real source id. Used by <see cref="ErrorDetailFormatter"/> to tell a
    /// tool's own "id: message" prefix from an unrelated word in free text that happens to precede a
    /// colon.</summary>
    public static bool IsKnownId(string id) => Names.ContainsKey(id);

    /// <summary>Every source whose behaviour differs enough from a plain launcher to need its own text.</summary>
    private static readonly Dictionary<string, string> DescriptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winget"] = "SourceDescWinget",
        ["windowsupdate"] = "SourceDescWindowsUpdate",
        ["msstore"] = "SourceDescMsStore",
        ["chocolatey"] = "SourceDescChocolatey",
        ["scoop"] = "SourceDescScoop",
        ["pip"] = "SourceDescPip",
        ["npm"] = "SourceDescNpm",
        ["pnpm"] = "SourceDescPnpm",
        ["dotnet-tool"] = "SourceDescDotnetTool",
        ["psgallery"] = "SourceDescPsGallery",
        ["gem"] = "SourceDescGem",
        ["composer"] = "SourceDescComposer",
        ["rustup"] = "SourceDescRustup",
        ["vcpkg"] = "SourceDescVcpkg",
        ["mise"] = "SourceDescMise",
        ["android-sdk"] = "SourceDescAndroidSdk",
        ["android-studio"] = "SourceDescAndroidStudio",
        ["gh-extension"] = "SourceDescGhExtension",
        ["pipx"] = "SourceDescPipx",
        ["vscode-extension"] = "SourceDescVsCodeExtension",
        ["wsl"] = "SourceDescWsl",
        ["deno"] = "SourceDescDeno",
        ["bun"] = "SourceDescBun",
        ["uv"] = "SourceDescUv",
        ["uv-tool"] = "SourceDescUvTool",
        ["gcloud"] = "SourceDescGcloud",
        ["ollama"] = "SourceDescOllama",
        ["flutter"] = "SourceDescFlutter",
        ["volta"] = "SourceDescVolta",
        ["pyenv-win"] = "SourceDescPyenvWin",
        ["steam"] = "SourceDescSteam",
        ["nvidia-driver"] = "SourceDescNvidia",
        ["github-release"] = "SourceDescGitHubRelease",
    };

    /// <summary>
    /// One sentence for the Settings hover tooltip: what the source is, how it works, where it comes
    /// from. Every game-launcher-detection source (there are more of those than everything else put
    /// together) shares one template rather than repeating near-identical text per launcher.
    /// </summary>
    public static string Description(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            return string.Empty;

        return DescriptionKeys.TryGetValue(sourceId, out var key)
            ? LocalizationService.Instance[key]
            : string.Format(LocalizationService.Instance["SourceDescLauncher"], Display(sourceId));
    }
}
