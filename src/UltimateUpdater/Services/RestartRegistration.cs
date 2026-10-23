using System.Runtime.InteropServices;

namespace UltimateUpdater.Services;

/// <summary>
/// Tells Windows how to start this app again after it was closed for an update or a reboot. Note
/// that the self-update does NOT depend on this: the Restart Manager offers to restart what it
/// closed and, measured on a test machine, does not actually do it, so the installer is told to
/// start the app again explicitly. This registration is what covers the reboot case.
/// </summary>
public static class RestartRegistration
{
    /// <summary>Restart for any reason (crash, hang, patch, reboot).</summary>
    private const uint RestartForAnyReason = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);

    /// <param name="commandLine">
    /// Arguments only; Windows prepends the executable itself. Null restarts it with none.
    /// </param>
    public static void Register(string? commandLine, LoggingService logging)
    {
        try
        {
            var result = RegisterApplicationRestart(commandLine, RestartForAnyReason);
            if (result != 0)
                logging.Warn($"Could not register for restart after an update (0x{result:X8}).");
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // older or trimmed-down Windows: updating still works, it just will not relaunch
            logging.Warn($"Restart registration is unavailable: {ex.Message}");
        }
    }
}
