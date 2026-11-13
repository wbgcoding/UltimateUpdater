using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every text parser is fed output of tools this app does not control (and sometimes a proxy error page or a
/// half-written file). The documented rule is "skip the bad row, never throw", because a throw inside a
/// parser fails a whole source. One hostile corpus is thrown at all of them so a new parser cannot quietly
/// break the rule.
/// </summary>
public class ParserRobustnessTests
{
    private static readonly string[] Corpus =
    [
        "",
        " ",
        "\r\n\r\n",
        "\0\0\0",
        "\u0001\u0002\u007f",
        "{",
        "}",
        "[",
        "]",
        "null",
        "true",
        "42",
        "\"just a string\"",
        "[null]",
        "[[],{},null,1,\"x\"]",
        "{\"a\":null}",
        "{\"name\":null,\"version\":null,\"latest_version\":null}",
        "[{\"name\":null},{\"name\":[1,2]},{\"name\":{}}]",
        "{\"name\":\"x\",\"version\":{\"a\":1},\"latest\":[1]}",
        "{\"a\":{\"b\":{\"c\":null}}}",
        "<",
        "<a><b></a>",
        "<?xml version=\"1.0\"?><feed></feed>",
        "|||",
        "a|b",
        "a|b|c|d|e|f|g",
        "-----",
        "Name   Id   Version\r\n----\r\n",
        "\u001b[31mred\u001b[0m \u001b[2K",
        "Ünïcödé 日本語 😀",
        "  \t  \t ",
        "1.2.3 -> 4.5.6",
        "[package] = ???",
        "\"description\": ",
        "{\"description\":42}",
        "version = \"1\"\n[",
        "key=\n=value\n==",
    ];

    private static IEnumerable<string> HostileInputs()
    {
        foreach (var item in Corpus)
            yield return item;
        // very long, single line and many lines
        yield return new string('x', 300_000);
        yield return string.Join('\n', Enumerable.Repeat("x|y|z", 20_000));
        yield return string.Concat(Enumerable.Repeat("[", 5000));
        yield return string.Concat(Enumerable.Repeat("{\"a\":", 2000));
        yield return string.Concat(Enumerable.Repeat("<a>", 5000));
    }

    private static readonly (string Name, Func<string, object?> Parse)[] Parsers =
    [
        ("ParseChocolatey", s => PackageManagerParsers.ParseChocolatey(s)),
        ("ParseChocolateyExactVersion", s => PackageManagerParsers.ParseChocolateyExactVersion(s, "git")),
        ("ParseScoopStatus", s => PackageManagerParsers.ParseScoopStatus(s)),
        ("ParsePipJson", s => PackageManagerParsers.ParsePipJson(s)),
        ("ParseNpmJson", s => PackageManagerParsers.ParseNpmJson(s)),
        ("ParseNpmListVersion", s => PackageManagerParsers.ParseNpmListVersion(s, "left-pad")),
        ("ParsePipShowVersion", s => PackageManagerParsers.ParsePipShowVersion(s)),
        ("ParseDotnetToolList", s => PackageManagerParsers.ParseDotnetToolList(s)),
        ("ParseNuGetLatestStable", s => PackageManagerParsers.ParseNuGetLatestStable(s)),
        ("ParseODataLatestVersion", s => PackageManagerParsers.ParseODataLatestVersion(s)),
        ("ParsePsGalleryInstalledJson", s => PackageManagerParsers.ParsePsGalleryInstalledJson(s)),
        ("ParseGemOutdated", s => PackageManagerParsers.ParseGemOutdated(s)),
        ("ParseComposerJson", s => PackageManagerParsers.ParseComposerJson(s)),
        ("ParseRustupCheck", s => PackageManagerParsers.ParseRustupCheck(s)),
        ("ParseVcpkgUpdate", s => PackageManagerParsers.ParseVcpkgUpdate(s)),
        ("ParseSdkManagerList", s => PackageManagerParsers.ParseSdkManagerList(s)),
        ("ParseGhExtensionList", s => PackageManagerParsers.ParseGhExtensionList(s)),
        ("ParsePipxJson", s => PackageManagerParsers.ParsePipxJson(s)),
        ("ParseVsCodeExtensions", s => PackageManagerParsers.ParseVsCodeExtensions(s)),
        ("ParseGcloudComponents", s => PackageManagerParsers.ParseGcloudComponents(s)),
        ("ParseUvToolList", s => PackageManagerParsers.ParseUvToolList(s)),
        ("FirstVersionToken", s => PackageManagerParsers.FirstVersionToken(s)),
        ("IsNewer(a)", s => PackageManagerParsers.IsNewer(s, "1.0")),
        ("IsNewer(b)", s => PackageManagerParsers.IsNewer("1.0", s)),
        ("ParsePyPiRelease", s => PackageMetadataParsers.ParsePyPiRelease(s)),
        ("ParsePyPiLatest", s => PackageMetadataParsers.ParsePyPiLatest(s)),
        ("ParseNpmManifestVersion", s => PackageMetadataParsers.ParseNpmManifestVersion(s)),
        ("ParseNuGetPublished", s => PackageMetadataParsers.ParseNuGetPublished(s)),
        ("ParseChocolateyODataMeta", s => PackageMetadataParsers.ParseChocolateyODataMeta(s)),
        ("ParseAndroidSdkPackageIndex", s => PackageMetadataParsers.ParseAndroidSdkPackageIndex(s)),
        ("ParseVsMarketplaceExtension", s => PackageMetadataParsers.ParseVsMarketplaceExtension(s)),
        ("ParseGitHubLatestRelease", s => PackageMetadataParsers.ParseGitHubLatestRelease(s)),
        ("ParseRubyGemsVersionDate", s => PackageMetadataParsers.ParseRubyGemsVersionDate(s, "1.0.0")),
        ("ParsePackagistVersion", s => PackageMetadataParsers.ParsePackagistVersion(s, "vendor/pkg", "1.0.0")),
        ("ParseScoopManifestDownloadUrl", s => PackageMetadataParsers.ParseScoopManifestDownloadUrl(s, false)),
        ("ParseScoopManifestDownloadUrl arm", s => PackageMetadataParsers.ParseScoopManifestDownloadUrl(s, true)),
        ("ParseScoopManifestHomepage", s => PackageMetadataParsers.ParseScoopManifestHomepage(s)),
        ("ParseRustupChannelDate", s => PackageMetadataParsers.ParseRustupChannelDate(s)),
        ("RepoOwner", s => PackageMetadataParsers.RepoOwner(s)),
        ("EngineRangeAccepts", s => PackageMetadataParsers.EngineRangeAccepts(s, new Version(20, 0))),
        ("FlutterParsers.ParseInstalled", s => FlutterParsers.ParseInstalled(s)),
        ("FlutterParsers.ParseLatestRelease", s => FlutterParsers.ParseLatestRelease(s, "stable")),
        ("VoltaParsers.ParseDefaultTools", s => VoltaParsers.ParseDefaultTools(s)),
        ("VoltaParsers.ParseLatestNodeVersion", s => VoltaParsers.ParseLatestNodeVersion(s, "20")),
        ("VoltaParsers.ParseNodeReleaseDate", s => VoltaParsers.ParseNodeReleaseDate(s, "20.1.0")),
        ("OllamaParsers.ParseLocalModels", s => OllamaParsers.ParseLocalModels(s)),
        ("OllamaParsers.NormalizeDigest", s => OllamaParsers.NormalizeDigest(s)),
        ("PyenvWinParsers.ParseInstallableVersions", s => PyenvWinParsers.ParseInstallableVersions(s)),
        ("PyenvWinParsers.ParseGlobalVersion", s => PyenvWinParsers.ParseGlobalVersion(s)),
        ("SteamNewsParser.ParseFirstNewsItem", s => SteamNewsParser.ParseFirstNewsItem(s)),
        ("SteamNewsParser.StripMarkup", s => SteamNewsParser.StripMarkup(s)),
        ("SteamAcfParser.ParseAppManifest", s => SteamAcfParser.ParseAppManifest(s)),
        ("SteamAcfParser.ParseLibraryFolders", s => SteamAcfParser.ParseLibraryFolders(s)),
        ("WingetUpgradeParser.ParseModuleJson", s => WingetUpgradeParser.ParseModuleJson(s)),
        ("WingetUpgradeParser.ParseCliTable", s => WingetUpgradeParser.ParseCliTable(s)),
        ("WingetUpgradeParser.ParseListIdForName", s => WingetUpgradeParser.ParseListIdForName(s, "Git")),
        ("WingetUpgradeParser.ParseListVersion", s => WingetUpgradeParser.ParseListVersion(s, "Git.Git")),
        ("WingetUpgradeParser.ParseShowReleaseDate", s => WingetUpgradeParser.ParseShowReleaseDate(s)),
        ("WingetUpgradeParser.ParseShowReleaseNotes", s => WingetUpgradeParser.ParseShowReleaseNotes(s)),
        ("WingetUpgradeParser.ParseVersionsList", s => WingetUpgradeParser.ParseVersionsList(s)),
        ("GitHubReleaseParsers.ParseLatestRelease", s => GitHubReleaseParsers.ParseLatestRelease(s)),
        ("GitHubReleaseParsers.TryExtractOwnerRepo", s => GitHubReleaseParsers.TryExtractOwnerRepo(s)),
        ("GitLabReleaseParsers.ParseReleaseDescription", s => GitLabReleaseParsers.ParseReleaseDescription(s)),
        ("ChangelogUrlParsers.TryParseGitHubReleaseUrl", s => ChangelogUrlParsers.TryParseGitHubReleaseUrl(s)),
        ("ChangelogUrlParsers.TryParseGitLabReleaseUrl", s => ChangelogUrlParsers.TryParseGitLabReleaseUrl(s)),
        ("NpmChangelogLookup.ParseView", s => NpmChangelogLookup.ParseView(s)),
        ("NpmChangelogLookup.TryBuildGitHubReleasesUrl", s => NpmChangelogLookup.TryBuildGitHubReleasesUrl(s)),
        ("NodeEngineRange.ParseNodeEngine", s => NodeEngineRange.ParseNodeEngine(s)),
        ("NodeEngineRange.Satisfies(range)", s => NodeEngineRange.Satisfies(s, "20.1.0")),
        ("NodeEngineRange.Satisfies(version)", s => NodeEngineRange.Satisfies(">=18", s)),
        ("ComponentStoreAnalysisParser.Parse", s => ComponentStoreAnalysisParser.Parse(s)),
        ("DriverStoreFolderNameParser.ExtractOriginalName", s => DriverStoreFolderNameParser.ExtractOriginalName(s)),
        ("DriverPackageInfReader.Parse", s => DriverPackageInfReader.Parse(s)),
        ("ProgressParser.Parse", s => ProgressParser.Parse(s)),
        ("ProgressParser.IsInstallPhaseLine", s => ProgressParser.IsInstallPhaseLine(s)),
        ("ErrorDetailFormatter.Format", s => ErrorDetailFormatter.Format(s)),
    ];

    public static TheoryData<string> ParserNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Parsers)
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ParserNames))]
    public void A_parser_never_throws_on_hostile_input(string parserName)
    {
        var parse = Parsers.Single(p => p.Name == parserName).Parse;
        var index = 0;
        foreach (var input in HostileInputs())
        {
            var preview = input.Length > 40 ? input[..40] + "..." : input;
            var thrown = Record.Exception(() => parse(input));
            Assert.True(thrown is null,
                $"{parserName} threw {thrown?.GetType().Name} on hostile input #{index} '{preview}': {thrown?.Message}");
            index++;
        }
    }

    [Fact]
    public void Every_public_text_parser_in_the_main_parser_classes_is_in_the_corpus_list()
    {
        // A parser added to one of these classes without being listed above would escape the rule.
        var listed = string.Join('\n', Parsers.Select(p => p.Name));
        foreach (var type in new[] { typeof(PackageManagerParsers) })
        {
            var methods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(m => m.Name.StartsWith("Parse", StringComparison.Ordinal))
                .Select(m => m.Name)
                .Distinct();
            foreach (var method in methods)
                Assert.True(listed.Contains(method, StringComparison.Ordinal), $"{type.Name}.{method} is not in the robustness corpus");
        }
    }
}
