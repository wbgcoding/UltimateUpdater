using System.IO;
using System.Net.Http;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Shared by every scan-time HTTP reader that is not itself pinned to a security-guard check
/// (self-update's <c>UpdateMetadata.ReadLimitedAsync</c> re-verifies the final URL against an allowlist
/// first and stays separate for that reason: merging it here would blur a security-relevant check into
/// a generic helper). The cap is enforced on the STREAM, not <c>Content-Length</c>: a chunked response
/// advertises no length at all, so a header-only check let an unbounded body get buffered whole before
/// any size decision was made.
/// </summary>
internal static class HttpResponseReading
{
    /// <summary>Reads at most <paramref name="maxBytes"/> as UTF-8 text; returns null the moment the
    /// stream exceeds the cap. <paramref name="onExceeded"/> lets a caller log with its own context
    /// (a source id, an item id) without this helper needing to know about either.</summary>
    internal static async Task<string?> ReadLimitedAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken, Action? onExceeded = null)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffered = new MemoryStream();
        var chunk = new byte[64 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;

            if (buffered.Length + read > maxBytes)
            {
                onExceeded?.Invoke();
                return null;
            }

            buffered.Write(chunk, 0, read);
        }

        // Unlike ReadAsStringAsync, GetString keeps a UTF-8 byte-order mark as U+FEFF, which JsonDocument
        // and XDocument then reject.
        var bytes = buffered.GetBuffer().AsSpan(0, (int)buffered.Length);
        if (bytes.StartsWith(System.Text.Encoding.UTF8.Preamble))
            bytes = bytes[3..];
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
