using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class NvidiaDriverLookupTests
{
    private const string SampleResponse = """
        {"IDS":[{"downloadInfo":{
            "Version":"610.74",
            "DownloadURL":"https://us.download.nvidia.com/Windows/610.74/610.74-desktop-win10-win11-64bit-international-dch-whql.exe",
            "DownloadURLFileSize":"823.35 MB",
            "ReleaseDateTime":"Tue Jul 07, 2026",
            "DetailsURL":"https://www.nvidia.com/en-us/drivers/details/274187/"
        }}]}
        """;

    [Fact]
    public void A_real_response_yields_the_version_url_size_and_date()
    {
        var driver = NvidiaDriverLookup.ParseDriverResponse(SampleResponse);

        Assert.NotNull(driver);
        Assert.Equal("610.74", driver!.Version);
        Assert.StartsWith("https://us.download.nvidia.com/", driver.DownloadUrl);
        Assert.Equal((long)(823.35 * 1024 * 1024), driver.SizeBytes);
        Assert.Equal(2026, driver.ReleaseDate!.Value.Year);
        Assert.Contains("nvidia.com", driver.DetailsUrl);
    }

    /// <summary>
    /// DetailsURL becomes the row's Open target, and it arrives from a remote response, so a scheme that
    /// would invoke a local protocol handler is dropped where it is read, not where it is clicked. No
    /// details page is a better row than one this app cannot vouch for.
    /// </summary>
    [Theory]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public void A_details_url_that_is_not_http_is_dropped(string details)
    {
        var json = """
            {"IDS":[{"downloadInfo":{
                "Version":"610.74",
                "DownloadURL":"https://us.download.nvidia.com/Windows/610.74/x.exe",
                "DetailsURL":"__DETAILS__"
            }}]}
            """.Replace("__DETAILS__", details);

        var driver = NvidiaDriverLookup.ParseDriverResponse(json);

        Assert.NotNull(driver);
        Assert.Null(driver!.DetailsUrl);
    }

    /// <summary>
    /// An unrecognised unit must never fall back to MB's multiplier, and neither a negative number nor
    /// an infinite one may be accepted, an invented or nonsensical download size is worse than none at
    /// all.
    /// </summary>
    [Theory]
    [InlineData("823.35 TB")]  // unrecognised unit, must never guess MB
    [InlineData("-5 MB")]      // negative
    [InlineData("Infinity MB")]
    [InlineData("NaN MB")]
    public void An_unrecognised_or_nonsensical_size_is_never_guessed(string size)
    {
        var json = """
            {"IDS":[{"downloadInfo":{
                "Version":"610.74",
                "DownloadURL":"https://us.download.nvidia.com/Windows/610.74/x.exe",
                "DownloadURLFileSize":"__SIZE__"
            }}]}
            """.Replace("__SIZE__", size);

        var driver = NvidiaDriverLookup.ParseDriverResponse(json);

        Assert.NotNull(driver);
        Assert.Null(driver!.SizeBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"IDS\":[]}")]
    [InlineData("{\"IDS\":[{\"downloadInfo\":{\"Version\":\"\"}}]}")]
    public void A_missing_or_empty_response_yields_null(string json)
    {
        Assert.Null(NvidiaDriverLookup.ParseDriverResponse(json));
    }

    private const string ProductXml = """
        <LookupValueSearch>
          <LookupValues>
            <LookupValue ParentID="127"><Name>GeForce RTX 4070</Name><Value>1015</Value></LookupValue>
            <LookupValue ParentID="127"><Name>GeForce RTX 4070 Ti</Name><Value>1032</Value></LookupValue>
            <LookupValue ParentID="120"><Name>GeForce RTX 3060</Name><Value>925</Value></LookupValue>
          </LookupValues>
        </LookupValueSearch>
        """;

    [Fact]
    public void A_detected_adapter_name_maps_to_its_product_id()
    {
        Assert.Equal(1015, NvidiaDriverLookup.FindProductId(ProductXml, "NVIDIA GeForce RTX 4070"));
        Assert.Equal(1032, NvidiaDriverLookup.FindProductId(ProductXml, "NVIDIA GeForce RTX 4070 Ti"));
    }

    [Fact]
    public void A_4070_does_not_get_mistaken_for_a_4070_Ti()
    {
        // exact "RTX 4070" must not resolve to the longer "RTX 4070 Ti"
        Assert.Equal(1015, NvidiaDriverLookup.FindProductId(ProductXml, "GeForce RTX 4070"));
    }

    [Fact]
    public void An_unknown_adapter_maps_to_nothing()
    {
        Assert.Null(NvidiaDriverLookup.FindProductId(ProductXml, "GeForce RTX 9090"));
        Assert.Null(NvidiaDriverLookup.FindProductId(null, "anything"));
    }

    [Theory]
    [InlineData("560.94", "561.09", true)]
    [InlineData("610.74", "610.74", false)]
    [InlineData("610.74", "610.70", false)]
    [InlineData(null, "610.74", true)]
    [InlineData("560.94", null, false)]
    public void Newer_is_a_numeric_comparison(string? installed, string? latest, bool expected)
    {
        Assert.Equal(expected, NvidiaDriverLookup.IsNewer(installed, latest));
    }

    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.5152", "551.52")]
    [InlineData("32.0.15.7602", "576.02")]
    [InlineData(null, null)]
    [InlineData("1.2", null)]
    public void The_wmi_driver_version_decodes_to_the_marketing_version(string? wmi, string? expected)
    {
        Assert.Equal(expected, DriverDetection.DecodeNvidiaVersion(wmi));
    }
}

/// <summary>
/// This is defense-in-depth behind the nvidia.com download-host allowlist, but the O= (organization)
/// RDN must still be matched exactly rather than as a substring search over the whole Subject, otherwise
/// any validly-signed binary whose DN merely mentions "NVIDIA" somewhere (a partner's OU, another
/// company's O=) would pass the gate before a self-elevating driver install.
/// </summary>
public class NvidiaCertificateSubjectTests
{
    [Fact]
    public void A_genuine_nvidia_organization_passes()
    {
        const string subject = "CN=NVIDIA Corporation, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US";
        Assert.True(NvidiaDriverSource.IsNvidiaOrganization(subject));
    }

    [Fact]
    public void Nvidia_mentioned_only_in_an_unrelated_component_is_refused()
    {
        const string subject = "CN=Some Other App, OU=NVIDIA Partner Program, O=SomeOtherCompany Inc, C=US";
        Assert.False(NvidiaDriverSource.IsNvidiaOrganization(subject));
    }

    [Fact]
    public void A_subject_with_no_nvidia_mention_at_all_is_refused()
    {
        const string subject = "CN=Contoso App, O=Contoso Ltd, C=US";
        Assert.False(NvidiaDriverSource.IsNvidiaOrganization(subject));
    }

    [Fact]
    public void Malformed_subject_text_is_refused_rather_than_throwing()
    {
        Assert.False(NvidiaDriverSource.IsNvidiaOrganization("not a distinguished name at all ==="));
    }
}
