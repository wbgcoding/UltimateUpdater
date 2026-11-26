namespace UltimateUpdater.Services.Sources.Shared;

/// <summary>
/// The one check for an id segment (owner, repository, extension, model name) that ends up in a URL or a
/// command argument: ASCII letters and digits plus '.', '-' and '_', nothing else. ASCII on purpose, since
/// char.IsLetterOrDigit also admits look-alike letters and full-width digits from other scripts.
/// </summary>
internal static class SafeToken
{
    /// <summary>
    /// True for a non-empty token of at most <paramref name="maxLength"/> characters made only of the allowed
    /// characters. With <paramref name="allowDotRuns"/> false, a ".." run (and a lone ".") is refused as well:
    /// Uri.EscapeDataString leaves dots alone, so such a segment would walk out of the path it is put into.
    /// </summary>
    public static bool IsPlain(string value, int maxLength = int.MaxValue, bool allowDotRuns = true) =>
        value.Length > 0
        && value.Length <= maxLength
        && (allowDotRuns || (!value.Contains("..") && value != "."))
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
