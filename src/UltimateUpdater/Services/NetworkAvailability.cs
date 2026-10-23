using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services;

/// <summary>
/// Whether this machine can reach the internet at all right now, checked once per scan so a source that
/// only needs the local machine is never skipped for no reason, and every network source fails the same
/// obvious way instead of each one separately timing out against a dead connection. Any failure here is
/// treated as ONLINE (fail open): a broken probe must never itself block a scan that could otherwise run
/// fine, and a false "online" costs one ordinary per-source timeout, not a skipped scan.
/// </summary>
public static class NetworkAvailability
{
    // Windows' own Network List Manager, the same signal the taskbar's network icon reads; a real
    // internet check, not just "a cable is plugged in" (which GetIsNetworkAvailable alone cannot tell
    // apart from a router with no uplink).
    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public static bool IsOnline()
    {
        object? manager = null;

        try
        {
            // Cheapest check first: no adapter at all (airplane mode, Wi-Fi off) needs no COM call.
            if (!NetworkInterface.GetIsNetworkAvailable())
                return false;

            var type = Type.GetTypeFromCLSID(NetworkListManagerClsid);
            if (type is null)
                return true;

            manager = Activator.CreateInstance(type);
            if (manager is null)
                return true;

            // Late-bound through IDispatch (Type.InvokeMember), deliberately NOT a hand-declared
            // [ComImport] vtable interface: INetworkListManager derives from IDispatch, so a vtable
            // interop interface would have to reproduce every preceding IDispatch/interface member in
            // the exact SDK order to land on the right slot, and getting that wrong is a native call
            // through a mismatched function pointer, not a catchable .NET exception. Invoking by NAME
            // sidesteps that risk entirely at the cost of one reflection call per scan.
            var result = manager.GetType().InvokeMember(
                "IsConnectedToInternet",
                BindingFlags.GetProperty,
                binder: null,
                target: manager,
                args: null);

            return result is bool isConnected ? isConnected : true;
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException
            or NotSupportedException or NetworkInformationException or TargetInvocationException
            or MissingMemberException or MemberAccessException or UnauthorizedAccessException)
        {
            // A probe that cannot answer must never itself block or fail a scan.
            return true;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
                Marshal.ReleaseComObject(manager);
        }
    }
}
