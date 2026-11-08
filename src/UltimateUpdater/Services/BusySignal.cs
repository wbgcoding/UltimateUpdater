using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// Tells the installer that this copy is in the middle of installing updates or cleaning up, so Setup can ask
/// before it ends the process. The signal is a named mutex that exists exactly while the app is busy: nobody
/// ever waits on it or owns it, the installer only looks whether it can be opened. That keeps it free of
/// thread ownership, which a mutex held across a work run (started on one thread, finished on another) would
/// otherwise need. The name carries the user's SID because Setup may run under a different administrator
/// account than the one using the app. The mutex gets an explicit DACL because an elevated app would otherwise
/// create it readable by Administrators only, and the unelevated Setup of the same person could not see it.
/// </summary>
internal sealed class BusySignal : IDisposable
{
    /// <summary>Everything before the SID. The installer script builds the same name.</summary>
    internal const string NamePrefix = @"Local\UltimateUpdater-Busy-";

    private readonly object _gate = new();
    private readonly LoggingService? _logging;
    private SafeWaitHandle? _mutex;
    private bool _unavailableLogged;

    internal BusySignal(string? sid = null, LoggingService? logging = null)
    {
        _logging = logging;
        Name = NamePrefix + (sid ?? WindowsIdentity.GetCurrent().User?.Value ?? "unknown");
    }

    internal string Name { get; }

    /// <summary>Raises or drops the signal; calling it with the current state does nothing.</summary>
    internal void Set(bool busy)
    {
        lock (_gate)
        {
            if (busy == (_mutex is not null))
                return;

            if (busy)
            {
                try
                {
                    _mutex = NamedObjectSecurity.CreateMutex(
                        Name, NamedObjectSecurity.BusyMutexSddl(NamedObjectSecurity.CurrentUserSid()), out _);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Something else already holds the name (a different kind of object, or a mutex this
                    // user may not open). The signal is only a courtesy to Setup, so the app goes on
                    // without it instead of crashing on the timer tick; the retry on the next tick is cheap.
                    if (!_unavailableLogged)
                    {
                        _unavailableLogged = true;
                        _logging?.Warn($"The busy signal for the installer is unavailable: {ex.Message}");
                    }
                }
            }
            else
            {
                _mutex!.Dispose();
                _mutex = null;
            }
        }
    }

    public void Dispose() => Set(false);
}
