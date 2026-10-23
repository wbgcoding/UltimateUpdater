using System.Management;
using System.Runtime.Versioning;

namespace UltimateUpdater.Services;

/// <summary>
/// Optional system restore point before the first elevated install batch.
/// Requires elevation, so this only ever runs inside the elevated worker. Best-effort: a failure
/// is reported but must not block the installs the user asked for.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RestorePointService
{
    private const int ApplicationInstall = 0;
    private const int BeginSystemChange = 100;

    public static bool TryCreate(string description, LoggingService logging)
    {
        try
        {
            using var systemRestore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description;
            parameters["RestorePointType"] = ApplicationInstall;
            parameters["EventType"] = BeginSystemChange;

            var result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
            var returnValue = Convert.ToUInt32(result?["ReturnValue"] ?? 1u);

            if (returnValue == 0)
            {
                logging.Info("Restore point created.");
                return true;
            }

            // 1058 = System Restore disabled by policy; anything else is an unexpected WMI failure
            logging.Warn($"Restore point not created (WMI return {returnValue}).");
            return false;
        }
        catch (ManagementException ex)
        {
            logging.Warn($"Restore point unavailable: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logging.Warn($"Restore point denied: {ex.Message}");
            return false;
        }
    }
}
