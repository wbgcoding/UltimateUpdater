using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// How release data is fetched. Two implementations exist only because the repository is private
/// during development; both enforce the same HTTPS allowlist and size cap.
/// </summary>
public interface IUpdateChannel
{
    bool IsAvailable { get; }

    /// <summary>
    /// Whether a GitHub PRE-RELEASE counts as an update on this channel. False on the channel that ships:
    /// a stable install must never be moved onto a beta because it happened to be the newest tag.
    /// </summary>
    bool AllowPrereleases => false;

    Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken);

    /// <summary>Reports bytes read so far and the advertised total, at most a few times a second and once more at the end.</summary>
    Task<bool> DownloadAsync(
        HttpClient client, string url, string targetPath, long maxBytes, CancellationToken cancellationToken,
        IProgress<(long Read, long? Total)>? progress = null);
}
