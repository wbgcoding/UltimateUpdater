using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// The pre-request IsAllowedUrl check only covers where a call STARTS; the shared HttpClient follows
/// redirects by default, so the response can still come from anywhere. RequestMessage.RequestUri is
/// the address after every redirect actually taken, and this is the one check that covers that
/// (mirrors DriverSources.IsAllowedDownload's same fix for the NVIDIA path).
/// </summary>
internal static class SelfUpdateUrlGuard
{
    public static bool FinalUrlAllowed(HttpResponseMessage response, LoggingService logging)
    {
        var finalUrl = response.RequestMessage?.RequestUri?.ToString();
        if (finalUrl is not null && SelfUpdateService.IsAllowedUrl(finalUrl))
            return true;

        logging.Warn("Self-update: a redirect carried the request off the allowlisted hosts; refusing.");
        return false;
    }
}
