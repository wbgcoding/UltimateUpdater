using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// The licence texts of everything bundled in the exe ship as THIRD-PARTY-NOTICES.txt: in the repository
/// root, beside the portable exe in dist and in the install folder. These checks keep the file complete
/// when a package is added and keep the build and the installer carrying it.
/// </summary>
public class ThirdPartyNoticesTests
{
    private static readonly Regex PackageReference = new(
        @"<PackageReference\s+Include=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Packages the app pulls in beneath its direct references and that end up in the exe.</summary>
    private static readonly string[] ShippedTransitivePackages = ["System.CodeDom", "WPF-UI.Abstractions"];

    [Fact]
    public void Every_package_the_app_references_has_its_notice_in_the_file()
    {
        var root = RepoRoot();
        var notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.txt"));
        var csproj = File.ReadAllText(Path.Combine(root, "src", "UltimateUpdater", "UltimateUpdater.csproj"));

        var missing = PackageReference.Matches(csproj).Select(m => m.Groups[1].Value)
            .Concat(ShippedTransitivePackages)
            .Where(id => !notices.Contains($"\n{id} ", StringComparison.Ordinal) && !notices.Contains($"\r\n{id} ", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0, $"no section in THIRD-PARTY-NOTICES.txt for: {string.Join(", ", missing)}");
    }

    [Fact]
    public void The_file_carries_the_runtime_notice_and_a_full_licence_text()
    {
        var notices = File.ReadAllText(Path.Combine(RepoRoot(), "THIRD-PARTY-NOTICES.txt"));

        Assert.Contains(".NET runtime (core libraries)", notices);
        Assert.Contains(".NET desktop runtime", notices);
        Assert.Contains("Permission is hereby granted, free of charge", notices);
        Assert.Contains("THE SOFTWARE IS PROVIDED", notices);
    }

    [Fact]
    public void The_build_and_the_installer_carry_the_file()
    {
        var root = RepoRoot();
        var build = File.ReadAllText(Path.Combine(root, "build.bat"));
        var setup = File.ReadAllText(Path.Combine(root, "installer", "UltimateUpdater.iss"));

        Assert.Contains("THIRD-PARTY-NOTICES.txt\" \"%DIST%\\THIRD-PARTY-NOTICES.txt\"", build);
        Assert.Matches(@"Source: ""\.\.\\THIRD-PARTY-NOTICES\.txt""; DestDir: ""\{app\}""", setup);
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
