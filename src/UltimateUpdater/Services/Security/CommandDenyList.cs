using System.IO;

namespace UltimateUpdater.Services.Security;

/// <summary>
/// Hard defense-in-depth for <see cref="ProcessRunner"/>: this app must never issue a command that
/// deletes Windows system files, full stop. Every current caller already only
/// issues package-manager-style commands, so this should never actually trigger; it exists for the
/// hypothetical future caller or a bug that hands it something it should not. Checked regardless of
/// caller or elevation.
/// Deliberately NOT a general malware/command scanner: only a specific named list (format/
/// partition a drive, touch boot config, disable Defender, delete inside the Windows system directory)
/// plus a shadow-copy wipe (ransomware's own opening move). Pattern-matching arbitrary command lines is
/// inherently incomplete; this is a backstop on top of the argument-array/no-shell design, not a
/// replacement for it.
/// </summary>
public static class CommandDenyList
{
    /// <summary>Executables this app has no legitimate reason to ever launch, denied regardless of arguments.</summary>
    private static readonly HashSet<string> AlwaysDeniedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "diskpart", "bcdedit", "bootrec", "bootcfg", "vssadmin",
    };

    private static readonly string[] PowerShellHosts = ["powershell", "pwsh"];

    /// <summary>Extensions Windows resolves for a bare command name, stripped before matching.</summary>
    private static readonly string[] LaunchableExtensions = [".exe", ".com", ".bat", ".cmd"];

    private static readonly string[] DefenderDisableMarkers =
    [
        "disablerealtimemonitoring", "disableioavprotection", "disableantispyware",
        "disableantivirus", "disablebehaviormonitoring", "disablescriptscanning",
        "windows defender", "windefend",
    ];

    /// <summary>
    /// True when this command must be refused. <paramref name="reason"/> is English, safe to log or show
    /// raw (SECURITY.md: no user paths or secrets in it beyond the executable name itself).
    /// </summary>
    public static bool IsDenied(string fileName, IReadOnlyList<string> arguments, out string reason)
    {
        var baseName = SafeFileName(fileName);
        var stem = NormalizedStem(fileName);

        if (AlwaysDeniedStems.Contains(stem))
        {
            reason = $"'{baseName}' has no legitimate use in this app and is refused outright.";
            return true;
        }

        var joinedArguments = string.Join(' ', arguments).ToLowerInvariant();

        var isPowerShell = IsPowerShellHost(stem);

        if (isPowerShell || string.Equals(stem, "sc", StringComparison.OrdinalIgnoreCase))
        {
            if (DefenderDisableMarkers.Any(joinedArguments.Contains)
                && (joinedArguments.Contains("disable") || joinedArguments.Contains("stop") || joinedArguments.Contains("config")))
            {
                reason = "the command line would disable or stop Windows Defender.";
                return true;
            }
        }

        if (isPowerShell && LooksLikeSystemDirectoryDeletion(joinedArguments))
        {
            reason = "the command line deletes inside the Windows system directory.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static bool IsPowerShellHost(string stem) =>
        PowerShellHosts.Contains(stem, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name Windows would end up launching, minus its launchable extension: the deny check runs on
    /// the caller's raw name before any PATH/PATHEXT resolution, so "diskpart", "diskpart.exe.",
    /// "diskpart.exe::$DATA" must all collapse to "diskpart".
    /// </summary>
    private static string NormalizedStem(string fileName)
    {
        var name = SafeFileName(fileName);
        var colon = name.IndexOf(':');
        if (colon >= 0)
            name = name[..colon];
        name = name.TrimEnd('.', ' ');

        var extension = Path.GetExtension(name);
        if (extension.Length == 0)
            return name;

        return LaunchableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name)
            : name;
    }

    /// <summary>
    /// "remove-item"/"del"/"rd" (or their common aliases) together with a path that resolves under
    /// %WINDIR%. Both halves have to be present. "remove-item" alone matches any ordinary cleanup
    /// command this app legitimately never issues anyway, so this only fires on the combination.
    /// </summary>
    private static bool LooksLikeSystemDirectoryDeletion(string joinedArgumentsLower)
    {
        var hasDeleteVerb = joinedArgumentsLower.Contains("remove-item")
            || joinedArgumentsLower.Contains(" del ") || joinedArgumentsLower.Contains(" rd ")
            || joinedArgumentsLower.Contains("remove-container") || joinedArgumentsLower.Contains("rmdir");

        if (!hasDeleteVerb)
            return false;

        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
        return joinedArgumentsLower.Contains(windir) || joinedArgumentsLower.Contains(@"c:\windows");
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}

/// <summary>Thrown when <see cref="CommandDenyList"/> refuses a command. Never caught to "route around" it.</summary>
public sealed class DeniedCommandException(string reason) : Exception($"Refusing to run this command: {reason}");
