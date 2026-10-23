using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class PackageManagerParsersTests
{
    [Fact]
    public void Chocolatey_reads_rows_and_skips_pinned()
    {
        const string stdout = """
            git|2.54.0|2.55.0|false
            nodejs|20.1.0|22.0.0|false
            pinnedapp|1.0.0|2.0.0|true
            """;

        var packages = PackageManagerParsers.ParseChocolatey(stdout);

        Assert.Equal(2, packages.Count);
        Assert.Equal("git", packages[0].Id);
        Assert.Equal("2.55.0", packages[0].Available);
        Assert.DoesNotContain(packages, p => p.Id == "pinnedapp");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage without pipes")]
    [InlineData("only|two")]
    public void Chocolatey_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseChocolatey(input));

    [Fact]
    public void Scoop_reads_outdated_rows_only()
    {
        const string stdout = """
            Name       Installed Version Latest Version
            ---------- ----------------- --------------
            neovim     0.10.0            0.11.1
            ripgrep    14.1.0            14.1.0
            """;

        var packages = PackageManagerParsers.ParseScoopStatus(stdout);

        // header/rule rows are rejected, and ripgrep is already current
        Assert.Single(packages);
        Assert.Equal("neovim", packages[0].Id);
        Assert.Equal("0.11.1", packages[0].Available);
    }

    /// <summary>scoop status also lists apps that are not outdated (an empty Latest Version cell, then an Info
    /// column), and held apps that `scoop update` skips: neither is an update anyone can install.</summary>
    [Fact]
    public void Scoop_skips_rows_without_a_latest_version_and_held_apps()
    {
        const string stdout = """
            Name   Installed Version Latest Version Missing Dependencies Info
            ----   ----------------- -------------- -------------------- ----
            foo    1.0                              7zip
            git    2.40.0            2.41.0                              Held package
            neovim 0.10.0            0.11.1
            """;

        var packages = PackageManagerParsers.ParseScoopStatus(stdout);

        var only = Assert.Single(packages);
        Assert.Equal("neovim", only.Id);
        Assert.Equal("0.11.1", only.Available);
    }

    [Fact]
    public void Chocolatey_skips_a_package_reported_with_its_own_version_as_available()
    {
        Assert.Empty(PackageManagerParsers.ParseChocolatey("mytool|1.2.0|1.2.0|false"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Everything is up to date")]
    public void Scoop_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseScoopStatus(input));

    /// <summary>
    /// A digit-first heuristic would reject every one of scoop's own non-numeric version words, silently
    /// dropping those packages from the whole scan.
    /// </summary>
    [Fact]
    public void Scoop_accepts_non_numeric_version_words()
    {
        const string stdout = """
            Name       Installed Version Latest Version
            ---------- ----------------- --------------
            somepkg    current           nightly
            other      v1.1              v1.2
            """;

        var packages = PackageManagerParsers.ParseScoopStatus(stdout);

        Assert.Equal(2, packages.Count);
        Assert.Contains(packages, p => p.Id == "somepkg" && p.Available == "nightly");
        Assert.Contains(packages, p => p.Id == "other" && p.Available == "v1.2");
    }

    [Fact]
    public void Pip_reads_json()
    {
        const string json = """
            [{"name":"requests","version":"2.31.0","latest_version":"2.32.3","latest_filetype":"wheel"}]
            """;

        var packages = PackageManagerParsers.ParsePipJson(json);

        Assert.Single(packages);
        Assert.Equal("requests", packages[0].Id);
        Assert.Equal("2.31.0", packages[0].Installed);
        Assert.Equal("2.32.3", packages[0].Available);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"unexpected\":\"object\"}")]
    public void Pip_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParsePipJson(input));

    [Fact]
    public void Npm_reads_json_and_skips_current_packages()
    {
        const string json = """
            {"typescript":{"current":"5.4.0","wanted":"5.6.2","latest":"5.6.2"},
             "eslint":{"current":"9.0.0","wanted":"9.0.0","latest":"9.0.0"}}
            """;

        var packages = PackageManagerParsers.ParseNpmJson(json);

        Assert.Single(packages);
        Assert.Equal("typescript", packages[0].Id);
        Assert.Equal("5.6.2", packages[0].Available);
    }

    [Fact]
    public void Npm_skips_a_package_installed_ahead_of_its_latest_tag()
    {
        const string json = """
            {"typescript":{"current":"5.9.0-dev.20250101","wanted":"5.9.0-dev.20250101","latest":"5.8.2"}}
            """;

        Assert.Empty(PackageManagerParsers.ParseNpmJson(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("npm ERR! something went wrong")]
    [InlineData("[]")]
    public void Npm_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseNpmJson(input));

    [Fact]
    public void DotnetToolList_reads_rows_after_the_rule()
    {
        const string stdout = """
            Package Id      Version      Commands
            --------------- ------------ ------------
            dotnet-ef       9.0.0        dotnet-ef
            csharpier       0.28.2       dotnet-csharpier
            """;

        var packages = PackageManagerParsers.ParseDotnetToolList(stdout);

        Assert.Equal(2, packages.Count);
        Assert.Equal("dotnet-ef", packages[0].Id);
        Assert.Equal("9.0.0", packages[0].Installed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("No tools installed.")]
    public void DotnetToolList_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParseDotnetToolList(input));

    [Fact]
    public void NuGet_index_returns_newest_stable_ignoring_prereleases()
    {
        const string json = """{"versions":["1.0.0","2.0.0","2.1.0","3.0.0-preview.1"]}""";

        Assert.Equal("2.1.0", PackageManagerParsers.ParseNuGetLatestStable(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"versions\":[]}")]
    [InlineData("{\"versions\":[\"1.0.0-beta\"]}")]
    public void NuGet_index_returns_null_when_there_is_no_stable_version(string input) =>
        Assert.Null(PackageManagerParsers.ParseNuGetLatestStable(input));

    /// <summary>
    /// A non-string entry in the feed must not throw out of the parser, the .NET tool source does not
    /// catch that, and every tool update would vanish behind a provider error over one odd feed entry.
    /// </summary>
    [Fact]
    public void NuGet_index_skips_entries_that_are_not_strings()
    {
        const string json = """{"versions":["1.0.0",42,null,{"v":"x"},"2.0.0"]}""";

        Assert.Equal("2.0.0", PackageManagerParsers.ParseNuGetLatestStable(json));
    }

    [Fact]
    public void PsGallery_reads_the_installed_modules()
    {
        const string json = """
            [{"Name":"Pester","Installed":"5.5.0"},
             {"Name":"PSReadLine","Installed":"2.3.5"}]
            """;

        var packages = PackageManagerParsers.ParsePsGalleryInstalledJson(json);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Pester", packages[0].Id);
        Assert.Equal("5.5.0", packages[0].Installed);
    }

    [Fact]
    public void PsGallery_handles_single_object_form()
    {
        const string json = """{"Name":"Pester","Installed":"5.5.0"}""";

        Assert.Single(PackageManagerParsers.ParsePsGalleryInstalledJson(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""[{"Name":"Pester"}]""")]
    public void PsGallery_never_throws_on_bad_input(string input) =>
        Assert.Empty(PackageManagerParsers.ParsePsGalleryInstalledJson(input));

    [Fact]
    public void OData_feed_yields_the_one_matched_version()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom"
                  xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                  xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><title>PSReadLine</title>
                <m:properties><d:Version>2.4.5</d:Version></m:properties></entry>
            </feed>
            """;

        Assert.Equal("2.4.5", PackageManagerParsers.ParseODataLatestVersion(xml));
    }

    /// <summary>A prefix match returning several packages must yield nothing, never a foreign version.</summary>
    [Fact]
    public void OData_feed_yields_nothing_when_it_matched_more_than_one_package()
    {
        const string xml = """
            <feed xmlns="http://www.w3.org/2005/Atom"
                  xmlns:d="http://schemas.microsoft.com/ado/2007/08/dataservices"
                  xmlns:m="http://schemas.microsoft.com/ado/2007/08/dataservices/metadata">
              <entry><title>PSReadLine</title>
                <m:properties><d:Version>2.4.5</d:Version></m:properties></entry>
              <entry><title>PSReadlineHistory</title>
                <m:properties><d:Version>20.1.23.1314</d:Version></m:properties></entry>
            </feed>
            """;

        Assert.Null(PackageManagerParsers.ParseODataLatestVersion(xml));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<feed></feed>")]
    public void OData_feed_never_throws_on_bad_input(string input) =>
        Assert.Null(PackageManagerParsers.ParseODataLatestVersion(input));

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.1", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData(null, "1.0.0", true)]
    [InlineData("1.0.0", null, false)]
    // same numeric core, but a stable release outranks a pre-release build installed under it,
    // comparing the numeric core alone would read this pair as "equal" and never offer the stable
    // release once a pre-release was installed.
    [InlineData("1.0.0-beta", "1.0.0", true)]
    // The reverse must not regress: a pre-release is never offered as an "upgrade" over a stable install.
    [InlineData("1.0.0", "1.0.0-beta", false)]
    [InlineData("not-a-version", "also-not", true)]
    [InlineData("10", "2", false)]
    [InlineData("2", "10", true)]
    [InlineData("1.0", "1.0", false)]
    // A missing part counts as zero: the same release written with fewer parts is no update.
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("10", "10.0.0", false)]
    [InlineData("1.2.3", "1.2.3.0", false)]
    [InlineData("1.2", "1.2.1", true)]
    [InlineData("1.2.1", "1.2", false)]
    // Two different pre-release suffixes on one core are ordered by semver precedence.
    [InlineData("1.0.0-rc2", "1.0.0-rc1", false)]
    [InlineData("1.0.0-rc1", "1.0.0-rc2", true)]
    [InlineData("1.0.0-beta", "1.0.0-rc.1", true)]
    // Build metadata takes no part: it neither makes a plain release look older nor a rebuild look newer.
    [InlineData("1.0.0+abc", "1.0.0", false)]
    [InlineData("1.0.0-rc.1+b1", "1.0.0-rc.1+b2", false)]
    // A suffix that is not semver shaped has no trustworthy order: no row.
    [InlineData("1.0.0-x_y", "1.0.0-x_z", false)]
    // A leading "v" before a digit is a tag decoration, not part of the version.
    [InlineData("v2.0.0", "v1.0.0", false)]
    [InlineData("v1.0.0", "v1.1.0", true)]
    [InlineData("1.0.0", "v1.0.0", false)]
    [InlineData("V1.0.0", "1.0.1", true)]
    public void IsNewer_compares_versions_and_falls_back_to_inequality(string? installed, string? available, bool expected) =>
        Assert.Equal(expected, PackageManagerParsers.IsNewer(installed, available));

    // ---- this fix's re-check parsers ----

    [Fact]
    public void Chocolatey_exact_version_matches_by_id_and_ignores_other_rows()
    {
        const string stdout = """
            Chocolatey v2.0.0
            git|2.55.0
            1 packages installed.
            """;

        Assert.Equal("2.55.0", PackageManagerParsers.ParseChocolateyExactVersion(stdout, "git"));
        Assert.Null(PackageManagerParsers.ParseChocolateyExactVersion(stdout, "nodejs"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage without pipes")]
    public void Chocolatey_exact_version_never_throws_on_bad_input(string input) =>
        Assert.Null(PackageManagerParsers.ParseChocolateyExactVersion(input, "git"));

    [Fact]
    public void Npm_list_version_reads_the_matching_dependency()
    {
        const string json = """{"dependencies":{"eslint":{"version":"9.1.0"}}}""";

        Assert.Equal("9.1.0", PackageManagerParsers.ParseNpmListVersion(json, "eslint"));
        Assert.Null(PackageManagerParsers.ParseNpmListVersion(json, "typescript"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"dependencies":"not an object"}""")]
    public void Npm_list_version_never_throws_on_bad_input(string input) =>
        Assert.Null(PackageManagerParsers.ParseNpmListVersion(input, "eslint"));

    [Fact]
    public void Pip_show_version_reads_the_version_label()
    {
        const string stdout = """
            Name: requests
            Version: 2.32.3
            Summary: Python HTTP for Humans.
            """;

        Assert.Equal("2.32.3", PackageManagerParsers.ParsePipShowVersion(stdout));
    }

    [Theory]
    [InlineData("")]
    [InlineData("WARNING: Package(s) not found: requests")]
    public void Pip_show_version_never_throws_on_bad_input(string input) =>
        Assert.Null(PackageManagerParsers.ParsePipShowVersion(input));
}
