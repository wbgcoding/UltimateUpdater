using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>
/// Reading the metadata (the releases JSON and SHA256SUMS.txt) with a ceiling. The artifact download
/// has always been capped both ways; these two were read straight into a string, so a response far
/// larger than any real one would simply be buffered whole, SECURITY.md asks for oversized input to
/// be rejected at every boundary, and this is one.
/// </summary>
internal static class UpdateMetadata
{
    private const int MaxBytes = 4 * 1024 * 1024;

    public static async Task<string?> ReadLimitedAsync(
        HttpResponseMessage response, LoggingService logging, CancellationToken cancellationToken)
    {
        if (!SelfUpdateUrlGuard.FinalUrlAllowed(response, logging))
            return null;

        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            logging.Warn("Self-update: metadata response is larger than the limit; refusing.");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        // One byte more than the limit is enough to tell "at the limit" from "over it", whatever the
        // advertised length said.
        var buffer = new byte[MaxBytes + 1];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
                break;

            total += read;
        }

        if (total > MaxBytes)
        {
            logging.Warn("Self-update: metadata response exceeded the limit while reading; refusing.");
            return null;
        }

        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
