using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Pure text→data parsers for pyenv-win. No live pyenv-win install was available on this dev box, so,
/// same "never guess" standard as every other source without a live install to test against, these are verified against
/// pyenv-win's OWN source (`pyenv.vbs`'s <c>CommandGlobal</c>/<c>CommandVersions</c>, read directly from
/// its GitHub repo) rather than assumed from general knowledge, plus a literal output example
/// from its README for the installable-versions list.
/// </summary>
public static partial class PyenvWinParsers
{
    // Real README example for `pyenv install -l`:
    // 3.8.0-win32
    // 3.8.0
    // 3.8.1rc1-win32
    // 3.8.1rc1
    // 3.9.0-win32
    // 3.9.0
    // Only a BARE "X.Y.Z" line is a confidently-installable stable release, a "-win32" suffix or a
    // pre-release tag (rc1, a1, b2,...) is never treated as "the" version for that release line.
    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex StableVersion();

    public static IReadOnlyList<string> ParseInstallableVersions(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return [];

        return stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => StableVersion().IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// `pyenv global` with no arguments, pyenv.vbs's own <c>CommandGlobal</c> prints one bare version
    /// string per line, or the literal line "no global version configured" when nothing is set. Multiple
    /// lines (more than one global tool pin) are deliberately NOT handled here, only a single, unambiguous
    /// global version is something this app is confident enough to compare against.
    /// </summary>
    public static string? ParseGlobalVersion(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return null;

        var lines = stdout.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count != 1 || lines[0].Equals("no global version configured", StringComparison.OrdinalIgnoreCase))
            return null;

        return StableVersion().IsMatch(lines[0]) ? lines[0] : null;
    }

    /// <summary>
    /// The highest available PATCH release in the SAME major.minor line as <paramref name="installed"/>,
    /// deliberately never a minor/major jump, which is what makes this a "safe", one-click update rather
    /// than a judgment call about which Python line to move to.
    /// </summary>
    public static string? FindSameLinePatchUpdate(string installed, IReadOnlyList<string> installable)
    {
        var parts = installed.Split('.');
        if (parts.Length != 3)
            return null;

        var prefix = $"{parts[0]}.{parts[1]}.";
        if (!int.TryParse(parts[2], out var installedPatch))
            return null;

        var best = installable
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => int.TryParse(v[prefix.Length..], out var patch) ? (Version: v, Patch: patch) : ((string Version, int Patch)?)null)
            .Where(v => v is not null)
            .Select(v => v!.Value)
            .OrderByDescending(v => v.Patch)
            .FirstOrDefault();

        return best.Version is { Length: > 0 } && best.Patch > installedPatch ? best.Version : null;
    }
}
