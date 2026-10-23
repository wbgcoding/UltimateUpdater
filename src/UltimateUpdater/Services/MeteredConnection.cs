namespace UltimateUpdater.Services;

/// <summary>
/// Whether the active internet connection is metered (Fixed or Variable cost; Unrestricted does not
/// count). An automatic scan should not run on someone's mobile hotspot, and an install
/// batch should say so before downloading anything sizeable. Reachable without an extra package because
/// the project targets <c>net10.0-windows10.0.19041.0</c>, which projects the WinRT connectivity API
/// directly. Same fail-open contract as every other environment probe here: no profile, or any exception
/// reading one, reads as "not metered" rather than blocking a scan or an install on a guess.
/// </summary>
public static class MeteredConnection
{
    public static bool IsMetered()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
                return false;

            var cost = profile.GetConnectionCost();
            return cost.NetworkCostType is Windows.Networking.Connectivity.NetworkCostType.Fixed
                or Windows.Networking.Connectivity.NetworkCostType.Variable;
        }
        catch (Exception)
        {
            // The WinRT connectivity stack can throw in ways this app has no useful name for (no
            // network stack initialized, running under a locked-down account, and similar); every one
            // of them reads as "not metered", never as a reason to warn on a guess.
            return false;
        }
    }
}
