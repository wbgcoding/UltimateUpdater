using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace UltimateUpdater.Services.SelfUpdate;

/// <summary>Shared streaming download with a hard byte cap, so a hostile server cannot fill the disk.</summary>
internal static class UpdateDownload
{
    /// <summary>A few readings a second are plenty for a progress bar; one per buffer would flood the dispatcher.</summary>
    internal static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<bool> ExecuteAsync(
        HttpClient client,
        HttpRequestMessage request,
        string targetPath,
        long maxBytes,
        LoggingService logging,
        CancellationToken cancellationToken,
        IProgress<(long Read, long? Total)>? progress = null)
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

            var advertised = response.Content.Headers.ContentLength;
            if (advertised is { } length && length > maxBytes)
            {
                logging.Warn("Self-update: advertised size exceeds the limit; refusing.");
                return false;
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(targetPath);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    logging.Warn("Self-update: download exceeded the size limit mid-stream; aborting.");
                    return false;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                if (progress is not null && clock.Elapsed - lastReport >= ReportInterval)
                {
                    lastReport = clock.Elapsed;
                    progress.Report((total, advertised));
                }
            }

            // The closing reading is always delivered, so a bar never stops short of the end.
            progress?.Report((total, advertised ?? total));
        }

        return true;
    }
}
