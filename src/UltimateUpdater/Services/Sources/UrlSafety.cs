namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The http(s)-only gate every network-sourced URL in this project passes through before it can reach a
/// changelog/more-info link: a remote response is not something this app controls (WSUS server, package
/// registry, news feed, driver metadata), so any other scheme becomes null rather than being trusted.
/// </summary>
public static class UrlSafety
{
    public static string? SafeHttpUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }
}
