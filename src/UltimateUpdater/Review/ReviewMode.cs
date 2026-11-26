#if DEBUG
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using UltimateUpdater.Services.Scan;

namespace UltimateUpdater.Review;

/// <summary>
/// Entry point of the review harness (Debug builds only): opens every window state on mock data, shoots it
/// and audits the layout. Nothing here reads the network, the real settings or the real system: data lives in
/// a scratch folder, sources are fakes, dialogs never block.
/// </summary>
internal static class ReviewMode
{
    public static bool IsRequested(string[] args) =>
        args.Any(a => a.Equals("--review", StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(Application app, string[] args)
    {
        var options = ReviewOptions.Parse(args, out var error);
        if (options is null)
        {
            Console.Error.WriteLine(error);
            app.Shutdown(2);
            return;
        }

        Directory.CreateDirectory(options.OutDirectory);
        var problems = new List<string>();
        var exitCode = 0;
        string? scratch = null;
        try
        {
            if (options.ListOnly)
            {
                File.WriteAllText(Path.Combine(options.OutDirectory, "ids.txt"), ReviewRunner.IdList(), new UTF8Encoding(false));
            }
            else if (!options.AllowHost && !IsVirtualMachine())
            {
                problems.Add("Refusing to open windows on a physical machine. Run this on the VM, or pass --allow-host.");
                exitCode = 3;
            }
            else
            {
                scratch = Directory.CreateTempSubdirectory("uu-review-").FullName;

                // Before anything touches AppPaths: every store the app reads or writes points into the scratch folder.
                AppPathsTestOverride.DataDirectory = Path.Combine(scratch, "data");
                Directory.CreateDirectory(AppPathsTestOverride.DataDirectory);
                ScanResultCache.PathOverride = Path.Combine(scratch, "scan-cache.json");
                HistoryService.PathOverride = Path.Combine(scratch, "history.json");
                Dialogs.SuppressInteractiveDialogs = true;
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                exitCode = await ReviewRunner.RunAsync(app, options, Path.Combine(scratch, "sessions"), problems);
            }
        }
        catch (Exception ex)
        {
            problems.Add(ex.ToString());
            exitCode = 1;
        }
        finally
        {
            if (problems.Count > 0)
                File.WriteAllText(Path.Combine(options.OutDirectory, "review-error.txt"), string.Join(Environment.NewLine, problems) + Environment.NewLine, new UTF8Encoding(false));

            if (scratch is not null)
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // scratch only
                }
            }
        }

        app.Shutdown(exitCode);
    }

    /// <summary>The harness opens real windows, so it only runs where nobody sits: the BIOS strings of a virtual machine say so.</summary>
    private static bool IsVirtualMachine()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            var text = string.Concat(key?.GetValue("SystemManufacturer"), " ", key?.GetValue("SystemProductName"), " ", key?.GetValue("BIOSVendor"));
            string[] markers = ["virtual", "vmware", "qemu", "kvm", "hyper-v", "xen", "parallels", "innotek", "bochs"];
            return markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
#endif
