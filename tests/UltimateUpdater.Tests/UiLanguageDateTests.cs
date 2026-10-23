using System.Globalization;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// Dates in the UI read in the order of the chosen UI language, not the Windows region: an English UI on a
/// German Windows must not show "05.10.2026" next to English wording.
/// </summary>
public class UiLanguageDateTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 12, 12, 30, 0, TimeSpan.Zero);

    private static void WithCultures(string region, string ui, Action body)
    {
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(region);
            CultureInfo.CurrentUICulture = new CultureInfo(ui);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUi;
        }
    }

    [Fact]
    public void History_when_follows_the_ui_language_not_the_windows_region()
    {
        WithCultures("de-DE", "en-US", () =>
        {
            var row = new HistoryRowViewModel(new HistoryEntry
            {
                Timestamp = Stamp, SourceId = "winget", ItemId = "git", Name = "git", Success = true,
            });

            Assert.Equal(Stamp.LocalDateTime.ToString("g", new CultureInfo("en-US")), row.When);
            Assert.DoesNotContain(".", row.When.Split(' ')[0]);
        });
    }

    [Fact]
    public void A_driver_date_follows_the_ui_language_not_the_windows_region()
    {
        WithCultures("de-DE", "en-US", () =>
        {
            var row = new DriverDeviceCandidateViewModel(new DriverDeviceCandidateDto
            {
                InstanceId = "USB_VID_1234", DisplayName = "Old Scanner", ClassName = "Image", PackageName = "oem12.inf",
                Date = Stamp,
            });

            Assert.Contains(Stamp.ToLocalTime().ToString("d", new CultureInfo("en-US")), row.VersionDateText);
        });
    }
}
