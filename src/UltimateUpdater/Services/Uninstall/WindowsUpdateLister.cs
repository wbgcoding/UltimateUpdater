using System.IO;
using System.Management;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Elevation;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The app side of listing installed Windows updates: asks the elevated worker for the DISM rows (DISM needs
/// administrator rights, so this can raise the elevation prompt) and joins them with the installed-hotfix list, which
/// any user can read. Everything that can go wrong ends as "no list", never as a guess.
/// </summary>
internal sealed class WindowsUpdateLister(ElevatedHost? elevated, LoggingService logging)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The worker's rows, or null when the helper is not available or DISM could not be read.</summary>
    public async Task<IReadOnlyList<WindowsUpdateRow>?> ReadRowsAsync(CancellationToken cancellationToken)
    {
        if (elevated is null)
            return null;

        var job = new UninstallProgramJob { Method = WindowsUpdateWorker.ListMethod };
        string? detail = null;
        var success = false;
        var reachedDone = false;

        bool OnLine(string line)
        {
            InstallProgressMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<InstallProgressMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // a malformed worker line is ignored, never fatal
            }

            switch (message?.Type)
            {
                case "done":
                    reachedDone = true;
                    return false;
                case "result":
                    success = message.Success;
                    detail = message.Detail;
                    break;
            }

            return true;
        }

        if (!await elevated.RunAsync("uninstall-program", job, OnLine, cancellationToken) || !reachedDone || !success)
        {
            logging.Warn("The installed Windows updates could not be listed.");
            return null;
        }

        return ParseRows(detail);
    }

    /// <summary>The rows of the worker's JSON; null when it is not a list. A row that is no update identity is dropped later
    /// by <see cref="WindowsUpdateDiscovery.ToEntries"/>.</summary>
    internal static IReadOnlyList<WindowsUpdateRow>? ParseRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<WindowsUpdateRow>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The installed-hotfix list (KB number, description, date). Empty when WMI does not answer: the rows still show, with their own dates.</summary>
    public IReadOnlyList<QuickFix> ReadQuickFixes(CancellationToken cancellationToken)
    {
        var fixes = new List<QuickFix>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT HotFixID, Description, InstalledOn FROM Win32_QuickFixEngineering"),
                new System.Management.EnumerationOptions { Timeout = WmiTimeout });

            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = item["HotFixID"] as string;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    fixes.Add(new QuickFix(
                        id, item["Description"] as string ?? string.Empty,
                        WindowsUpdateDiscovery.ParseQuickFixDate(item["InstalledOn"]?.ToString())));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException
                                       or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            logging.Warn($"The installed hotfix list could not be read: {ex.Message}");
        }

        return fixes;
    }
}
