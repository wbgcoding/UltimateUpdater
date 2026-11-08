using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace UltimateUpdater.Services;

/// <summary>
/// Lets Setup and the uninstaller ask this copy to end even when it runs elevated. Windows keeps an unelevated
/// process from closing or killing an elevated one, so the installer cannot do it itself; instead the app waits
/// on a named auto-reset event the installer may signal. The event name carries the user's SID and a hash of the
/// folder the exe runs from, so Setup for one folder never reaches a copy in another folder or of another user.
///
/// A signal carries no data and the callback only ever ends the app, never while work is running (see App).
/// The name is only used if this process created the event: when it already exists, somebody else made it first
/// (possibly with a DACL that lets anyone signal), so the app does not listen on it at all.
/// </summary>
internal sealed class ExitRequestListener : IDisposable
{
    /// <summary>Everything before the SID. The installer script builds the same name.</summary>
    internal const string NamePrefix = @"Local\UltimateUpdater-Exit-";

    private readonly Action _onSignal;
    private readonly LoggingService? _logging;
    private EventWaitHandle? _event;
    private RegisteredWaitHandle? _registration;

    private ExitRequestListener(string name, Action onSignal, LoggingService? logging)
    {
        Name = name;
        _onSignal = onSignal;
        _logging = logging;
    }

    internal string Name { get; }

    /// <summary>False when the name was taken by somebody else or the event could not be made.</summary>
    internal bool IsListening => _registration is not null;

    /// <summary>The first 16 hex digits of SHA256 over the lower-case folder path, without a trailing separator.</summary>
    internal static string FolderHash(string folder)
    {
        var normalized = folder.TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    internal static string BuildName(string userSid, string exeFolder) =>
        NamePrefix + userSid + "-" + FolderHash(exeFolder);

    /// <summary>
    /// Starts listening. <paramref name="onSignal"/> runs on a pool thread, once per signal; it must not throw
    /// and must do its own thread hopping. Never throws: the exit request is a courtesy to Setup, so a failure
    /// is logged and the app simply goes on without it.
    /// </summary>
    internal static ExitRequestListener Start(
        Action onSignal, LoggingService? logging = null, string? userSid = null, string? exeFolder = null)
    {
        string name;
        string sid;
        try
        {
            sid = userSid ?? NamedObjectSecurity.CurrentUserSid();
            var folder = exeFolder ?? System.IO.Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(folder))
                throw new InvalidOperationException("The folder of the running exe is unknown.");

            name = BuildName(sid, folder);
        }
        catch (Exception ex)
        {
            logging?.Warn($"The exit request for the installer is unavailable: {ex.Message}");
            return new ExitRequestListener(string.Empty, onSignal, logging);
        }

        var listener = new ExitRequestListener(name, onSignal, logging);
        listener.Listen(sid);
        return listener;
    }

    private void Listen(string userSid)
    {
        try
        {
            // The DACL always names the process's own user, whatever SID the name was built from: the
            // person who may signal is the one who runs this copy.
            var handle = NamedObjectSecurity.CreateEvent(
                Name, NamedObjectSecurity.ExitEventSddl(NamedObjectSecurity.CurrentUserSid()), out var alreadyExisted);

            if (alreadyExisted)
            {
                // Not ours: whoever made it chose its access rights, and it may be one anybody can set. Never
                // act on it.
                handle.Dispose();
                _logging?.Warn("The exit request name for the installer is already taken by another process; not listening.");
                return;
            }

            _event = new EventWaitHandle(false, EventResetMode.AutoReset);
            _event.SafeWaitHandle.Dispose();
            _event.SafeWaitHandle = handle;
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _event, (_, _) => Signalled(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            _logging?.Warn($"The exit request for the installer is unavailable: {ex.Message}");
            Release();
        }
    }

    private void Signalled()
    {
        try
        {
            _onSignal();
        }
        catch (Exception ex)
        {
            // An exception on a pool thread would end the whole process.
            _logging?.Warn($"The installer's exit request failed: {ex.Message}");
        }
    }

    private void Release()
    {
        _registration?.Unregister(null);
        _registration = null;
        _event?.Dispose();
        _event = null;
    }

    public void Dispose() => Release();
}
