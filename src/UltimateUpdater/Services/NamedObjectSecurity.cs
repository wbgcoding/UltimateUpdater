using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// Creates the named kernel objects the installer talks to with an explicit security descriptor. An object made
/// with the default one gets the creator's default DACL, and for an elevated process that grants nobody but
/// Administrators and SYSTEM: the unelevated Setup of the same person could not even open it.
/// </summary>
internal static class NamedObjectSecurity
{
    private const uint Synchronize = 0x00100000;
    private const uint EventModifyState = 0x0002;
    private const uint EventAllAccess = 0x001F0003;
    private const uint MutexAllAccess = 0x001F0001;
    private const int ErrorAlreadyExists = 183;
    private const uint SddlRevision1 = 1;

    /// <summary>
    /// The rights the person's own unelevated Setup gets on the exit event: signal it, nothing else. Without
    /// Synchronize no other process of the user (a low integrity one included) can wait on the auto-reset event
    /// and swallow the signal; the app keeps the handle it got when it created the event.
    /// </summary>
    internal const uint UserEventRights = EventModifyState;

    /// <summary>Mutex: the person may only open it to see that it exists.</summary>
    internal const uint UserMutexRights = Synchronize;

    /// <summary>
    /// DACL: the user gets exactly <paramref name="userRights"/>, SYSTEM and Administrators everything, nobody
    /// else anything. Protected, so nothing is ever inherited. The owner rights entry takes away the owner's
    /// implicit right to rewrite the DACL (an object an unelevated copy creates is owned by the user, who could
    /// otherwise grant itself the wait right and swallow the exit signal); reading the descriptor stays allowed. With <paramref name="noWriteUp"/> the object also
    /// carries a Medium mandatory label with no-write-up: a process below Medium integrity (a sandboxed one)
    /// cannot change it even where the DACL would allow it.
    /// </summary>
    internal static string BuildSddl(string userSid, uint userRights, uint fullRights, bool noWriteUp)
    {
        var sddl = $"D:P(A;;0x{userRights:x};;;{userSid})(A;;0x{fullRights:x};;;SY)(A;;0x{fullRights:x};;;BA)(A;;RC;;;OW)";
        return noWriteUp ? sddl + "S:(ML;;NW;;;ME)" : sddl;
    }

    internal static string ExitEventSddl(string userSid) =>
        BuildSddl(userSid, UserEventRights, EventAllAccess, noWriteUp: true);

    internal static string BusyMutexSddl(string userSid) =>
        BuildSddl(userSid, UserMutexRights, MutexAllAccess, noWriteUp: false);

    internal static string CurrentUserSid() =>
        System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current user has no SID.");

    /// <summary>
    /// Creates the event, or opens it if the name is taken. <paramref name="alreadyExisted"/> tells which, so a
    /// caller that wants to be the creator can walk away from a name somebody else made first. The returned
    /// handle can wait on the event only: nothing in the app needs more.
    /// </summary>
    internal static SafeWaitHandle CreateEvent(string name, string sddl, out bool alreadyExisted)
    {
        using var descriptor = SecurityDescriptor.FromSddl(sddl);
        var attributes = descriptor.Attributes();
        var handle = CreateEventExW(ref attributes, name, 0, Synchronize);
        return Checked(handle, out alreadyExisted);
    }

    /// <summary>The mutex counterpart of <see cref="CreateEvent"/>; it is never owned or waited on.</summary>
    internal static SafeWaitHandle CreateMutex(string name, string sddl, out bool alreadyExisted)
    {
        using var descriptor = SecurityDescriptor.FromSddl(sddl);
        var attributes = descriptor.Attributes();
        var handle = CreateMutexExW(ref attributes, name, 0, Synchronize);
        return Checked(handle, out alreadyExisted);
    }

    private static SafeWaitHandle Checked(SafeWaitHandle handle, out bool alreadyExisted)
    {
        var error = Marshal.GetLastWin32Error();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new Win32Exception(error);
        }

        alreadyExisted = error == ErrorAlreadyExists;
        return handle;
    }

    private sealed class SecurityDescriptor : IDisposable
    {
        private IntPtr _pointer;

        private SecurityDescriptor(IntPtr pointer) => _pointer = pointer;

        internal static SecurityDescriptor FromSddl(string sddl)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SddlRevision1, out var pointer, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return new SecurityDescriptor(pointer);
        }

        internal SecurityAttributes Attributes() => new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            SecurityDescriptor = _pointer,
            InheritHandle = 0,
        };

        public void Dispose()
        {
            if (_pointer == IntPtr.Zero)
                return;

            LocalFree(_pointer);
            _pointer = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateEventExW(ref SecurityAttributes attributes, string name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
}
