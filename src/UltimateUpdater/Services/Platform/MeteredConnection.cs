using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Platform;

/// <summary>
/// Whether the active internet connection is metered (Fixed or Variable cost; Unrestricted does not
/// count). An automatic scan should not run on someone's mobile hotspot, and an install
/// batch should say so before downloading anything sizeable. Read through the Network List Manager's
/// classic COM interface rather than the WinRT connectivity API: that one needs an OS-versioned target
/// framework, whose WinRT projection made the published exe several times larger for this one call.
/// Same fail-open contract as every other environment probe here: no connection, or any exception
/// reading one, reads as "not metered" rather than blocking a scan or an install on a guess.
/// </summary>
public static class MeteredConnection
{
    // NLM_CONNECTION_COST flags (netlistmgr.h).
    private const uint CostFixed = 0x2;
    private const uint CostVariable = 0x4;

    public static bool IsMetered()
    {
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, throwOnError: true)!);
            if (manager is not INetworkCostManager costs)
                return false;

            // A null destination asks for the cost of the machine's preferred connection, which is what
            // the WinRT "internet connection profile" reported.
            costs.GetCost(out var cost, IntPtr.Zero);
            return IsMeteredCost(cost);
        }
        catch (Exception)
        {
            // The network stack can throw in ways this app has no useful name for (no network stack
            // initialized, running under a locked-down account, and similar); every one of them reads as
            // "not metered", never as a reason to warn on a guess.
            return false;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
                Marshal.ReleaseComObject(manager);
        }
    }

    /// <summary>Fixed or Variable cost counts as metered; Unrestricted, Unknown and the extra state bits
    /// (roaming, over the data limit) alone do not.</summary>
    internal static bool IsMeteredCost(uint cost) => (cost & (CostFixed | CostVariable)) != 0;

    private static readonly Guid NetworkListManagerClsid = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    [ComImport]
    [Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INetworkCostManager
    {
        void GetCost(out uint cost, IntPtr destination);
    }
}
