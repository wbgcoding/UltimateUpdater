using System.Diagnostics;
using System.Globalization;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// The hand-over between a portable copy that has just replaced itself and the copy it started. The old
/// process passes its own id; the new one waits for that process to be gone BEFORE it asks for the
/// single-instance lock (otherwise the lock is still held, the new copy hands over to the dying one and
/// exits, and nothing is left running) and before it deletes the old image (which the old process still
/// maps until it exits).
/// </summary>
public static class AfterUpdateHandoff
{
    public const string Argument = "--after-update";

    /// <summary>Long enough for a normal shutdown, short enough that a reused process id or a hung old copy
    /// cannot keep the new one from starting.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The process id following <see cref="Argument"/>, or null when the argument is absent or its value is
    /// anything but a positive integer: a bad value is ignored, never trusted into a wait.
    /// </summary>
    public static int? ParseProcessId(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (!string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : null;
        }

        return null;
    }

    /// <summary>
    /// With the argument present: waits for that process, then runs the cleanup of the previous image, and
    /// only then asks for the single-instance lock. Without it: just asks. The order is the whole point, so
    /// it lives in one place the tests can drive with fakes.
    /// </summary>
    public static bool AcquireAfterWaiting(
        IReadOnlyList<string> args, Action<int> waitForExit, Action cleanupPreviousImage, Func<bool> tryAcquire)
    {
        if (ParseProcessId(args) is { } processId)
        {
            waitForExit(processId);
            cleanupPreviousImage();
        }

        return tryAcquire();
    }

    /// <summary>Waits for a process to end, at most <see cref="MaxWait"/>. A process that no longer exists
    /// (or cannot be opened) counts as gone.</summary>
    public static void WaitForProcessExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(MaxWait);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // already gone, or not ours to wait for: the lock attempt that follows decides what happens
        }
    }
}
