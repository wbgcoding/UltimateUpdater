using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every direct package referenced by either project must be named in <c>NOTICE-dependencies.md</c>,
/// so a new dependency cannot silently ship without its licence being recorded. The real check reads
/// both live <c>.csproj</c> files; <see cref="The_check_flags_a_package_the_notice_does_not_mention"/>
/// proves the comparison actually fires, using in-memory text rather than touching either project.
/// </summary>
public class DependencyNoticeTests
{
    private static readonly Regex PackageReference = new(
        @"<PackageReference\s+Include=""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_referenced_package_is_named_in_the_notice_file()
    {
        var root = RepoRoot();
        var noticeText = File.ReadAllText(Path.Combine(root, "NOTICE-dependencies.md"));

        var csprojFiles = new[]
        {
            Path.Combine(root, "src", "UltimateUpdater", "UltimateUpdater.csproj"),
            Path.Combine(root, "tests", "UltimateUpdater.Tests", "UltimateUpdater.Tests.csproj"),
        };

        var missing = MissingPackages(csprojFiles.Select(File.ReadAllText), noticeText);

        Assert.True(missing.Count == 0,
            $"packages referenced but not in NOTICE-dependencies.md: {string.Join(", ", missing)}");
    }

    /// <summary>A comparison that never finds a gap would stay green even after the notice file rotted
    /// out of date; this proves the detector fires on a package it has never seen, without touching
    /// either real project file.</summary>
    [Fact]
    public void The_check_flags_a_package_the_notice_does_not_mention()
    {
        const string fakeCsproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
                <PackageReference Include="Totally.Unlisted.Package" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """;
        const string fakeNotice = "| CommunityToolkit.Mvvm | 8.4.2 | MIT |";

        var missing = MissingPackages([fakeCsproj], fakeNotice);

        Assert.Equal(["Totally.Unlisted.Package"], missing);
    }

    [Fact]
    public void A_package_that_is_named_is_not_flagged()
    {
        const string fakeCsproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
              </ItemGroup>
            </Project>
            """;
        const string fakeNotice = "| CommunityToolkit.Mvvm | 8.4.2 | MIT |";

        Assert.Empty(MissingPackages([fakeCsproj], fakeNotice));
    }

    private static List<string> MissingPackages(IEnumerable<string> csprojContents, string noticeText)
    {
        var missing = new List<string>();

        foreach (var content in csprojContents)
        {
            foreach (Match match in PackageReference.Matches(content))
            {
                var package = match.Groups[1].Value;
                if (!noticeText.Contains(package, StringComparison.OrdinalIgnoreCase))
                    missing.Add(package);
            }
        }

        return missing;
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
