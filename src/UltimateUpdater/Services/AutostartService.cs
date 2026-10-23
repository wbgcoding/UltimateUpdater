using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UltimateUpdater.Services;

/// <summary>
/// Per-user autostart via HKCU\...\Run. Never written without explicit consent:
/// only the settings toggle or the installer checkbox reaches this.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutostartService
{
    private const string DefaultRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UltimateUpdater";
    private const string TrayArgument = "--tray";

    private readonly LoggingService _logging;
    private readonly string _runKeyPath;

    public AutostartService(LoggingService logging) : this(logging, DefaultRunKeyPath)
    {
    }

    /// <summary>A different key under HKCU, so a test never touches the real autostart entry.</summary>
    internal AutostartService(LoggingService logging, string runKeyPath)
    {
        _logging = logging;
        _runKeyPath = runKeyPath;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Returns false if the registry refused the change, so the UI can stay truthful.</summary>
    public bool TrySet(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            if (key is null)
                return false;

            if (enabled)
            {
                var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "UltimateUpdater.exe");
                key.SetValue(ValueName, $"\"{exePath}\" {TrayArgument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logging.Warn($"Could not change autostart: {ex.Message}");
            return false;
        }
    }
}
