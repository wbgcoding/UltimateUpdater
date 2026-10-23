using System.IO;
using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>Shared streaming download with a hard byte cap, so a hostile server cannot fill the disk.</summary>
internal static class UpdateDownload
{
    public static async Task<bool> ExecuteAsync(
        HttpClient client,
        HttpRequestMessage request,
        string targetPath,
        long maxBytes,
        LoggingService logging,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logging.Warn($"Self-update: download returned {(int)response.StatusCode}.");
                return false;
            }

            if (!SelfUpdateUrlGuard.FinalUrlAllowed(response, logging))
                return false;

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
            {
                logging.Warn("Self-update: advertised size exceeds the limit; refusing.");
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(targetPath);

            var buffer = new byte[81920];
            long total = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    logging.Warn("Self-update: download exceeded the size limit mid-stream; aborting.");
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        return true;
    }
}
