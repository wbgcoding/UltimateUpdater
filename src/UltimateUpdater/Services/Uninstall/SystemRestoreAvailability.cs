using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// A fast look, before the elevated helper is asked for a restore point, at the switches that make Windows refuse
/// one: System Restore turned off by policy or setting, or a service it needs disabled. Without it the helper start
/// and Windows' own refusal can take minutes (a UAC prompt, then a WMI call that waits on a service that never
/// answers) before the person is asked whether to remove anyway. Only an explicit "off" counts: a missing key proves
/// nothing (Windows 11 has no System Restore service key at all and makes restore points), and a machine that
/// looks fine can still refuse, which the helper's own result then reports as before.
/// </summary>
public static class SystemRestoreAvailability
{
    internal const string PolicyKey = @"HKLM\SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
    internal const string SettingKey = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    internal const string ServicesKey = @"HKLM\SYSTEM\CurrentControlSet\Services";

    /// <summary>Service start type 4: disabled.</summary>
    private const int Disabled = 4;

    /// <summary>The services a restore point is made through (System Restore itself, where it still has a service,
    /// the Volume Shadow Copy service and its software provider).</summary>
    private static readonly string[] Services = ["srservice", "VSS", "swprv"];

    /// <summary>Why no restore point can be made right now, or null when nothing says so.</summary>
    public static string? UnavailableReason(IRegistryView registry)
    {
        if (registry.DwordValue(PolicyKey, "DisableSR") == 1)
            return "System Restore is turned off by policy";

        if (registry.DwordValue(SettingKey, "DisableSR") == 1)
            return "System Restore is turned off";

        foreach (var service in Services)
        {
            if (registry.DwordValue($@"{ServicesKey}\{service}", "Start") == Disabled)
                return $"the {service} service is disabled";
        }

        return null;
    }
}
