using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>A module installed from a private feed has no entry in the public gallery, so it must never be listed.</summary>
public class PsGalleryScanScriptTests
{
    [Fact]
    public void The_scan_keeps_only_modules_installed_from_the_public_gallery()
    {
        Assert.Contains("$_.Repository -eq 'PSGallery'", PsGallerySource.ScanScript, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repository_check_is_part_of_the_same_filter_as_the_location_check()
    {
        var script = PsGallerySource.ScanScript;
        var filter = script.IndexOf("Where-Object", StringComparison.Ordinal);

        Assert.True(filter >= 0);
        Assert.True(script.IndexOf("$_.Repository -eq 'PSGallery'", StringComparison.Ordinal) > filter);
        Assert.True(script.IndexOf("$_.Repository", StringComparison.Ordinal) < script.IndexOf("ForEach-Object", StringComparison.Ordinal));
    }
}
