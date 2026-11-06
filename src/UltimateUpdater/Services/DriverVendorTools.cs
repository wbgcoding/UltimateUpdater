namespace UltimateUpdater.Services;

/// <summary>
/// The vendor's own control program for a driver row: a driver has no application of its own to take an
/// icon from, but the vendor's tool is what the user knows the driver by. The first candidate that
/// exists wins, so a newer tool is listed before the one it replaced.
/// </summary>
internal sealed class DriverVendorTools
{
    internal sealed record Vendor(string Key, string[] TitleWords, string[] Candidates, string? UninstallName);

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, string> _expand;
    private readonly IReadOnlyList<Vendor> _vendors;

    public DriverVendorTools(Func<string, bool>? fileExists = null, Func<string, string>? expand = null, IReadOnlyList<Vendor>? vendors = null)
    {
        _fileExists = fileExists ?? System.IO.File.Exists;
        _expand = expand ?? Environment.ExpandEnvironmentVariables;
        _vendors = vendors ?? DefaultVendors;
    }

    internal static readonly IReadOnlyList<Vendor> DefaultVendors =
    [
        new("nvidia", ["nvidia"],
        [
            @"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe",
            @"%ProgramFiles%\NVIDIA Corporation\NVIDIA App\NVIDIA App.exe",
            @"%ProgramFiles%\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe",
            @"%ProgramFiles(x86)%\NVIDIA Corporation\NVIDIA GeForce Experience\NVIDIA GeForce Experience.exe",
        ], null),
        new("amd", ["amd", "advanced"],
        [
            @"%ProgramFiles%\AMD\CNext\CNext\RadeonSoftware.exe",
        ], null),
        new("intel", ["intel"], [], "Driver & Support Assistant"),
        new("realtek", ["realtek"],
        [
            @"%ProgramFiles%\Realtek\Audio\HDA\RtkNGUI64.exe",
            @"%ProgramFiles%\Realtek\Audio\HDA\RAVCpl64.exe",
        ], null),
        new("logitech", ["logitech", "logi"],
        [
            @"%ProgramFiles%\LGHUB\lghub.exe",
        ], null),
    ];

    /// <summary>The sources whose rows are drivers; every Windows Update row is matched by its title instead.</summary>
    internal static bool IsDriverSource(string sourceId) =>
        sourceId is "nvidia-driver" or "windowsupdate";

    /// <summary>The vendor a driver row belongs to: the NVIDIA source by id, a Windows Update row by the first word of its title.</summary>
    internal Vendor? VendorFor(string sourceId, string displayName)
    {
        if (sourceId == "nvidia-driver")
            return _vendors.FirstOrDefault(v => v.Key == "nvidia");

        if (sourceId != "windowsupdate" || string.IsNullOrWhiteSpace(displayName))
            return null;

        var first = displayName.Split([' ', '-', '(', ',', '®'], StringSplitOptions.RemoveEmptyEntries)[0];
        var word = TextNormalization.AlphanumericLower(first);
        return _vendors.FirstOrDefault(v => v.TitleWords.Contains(word));
    }

    /// <summary>The vendor tool's executable, or null when none of the candidates is installed.</summary>
    internal string? FindExecutable(Vendor vendor) =>
        vendor.Candidates.Select(_expand).FirstOrDefault(_fileExists);
}
