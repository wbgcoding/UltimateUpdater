using System.IO;
using System.Management;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The installed-hotfix list (KB number, description, date) that names the update rows read from the registry
/// (<see cref="WindowsUpdateRegistryReader"/>). Any user can read it; everything that can go wrong ends as an empty
/// list, never as a guess.
/// </summary>
internal sealed class WindowsUpdateLister(LoggingService logging)
{
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);

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
