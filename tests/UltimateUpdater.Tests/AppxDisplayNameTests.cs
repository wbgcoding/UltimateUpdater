using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>Store apps list with the manifest's display name and publisher display name, and the packages nobody opens stay out.</summary>
public class AppxDisplayNameTests
{
    private const string Paint = "Microsoft.Paint_11.2.0.0_x64__8wekyb3d8bbwe";

    private static string Package(
        string name = "Microsoft.Paint",
        string fullName = Paint,
        string publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US",
        string? displayName = "ms-resource:AppName",
        string? publisherDisplayName = "ms-resource:PublisherDisplayName",
        string extra = "") =>
        "{\"Name\":\"" + name + "\",\"PackageFullName\":\"" + fullName + "\",\"PackageFamilyName\":\"f\",\"Publisher\":\"" + publisher
        + "\",\"Version\":\"11.2.0.0\",\"IsFramework\":false,\"IsResourcePackage\":false,\"IsBundle\":false,\"NonRemovable\":false,"
        + "\"SignatureKind\":3,\"DisplayName\":" + Quote(displayName) + ",\"PublisherDisplayName\":" + Quote(publisherDisplayName)
        + extra + "}";

    private static string Quote(string? text) => text is null ? "null" : "\"" + text + "\"";

    private static Func<string, string?> Resources(params (string Source, string Text)[] known) =>
        source => known.Where(pair => pair.Source == source).Select(pair => pair.Text).FirstOrDefault();

    [Fact]
    public void A_resource_reference_resolves_to_the_display_name_and_the_publisher_display_name()
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(
            Package(),
            Resources(
                ("@{" + Paint + "?ms-resource:AppName}", "Paint"),
                ("@{" + Paint + "?ms-resource:PublisherDisplayName}", "Microsoft Corporation")))!);

        Assert.Equal("Paint", entry.Name);
        Assert.Equal("Microsoft Corporation", entry.Publisher);
        Assert.Equal("appx:" + Paint, entry.Key);
    }

    [Fact]
    public void A_relative_reference_falls_back_to_the_spelled_out_resource_path()
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(
            Package(publisherDisplayName: "Contoso Ltd"),
            Resources(("@{" + Paint + "?ms-resource://Microsoft.Paint/resources/AppName}", "Paint")))!);

        Assert.Equal("Paint", entry.Name);
        Assert.Equal("Contoso Ltd", entry.Publisher);
    }

    [Fact]
    public void A_plain_display_name_is_taken_as_it_is_without_a_lookup()
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(
            Package(displayName: "Notes", publisherDisplayName: "Contoso"),
            _ => throw new InvalidOperationException("no lookup expected"))!);

        Assert.Equal("Notes", entry.Name);
        Assert.Equal("Contoso", entry.Publisher);
    }

    [Fact]
    public void An_unresolvable_reference_falls_back_to_the_package_name_and_the_common_name_of_the_subject()
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(Package(), _ => null)!);

        Assert.Equal("Microsoft.Paint", entry.Name);
        Assert.Equal("Microsoft Corporation", entry.Publisher);
    }

    [Fact]
    public void A_lookup_that_throws_or_returns_the_reference_again_is_unresolved_and_loses_no_other_package()
    {
        const string other = "Contoso.Notes_1.0.0.0_x64__abc";
        var json = "[" + Package() + "," + Package("Contoso.Notes", other, "CN=Contoso", "Notes", "Contoso") + "]";

        var entries = AppxPackageReader.ParseJson(
            json,
            source => source.Contains("AppName") ? throw new System.Runtime.InteropServices.COMException("no pri") : "ms-resource:x")!;

        Assert.Equal(["Microsoft.Paint", "Notes"], entries.Select(entry => entry.Name));
        Assert.Equal("Microsoft Corporation", entries[0].Publisher);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_package_without_manifest_texts_keeps_its_package_name_and_the_subject_common_name(string? text)
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(
            Package(displayName: text, publisherDisplayName: text, publisher: "CN=\\\"Acme, Inc.\\\", O=Acme"), _ => null)!);

        Assert.Equal("Microsoft.Paint", entry.Name);
        Assert.Equal("Acme, Inc.", entry.Publisher);
    }

    [Fact]
    public void A_subject_without_a_common_name_stays_as_it_is()
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(
            Package(displayName: null, publisherDisplayName: null, publisher: "O=Acme, C=US"), _ => null)!);

        Assert.Equal("O=Acme, C=US", entry.Publisher);
    }

    [Theory]
    [InlineData("\"IsFramework\":false", "\"IsFramework\":true")]
    [InlineData("\"IsResourcePackage\":false", "\"IsResourcePackage\":true")]
    [InlineData("\"IsBundle\":false", "\"IsBundle\":true")]
    [InlineData("\"NonRemovable\":false", "\"NonRemovable\":true")]
    [InlineData("\"SignatureKind\":3", "\"SignatureKind\":4")]
    [InlineData("\"SignatureKind\":3", "\"SignatureKind\":\"System\"")]
    public void Packages_nobody_installed_or_could_remove_are_skipped_by_their_properties(string from, string to)
    {
        var json = Package().Replace(from, to);

        Assert.NotEqual(Package(), json);
        Assert.Empty(AppxPackageReader.ParseJson(json, _ => "x")!);
    }

    [Fact]
    public void A_package_whose_manifest_offers_no_app_to_open_is_skipped_and_one_with_an_app_stays()
    {
        var hidden = Package("Microsoft.StartExperiencesApp", "Microsoft.StartExperiencesApp_1.0.0.0_x64__abc", extra: ",\"HasVisibleApp\":false");
        var language = Package(
            "Microsoft.LanguageExperiencePackde-DE", "Microsoft.LanguageExperiencePackde-DE_1.0.0.0_neutral__abc", extra: ",\"HasVisibleApp\":false");
        var paint = Package(extra: ",\"HasVisibleApp\":true");

        var entries = AppxPackageReader.ParseJson("[" + hidden + "," + language + "," + paint + "]", _ => "Paint")!;

        Assert.Equal(["Paint"], entries.Select(entry => entry.Name));
    }

    [Theory]
    [InlineData(",\"HasVisibleApp\":null")]
    [InlineData("")]
    public void A_package_whose_manifest_could_not_be_read_stays_listed(string extra)
    {
        var entry = Assert.Single(AppxPackageReader.ParseJson(Package(displayName: null, publisherDisplayName: null, extra: extra), _ => null)!);

        Assert.Equal("Microsoft.Paint", entry.Name);
    }

    [Fact]
    public void The_script_reads_the_manifest_per_package_inside_its_own_try()
    {
        Assert.Contains("Get-AppxPackageManifest", AppxPackageReader.Script);
        Assert.Contains("PublisherDisplayName", AppxPackageReader.Script);
        Assert.Contains("try {", AppxPackageReader.Script);
        Assert.DoesNotContain("\"", AppxPackageReader.Script);
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation", "Microsoft Corporation")]
    [InlineData("O=Acme, cn=Acme Ltd", "Acme Ltd")]
    [InlineData("CN=", null)]
    [InlineData("", null)]
    public void The_common_name_is_read_from_a_distinguished_name(string subject, string? expected)
    {
        Assert.Equal(expected, AppxPackageReader.CommonName(subject));
    }
}
