using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// The offline, no-network fallback for a pip package PyPI has nothing to say about: the installed
/// copy's own dist-info METADATA file, read straight off disk. <see cref="PipDistInfoMetadata.ParsePublisher"/>
/// is pure text-in, so most of this tests that directly; one test drives the real disk path
/// (<see cref="PipDistInfoMetadata.ReadPublisher"/>) against a throwaway temp directory shaped like a real
/// site-packages tree.
/// </summary>
public class PipDistInfoMetadataTests
{
    [Fact]
    public void Reads_the_plain_author_field()
    {
        const string metadata = """
            Metadata-Version: 2.1
            Name: widgetlib
            Version: 1.2.3
            Author: Jane Doe
            Author-email: jane@example.com

            A long description that must never be scanned for headers.
            """;

        Assert.Equal("Jane Doe", PipDistInfoMetadata.ParsePublisher(metadata));
    }

    [Fact]
    public void Falls_back_to_the_name_in_author_email_when_author_is_blank()
    {
        const string metadata = """
            Metadata-Version: 2.1
            Name: widgetlib
            Author-email: Jane Doe <jane@example.com>
            """;

        Assert.Equal("Jane Doe", PipDistInfoMetadata.ParsePublisher(metadata));
    }

    [Fact]
    public void Falls_back_to_maintainer_then_to_a_github_project_url()
    {
        const string maintainerOnly = """
            Metadata-Version: 2.1
            Name: widgetlib
            Maintainer: Maintainers Guild
            """;
        Assert.Equal("Maintainers Guild", PipDistInfoMetadata.ParsePublisher(maintainerOnly));

        const string projectUrlOnly = """
            Metadata-Version: 2.1
            Name: widgetlib
            Project-URL: Source, https://github.com/pallets/flask
            """;
        Assert.Equal("pallets", PipDistInfoMetadata.ParsePublisher(projectUrlOnly));
    }

    [Fact]
    public void A_bare_email_with_no_name_and_no_url_yields_nothing()
    {
        const string metadata = """
            Metadata-Version: 2.1
            Name: widgetlib
            Author-email: jane@example.com
            """;

        Assert.Null(PipDistInfoMetadata.ParsePublisher(metadata));
    }

    [Fact]
    public void Header_parsing_stops_at_the_first_blank_line()
    {
        // "Author:" inside the description body must never be read as a real header.
        const string metadata = """
            Metadata-Version: 2.1
            Name: widgetlib
            Maintainer: Real Maintainer

            This package was written by someone.
            Author: Not A Real Header
            """;

        Assert.Equal("Real Maintainer", PipDistInfoMetadata.ParsePublisher(metadata));
    }

    /// <summary>Locates the right *.dist-info folder by its PEP 503 normalised name (hyphen/underscore
    /// interchangeable, case-insensitive) among several installed packages, and reads its METADATA file
    /// off the real filesystem.</summary>
    [Fact]
    public void ReadPublisher_finds_the_matching_dist_info_folder_on_disk()
    {
        var sitePackages = Directory.CreateTempSubdirectory("wu-distinfo-test-").FullName;
        try
        {
            var target = Path.Combine(sitePackages, "typing_extensions-4.5.0.dist-info");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "METADATA"), "Metadata-Version: 2.1\nAuthor: Guido\n");

            var otherPackage = Path.Combine(sitePackages, "requests-2.31.0.dist-info");
            Directory.CreateDirectory(otherPackage);
            File.WriteAllText(Path.Combine(otherPackage, "METADATA"), "Metadata-Version: 2.1\nAuthor: Kenneth\n");

            // Hyphenated request against an underscored folder name, the normalisation this is testing.
            Assert.Equal("Guido", PipDistInfoMetadata.ReadPublisher(sitePackages, "typing-extensions"));
            Assert.Equal("Kenneth", PipDistInfoMetadata.ReadPublisher(sitePackages, "requests"));
            Assert.Null(PipDistInfoMetadata.ReadPublisher(sitePackages, "not-installed"));
        }
        finally
        {
            Directory.Delete(sitePackages, recursive: true);
        }
    }

    /// <summary>A hyphen in the version part ("pkg-2.0-1") and an old, unescaped name with a hyphen
    /// ("my-pkg-1.0") both still resolve; a longer package name never claims a shorter one's folder.</summary>
    [Fact]
    public void ReadPublisher_finds_folders_whose_version_or_name_carries_a_hyphen()
    {
        var sitePackages = Directory.CreateTempSubdirectory("wu-distinfo-test-").FullName;
        try
        {
            WriteDistInfo(sitePackages, "widget-2.0-1.dist-info", "Alice");
            WriteDistInfo(sitePackages, "my-pkg-1.0.dist-info", "Bob");
            WriteDistInfo(sitePackages, "foo-bar-3.1.dist-info", "Carol");

            Assert.Equal("Alice", PipDistInfoMetadata.ReadPublisher(sitePackages, "widget"));
            Assert.Equal("Bob", PipDistInfoMetadata.ReadPublisher(sitePackages, "my-pkg"));
            Assert.Equal("Bob", PipDistInfoMetadata.ReadPublisher(sitePackages, "My_Pkg"));
            Assert.Equal("Carol", PipDistInfoMetadata.ReadPublisher(sitePackages, "foo-bar"));
            Assert.Null(PipDistInfoMetadata.ReadPublisher(sitePackages, "foo"));
        }
        finally
        {
            Directory.Delete(sitePackages, recursive: true);
        }
    }

    private static void WriteDistInfo(string sitePackages, string folder, string author)
    {
        var directory = Path.Combine(sitePackages, folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "METADATA"), $"Metadata-Version: 2.1\nAuthor: {author}\n");
    }

    [Fact]
    public void ReadPublisher_is_null_safe_for_a_missing_site_packages_directory()
    {
        Assert.Null(PipDistInfoMetadata.ReadPublisher(null, "anything"));
        Assert.Null(PipDistInfoMetadata.ReadPublisher(@"C:\definitely-not-a-real-path-xyz", "anything"));
    }
}
