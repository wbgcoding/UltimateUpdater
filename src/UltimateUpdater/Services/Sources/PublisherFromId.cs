namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Winget ids follow the "Publisher.Product" shape (the same dot
/// <see cref="WingetUpgradeParser"/> already relies on); other sources (npm, chocolatey, Windows Update,
/// a launcher's own id) carry no such prefix. Pure and synchronous, no network call and never touched by
/// a scan, so it costs nothing to run on every keystroke of a search or on every row's tooltip.
/// </summary>
public static class PublisherFromId
{
    /// <summary>The part before the first dot when <paramref name="id"/> has the winget shape (a
    /// non-empty prefix, a non-empty remainder, no whitespace in the prefix), otherwise null.</summary>
    public static string? Derive(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var dot = id.IndexOf('.');
        if (dot <= 0 || dot == id.Length - 1)
            return null;

        var publisher = id[..dot];
        return publisher.Any(char.IsWhiteSpace) ? null : publisher;
    }
}
