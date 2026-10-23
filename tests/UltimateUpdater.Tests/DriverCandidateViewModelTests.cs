using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The version/date second line and the multi-line detail tooltip, for both driver
/// candidate rows. Mutates <see cref="LocalizationService"/>'s process-wide culture like
/// <see cref="LocalizationServiceTests"/> already does; every test restores "en" itself.
/// </summary>
[Collection("UiLanguage")]
public class DriverCandidateViewModelTests
{
    private static readonly DateTimeOffset SampleDate = new(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Dates read in the UI language, the same culture the view models format with.</summary>
    private static string Day(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentUICulture);

    private static DriverDeviceCandidateDto DeviceDto(
        DateTimeOffset? lastConnected = null, DriverLastSeenKind kind = DriverLastSeenKind.Unknown,
        string? provider = null, string? originalName = null, string? storePath = null,
        string? version = null, DateTimeOffset? date = null, long sizeBytes = 0,
        string? newerVersion = null, DateTimeOffset? newerDate = null) => new()
    {
        InstanceId = "USB\\VID_1234",
        DisplayName = "Old Scanner",
        ClassName = "Image",
        PackageName = "oem12.inf",
        LastConnected = lastConnected,
        LastConnectedKind = kind,
        Provider = provider,
        OriginalName = originalName,
        StorePath = storePath,
        Version = version,
        Date = date,
        SizeBytes = sizeBytes,
        NewerVersion = newerVersion,
        NewerDate = newerDate,
    };

    private static DriverPackageCandidateDto PackageDto(
        string? version = null, DateTimeOffset? date = null, long sizeBytes = 0,
        string? originalName = null, string? storePath = null,
        string? newerVersion = null, DateTimeOffset? newerDate = null) => new()
    {
        PackageName = "oem7.inf",
        Provider = "Acme",
        ClassName = "Sound",
        Version = version,
        Date = date,
        SizeBytes = sizeBytes,
        OriginalName = originalName,
        StorePath = storePath,
        NewerVersion = newerVersion,
        NewerDate = newerDate,
    };

    [Fact]
    public void LastConnectedText_uses_connected_wording_for_a_real_connect_event()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(SampleDate, DriverLastSeenKind.Connected));

        Assert.Equal($"Last connected {Day(SampleDate)}", vm.LastConnectedText);
    }

    [Fact]
    public void LastConnectedText_uses_installed_wording_for_an_install_date_fallback()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(SampleDate, DriverLastSeenKind.Installed));

        Assert.Equal($"Installed {Day(SampleDate)}", vm.LastConnectedText);
    }

    [Fact]
    public void LastConnectedText_shows_unknown_when_there_is_no_date_at_all()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto());

        Assert.Equal("Last connection unknown", vm.LastConnectedText);
    }

    [Fact]
    public void VersionDateText_combines_both_when_both_are_known()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(version: "1.2.3.4", date: SampleDate));

        Assert.Equal($"Version 1.2.3.4, {Day(SampleDate)}", vm.VersionDateText);
        Assert.True(vm.HasVersionDateText);
    }

    [Fact]
    public void VersionDateText_shows_only_the_version_when_the_date_is_missing()
    {
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(version: "1.2.3.4"));

        Assert.Equal("1.2.3.4", vm.VersionDateText);
    }

    [Fact]
    public void VersionDateText_shows_only_the_date_when_the_version_is_missing()
    {
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(date: SampleDate));

        Assert.Equal(Day(SampleDate), vm.VersionDateText);
    }

    [Fact]
    public void HasVersionDateText_is_false_when_neither_version_nor_date_is_known()
    {
        var vm = new DriverDeviceCandidateViewModel(DeviceDto());

        Assert.Equal(string.Empty, vm.VersionDateText);
        Assert.False(vm.HasVersionDateText);
    }

    [Fact]
    public void DetailTooltipText_lists_every_known_fact_and_skips_missing_ones()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(
            provider: "Acme", originalName: "acmescan.inf",
            storePath: @"C:\Windows\System32\DriverStore\FileRepository\acmescan.inf_amd64_abc",
            version: "1.2.3.4", date: SampleDate, sizeBytes: 4L * 1024 * 1024));

        var lines = vm.DetailTooltipText.Split('\n');

        Assert.Contains("Provider: Acme", lines);
        Assert.Contains("Class: Image", lines);
        Assert.Contains("INF file: oem12.inf", lines);
        Assert.Contains("Original name: acmescan.inf", lines);
        Assert.Contains(@"Driver store path: C:\Windows\System32\DriverStore\FileRepository\acmescan.inf_amd64_abc", lines);
        Assert.Contains("Version: 1.2.3.4", lines);
        Assert.Contains($"Date: {Day(SampleDate)}", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Installed now", StringComparison.Ordinal));
    }

    [Fact]
    public void DetailTooltipText_ends_with_the_installed_now_line_when_superseded()
    {
        LocalizationService.Instance.Apply("en");
        var newerDate = SampleDate.AddYears(1);
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(newerVersion: "2.0.0.0", newerDate: newerDate));

        Assert.Equal($"Installed now: version 2.0.0.0, {Day(newerDate)}", vm.DetailTooltipText.Split('\n')[^1]);
    }

    [Fact]
    public void DetailTooltipText_omits_size_when_it_is_zero()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverDeviceCandidateViewModel(DeviceDto(provider: "Acme"));

        Assert.DoesNotContain(vm.DetailTooltipText.Split('\n'), l => l.StartsWith("Size:", StringComparison.Ordinal));
    }

    [Fact]
    public void Package_VersionDateText_and_tooltip_match_the_device_rows_own_shape()
    {
        LocalizationService.Instance.Apply("en");
        var vm = new DriverPackageCandidateViewModel(PackageDto(
            version: "3.0.0.0", date: SampleDate, sizeBytes: 8L * 1024 * 1024,
            originalName: "acmesnd.inf", storePath: @"C:\Windows\System32\DriverStore\FileRepository\acmesnd.inf_amd64_def"));

        Assert.Equal($"Version 3.0.0.0, {Day(SampleDate)}", vm.VersionDateText);
        var lines = vm.DetailTooltipText.Split('\n');
        Assert.Contains("Provider: Acme", lines);
        Assert.Contains("INF file: oem7.inf", lines);
        Assert.Contains("Original name: acmesnd.inf", lines);
    }

    [Fact]
    public void Package_tooltip_ends_with_the_installed_now_line_when_superseded()
    {
        LocalizationService.Instance.Apply("en");
        var newerDate = SampleDate.AddMonths(1);
        var vm = new DriverPackageCandidateViewModel(PackageDto(newerVersion: "4.0.0.0", newerDate: newerDate));

        Assert.Equal($"Installed now: version 4.0.0.0, {Day(newerDate)}", vm.DetailTooltipText.Split('\n')[^1]);
    }
}
