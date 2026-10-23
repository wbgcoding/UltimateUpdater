using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The driver store names each package's own folder <c>&lt;originalfilename&gt;.inf_&lt;arch&gt;_&lt;hash&gt;</c>
/// (e.g. <c>nvhda.inf_amd64_1a2b3c4d5e6f7890</c>), which is where "original name" comes from: pnputil's own
/// enumeration text is localised and therefore off-limits, but this folder name is neither localised nor
/// OS-version-dependent. Pure string parsing, no I/O.
/// </summary>
public static class DriverStoreFolderNameParser
{
    private static readonly Regex FolderName = new(
        @"^(?<original>.+\.inf)_(?:amd64|x86|arm64|arm)_[0-9a-f]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The original file name (e.g. "nvhda.inf") when the folder follows the known pattern; the
    /// whole folder name, unchanged, when it does not, an unrecognised shape is still shown rather than
    /// silently dropped.</summary>
    public static string ExtractOriginalName(string driverStoreFolderName)
    {
        var match = FolderName.Match(driverStoreFolderName);
        return match.Success ? match.Groups["original"].Value : driverStoreFolderName;
    }
}
