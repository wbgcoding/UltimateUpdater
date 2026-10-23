namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Classifies a process exit code as a genuine crash rather than an ordinary nonzero failure the tool
/// reported on purpose. Windows assigns the NTSTATUS "error" severity to every code whose top two bits
/// are set (0xC0000000 and up), that band is what an abnormal termination (an access violation, a stack
/// buffer overrun, a process the OS itself killed) actually returns, as opposed to a tool's own "I looked
/// and something is wrong" exit code, which is almost always a small positive number. .NET's own
/// unhandled-exception exit code, 0xE0434352, lies inside that band and so counts as a crash too. One band inside the NTSTATUS range is carved back OUT again: node/libuv tools (npm among
/// them) report their own ordinary errors as small NEGATIVE numbers, -4095 through -1, which land as
/// unsigned 0xFFFFF001 through 0xFFFFFFFF and would otherwise read as an OS-level crash even though the
/// tool ran fine and printed a perfectly normal error message (measured on the VM: npm exits -4058,
/// 0xFFFFF026, whenever `npm outdated` itself hit an error worth showing, not a crash of npm.exe).
/// Android's sdkmanager.bat wraps a JVM, and a JVM that aborts on launch (because the Java on PATH is
/// older than the 17+ its command-line tools require) exits with a genuine crash code from outside that
/// carved-out band, not a normal sdkmanager error.
/// </summary>
internal static class ProcessExitCodes
{
    /// <summary>The lowest NTSTATUS value with the "Error" severity bits (31-30) set.</summary>
    private const uint NtStatusErrorFloor = 0xC0000000u;

    /// <summary>The libuv/node error band, signed -4095 (inclusive) through -1 (inclusive): an ordinary
    /// tool-reported failure, never an OS-level crash, even though it falls inside the NTSTATUS "error"
    /// band above.</summary>
    private const uint NodeErrorBandFloor = unchecked((uint)-4095);

    private const uint NodeErrorBandCeiling = unchecked((uint)-1);

    public static bool IsCrash(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        if (unsigned is >= NodeErrorBandFloor and <= NodeErrorBandCeiling)
            return false;

        return unsigned >= NtStatusErrorFloor;
    }
}
