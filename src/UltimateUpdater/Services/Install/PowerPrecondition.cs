namespace UltimateUpdater.Services.Install;

/// <summary>
/// Pre-install check: should the pre-flight window warn about the battery? Reads the same
/// <c>SystemInformation.PowerStatus</c> source <see cref="ScanScheduler"/>'s own AC check already uses.
/// Warns only when the line status is genuinely Offline AND the percent is a real reading below 20:
/// unknown on either side (line status Unknown, or a percent above 1.0, the sentinel for "not
/// reported") means no warning, never a guess.
/// </summary>
public static class PowerPrecondition
{
    public static bool ShouldWarn(System.Windows.Forms.PowerLineStatus line, float batteryLifePercent)
    {
        if (line != System.Windows.Forms.PowerLineStatus.Offline)
            return false;

        // 1.0 means full charge; anything above 1 is the "not reported" sentinel, not a real reading.
        if (batteryLifePercent > 1f)
            return false;

        return batteryLifePercent < 0.2f;
    }
}
