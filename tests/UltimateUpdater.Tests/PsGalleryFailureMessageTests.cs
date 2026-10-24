using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

public class PsGalleryFailureMessageTests
{
    [Fact]
    public void A_failed_module_install_carries_the_reason_the_cmdlet_printed()
    {
        using var client = new HttpClient();
        var source = new PsGallerySource(new LoggingService(), client);

        var message = source.FailureMessage("install", "Pester",
            new ProcessResult(1, "", "Install-Module : No match was found for the specified search criteria"));

        Assert.Contains("install failed for 'Pester'", message);
        Assert.Contains("No match was found for the specified search criteria", message);
        Assert.Contains("(exit code 1)", message);
    }

    [Fact]
    public void A_silent_failure_still_names_the_exit_code()
    {
        using var client = new HttpClient();
        var source = new PsGallerySource(new LoggingService(), client);

        var message = source.FailureMessage("uninstall", "Pester", new ProcessResult(5, "", ""));

        Assert.Contains("uninstall failed for 'Pester'", message);
        Assert.Contains("exit", message);
        Assert.Contains("5", message);
    }
}
