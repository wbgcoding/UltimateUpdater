using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services;

/// <summary>
/// Starts an installed Store (MSIX) app by its application user model id. <see cref="UriLauncher"/> refuses
/// the <c>shell:</c> scheme on purpose (it reaches folders, control panels and arbitrary shell verbs), so this is
/// the one place that opens exactly one shell form: <c>shell:AppsFolder\&lt;family&gt;!&lt;app id&gt;</c>. The
/// target is checked against a strict pattern before anything starts, explorer.exe is taken from the Windows
/// folder by absolute path (never from PATH), the string is the only argument, and the process is started
/// unelevated by this unelevated app.
/// </summary>
internal static class PackagedAppLauncher
{
    internal const string Prefix = @"shell:AppsFolder\";

    private static readonly Regex Target = new(
        @"^shell:AppsFolder\\[A-Za-z0-9.\-]+_[a-z0-9]{13}![A-Za-z0-9.\-]+\z",
        RegexOptions.CultureInvariant);

    /// <summary>True for a row target that must go through <see cref="LaunchDetached"/> rather than the URI launcher.</summary>
    public static bool IsPackagedAppTarget(string? target) =>
        target is not null && target.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for the exact AUMID form; anything else, including any other <c>shell:</c> target, is refused.</summary>
    public static bool IsValidTarget(string? target) => target is not null && Target.IsMatch(target);

    /// <summary>The AUMID target for a package family and application id, or null when either is missing or malformed.</summary>
    public static string? TargetFor(string? familyName, string? applicationId)
    {
        if (familyName is null || applicationId is null)
            return null;

        var target = $"{Prefix}{familyName}!{applicationId}";
        return IsValidTarget(target) ? target : null;
    }

    /// <summary>The process start for a valid target (explorer.exe by absolute path, the target as the single
    /// argument, shell execute), or null when the target is not valid.</summary>
    internal static ProcessStartInfo? BuildStartInfo(string target, string windowsFolder)
    {
        if (!IsValidTarget(target) || string.IsNullOrEmpty(windowsFolder))
            return null;

        // A shell execute, so no pipe handles are inherited (nothing here is read back). The target holds
        // neither a space nor a quote, so the argument string is exactly the one validated target.
        var info = new ProcessStartInfo(Path.Combine(windowsFolder, "explorer.exe"), target) { UseShellExecute = true };
        return info;
    }

    /// <summary>Fire-and-forget, like <see cref="UriLauncher.LaunchDetached"/>: nothing here waits for the app.</summary>
    public static void LaunchDetached(string target, LoggingService logging) =>
        _ = Task.Run(() => Launch(target, logging));

    public static bool Launch(string target, LoggingService logging)
    {
        var info = BuildStartInfo(target, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (info is null || !File.Exists(info.FileName))
        {
            logging.Warn($"Refusing to start '{target}': not a packaged-app target.");
            return false;
        }

        try
        {
            using var process = Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            logging.Warn($"Could not start '{target}': {ex.Message}");
            return false;
        }
    }
}
