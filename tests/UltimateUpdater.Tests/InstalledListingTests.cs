using System.Reflection;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>
/// The installed-package listing of the package manager sources: the parsers (from recorded output, never throwing on
/// bad input), the fail-closed run check, the shape of the rows, and the duplicate rule against registry programs.
/// </summary>
public class InstalledListingTests
{
    private static readonly string Registered = char.ConvertFromUtf32(0xAE);
    private static readonly string Trademark = char.ConvertFromUtf32(0x2122);
    private static readonly string Copyright = char.ConvertFromUtf32(0xA9);
    private static readonly string NonBreakingSpace = char.ConvertFromUtf32(0xA0);

    private static CliUpdateSource Source(string id) =>
        Assert.IsAssignableFrom<CliUpdateSource>(SourceRegistry.CreateAll(new LoggingService()).Single(s => s.Id == id));

    private static (string Id, string Version) Pair(InstalledPackage p) => (p.Id, p.Version ?? "?");

    // ---- parsers ----

    [Fact]
    public void Chocolatey_listing_reads_id_and_version_and_skips_banner_lines()
    {
        var packages = InstalledListParsers.ParseChocolatey("Chocolatey v2.2.2\ngit|2.44.0\n7zip|23.1.0\nnot a package line\n");

        Assert.Equal([("git", "2.44.0"), ("7zip", "23.1.0")], packages.Select(Pair));
        Assert.All(packages, p => Assert.Null(p.RequiredByOthers));
    }

    [Fact]
    public void Scoop_export_reads_the_json_document_and_the_older_text_lines()
    {
        var json = InstalledListParsers.ParseScoopExport("{\"apps\":[{\"Name\":\"git\",\"Version\":\"2.44.0.windows.1\",\"Source\":\"main\"},{\"Name\":\"jq\",\"Version\":\"1.7.1\"}]}");
        var text = InstalledListParsers.ParseScoopExport("git (2.44.0.windows.1) [main]\njq (1.7.1) *global*\n");

        Assert.Equal([("git", "2.44.0.windows.1"), ("jq", "1.7.1")], json.Select(Pair));
        Assert.Equal([("git", "2.44.0.windows.1"), ("jq", "1.7.1")], text.Select(Pair));
    }

    [Fact]
    public void Pip_list_reads_every_package_of_the_interpreter()
    {
        var packages = InstalledListParsers.ParsePipList("[{\"name\":\"requests\",\"version\":\"2.31.0\"},{\"name\":\"urllib3\",\"version\":\"2.2.1\"}]");

        Assert.Equal([("requests", "2.31.0"), ("urllib3", "2.2.1")], packages.Select(Pair));
    }

    [Fact]
    public void Pip_required_flag_comes_from_the_not_required_listing_with_pip_name_equality()
    {
        var all = InstalledListParsers.ParsePipList(
            "[{\"name\":\"Requests\",\"version\":\"2.31.0\"},{\"name\":\"urllib3\",\"version\":\"2.2.1\"},{\"name\":\"typing_extensions\",\"version\":\"4.0\"}]");
        var free = InstalledListParsers.ParsePipList("[{\"name\":\"requests\",\"version\":\"2.31.0\"},{\"name\":\"typing-extensions\",\"version\":\"4.0\"}]");

        var marked = InstalledListParsers.MarkPipRequired(all, free);

        Assert.Equal([false, true, false], marked.Select(p => p.RequiredByOthers!.Value));
    }

    [Fact]
    public void Pip_required_flag_stays_unknown_when_the_not_required_call_gave_no_answer()
    {
        var all = InstalledListParsers.ParsePipList("[{\"name\":\"requests\",\"version\":\"2.31.0\"}]");

        var marked = InstalledListParsers.MarkPipRequired(all, null);

        Assert.Null(Assert.Single(marked).RequiredByOthers);
    }

    [Fact]
    public void Npm_listing_reads_the_global_packages_and_says_no_other_package_holds_them()
    {
        const string report = "{\"name\":\"lib\",\"dependencies\":{\"typescript\":{\"version\":\"5.4.5\"},\"npm\":{\"version\":\"10.5.0\",\"overridden\":false}}}";

        var packages = InstalledListParsers.ParseNpmList(report);

        Assert.Equal([("typescript", "5.4.5"), ("npm", "10.5.0")], packages.Select(Pair));
        Assert.All(packages, p => Assert.False(p.RequiredByOthers));
    }

    [Fact]
    public void Pnpm_listing_reads_the_project_array_and_leaves_the_flag_unknown()
    {
        var packages = InstalledListParsers.ParsePnpmList("[{\"path\":\"x\",\"dependencies\":{\"typescript\":{\"version\":\"5.4.5\"}}}]");

        var only = Assert.Single(packages);
        Assert.Equal(("typescript", "5.4.5"), Pair(only));
        Assert.Null(only.RequiredByOthers);
    }

    [Fact]
    public void Gh_extension_listing_returns_every_extension_not_only_the_ones_with_an_update()
    {
        var packages = InstalledListParsers.ParseGhExtensions("gh copilot\tgithub/gh-copilot\tv1.0.5\n*\tgh dash\tdlvhdr/gh-dash\tv4.2.0\n");

        Assert.Equal([("github/gh-copilot", "v1.0.5"), ("dlvhdr/gh-dash", "v4.2.0")], packages.Select(Pair));
    }

    [Fact]
    public void Gem_listing_skips_default_only_gems_and_takes_the_newest_removable_version()
    {
        var packages = InstalledListParsers.ParseGemList(
            "*** LOCAL GEMS ***\n\nbundler (default: 2.5.6)\nrake (13.1.0, 12.3.3)\njson (default: 2.7.1, 2.6.0)\nnokogiri (1.16.2 x64-mingw-ucrt)\n");

        Assert.Equal([("rake", "13.1.0"), ("json", "2.6.0"), ("nokogiri", "1.16.2")], packages.Select(Pair));
    }

    [Fact]
    public void Composer_listing_reads_the_installed_array()
    {
        var packages = InstalledListParsers.ParseComposerShow("{\"installed\":[{\"name\":\"laravel/installer\",\"version\":\"v5.3.0\"},{\"name\":\"psy/psysh\",\"version\":\"v0.12.3\"}]}");

        Assert.Equal([("laravel/installer", "v5.3.0"), ("psy/psysh", "v0.12.3")], packages.Select(Pair));
    }

    [Fact]
    public void Vcpkg_listing_keys_a_port_by_triplet_and_skips_feature_lines()
    {
        var packages = InstalledListParsers.ParseVcpkgList(
            "zlib:x64-windows                                  1.3.1            A compression library\nboost-asio[ssl]:x64-windows                       1.84.0\nfmt:x64-windows 10.2.1\n");

        Assert.Equal([("zlib:x64-windows", "1.3.1"), ("fmt:x64-windows", "10.2.1")], packages.Select(Pair));
        Assert.Equal(["zlib", "fmt"], packages.Select(p => p.Name));
    }

    [Fact]
    public void Mise_listing_takes_the_active_installed_version_and_skips_tools_only_requested()
    {
        const string json = "{\"node\":[{\"version\":\"20.11.0\",\"installed\":true,\"active\":false},{\"version\":\"22.1.0\",\"installed\":true,\"active\":true}],"
                          + "\"python\":[{\"version\":\"3.12\",\"installed\":false,\"active\":true}]}";

        var packages = InstalledListParsers.ParseMiseList(json);

        Assert.Equal([("node", "22.1.0")], packages.Select(Pair));
    }

    [Fact]
    public void Sdkmanager_listing_reads_only_the_installed_section_in_both_layouts()
    {
        const string pipes = "Installed packages:\n  Path                 | Version | Description         | Location\n  -------              | ------- | -------             | -------\n  platform-tools       | 35.0.1  | Platform-Tools      | platform-tools\n  platforms;android-34 | 3       | Android SDK 34      | platforms\\android-34\n\nAvailable Packages:\n  Path                 | Version | Description\n  build-tools;35.0.0   | 35.0.0  | Build-Tools 35\n";
        const string spaces = "Installed packages:\n  platform-tools   35.0.1   Android SDK Platform-Tools\n\nAvailable packages:\n  build-tools;35.0.0   35.0.0   Build-Tools\n";

        Assert.Equal([("platform-tools", "35.0.1"), ("platforms;android-34", "3")], InstalledListParsers.ParseSdkManagerInstalled(pipes).Select(Pair));
        Assert.Equal([("platform-tools", "35.0.1")], InstalledListParsers.ParseSdkManagerInstalled(spaces).Select(Pair));
    }

    [Fact]
    public void Gcloud_listing_takes_the_components_that_have_a_current_version()
    {
        const string json = "Updates are available.\n[{\"id\":\"core\",\"name\":\"Cloud SDK Core Libraries\",\"current_version_string\":\"2024.04.19\"},"
                          + "{\"id\":\"bq\",\"name\":\"BigQuery\",\"current_version_string\":null},{\"id\":\"gsutil\",\"current_version_string\":\"5.27\"}]";

        var packages = InstalledListParsers.ParseGcloudInstalled(json);

        Assert.Equal([("core", "2024.04.19"), ("gsutil", "5.27")], packages.Select(Pair));
        Assert.Equal(["Cloud SDK Core Libraries", "gsutil"], packages.Select(p => p.Name));
    }

    [Fact]
    public void Pyenv_listing_takes_the_versions_and_never_the_system_entry()
    {
        var versions = InstalledListParsers.ParsePyenvVersions("  system\n* 3.12.2 (set by C:\\Users\\x\\.pyenv\\pyenv-win\\version)\n  3.11.9\n");

        Assert.Equal(["3.12.2", "3.11.9"], versions);
    }

    [Fact]
    public void Every_parser_survives_garbage_and_null_without_throwing_or_inventing_rows()
    {
        string?[] garbage = [null, "", "   ", "{", "[1,2,3]", "\"text\"", "null", "{\"apps\":5}", "{\"dependencies\":[]}", "\0\0\0", "|||", "\t\t\n"];
        Func<string?, object>[] parsers =
        [
            InstalledListParsers.ParseChocolatey, InstalledListParsers.ParseScoopExport, InstalledListParsers.ParsePipList,
            InstalledListParsers.ParseNpmList, InstalledListParsers.ParsePnpmList, InstalledListParsers.ParseGhExtensions,
            InstalledListParsers.ParseGemList, InstalledListParsers.ParseComposerShow, InstalledListParsers.ParseVcpkgList,
            InstalledListParsers.ParseMiseList, InstalledListParsers.ParseSdkManagerInstalled, InstalledListParsers.ParseGcloudInstalled,
            InstalledListParsers.ParsePyenvVersions,
        ];

        foreach (var parser in parsers)
        foreach (var input in garbage)
        {
            var result = parser(input);
            Assert.Empty((System.Collections.IEnumerable)result);
        }
    }

    [Theory]
    [InlineData("[{\"name\":\"\",\"version\":\"1\"},{\"version\":\"2\"},5,{\"name\":\"ok\"}]")]
    public void Pip_list_skips_entries_without_a_name_and_keeps_a_missing_version_unknown(string json)
    {
        var only = Assert.Single(InstalledListParsers.ParsePipList(json));

        Assert.Equal("ok", only.Id);
        Assert.Null(only.Version);
    }

    // ---- per-source mapping ----

    [Theory]
    [InlineData("pipx", "{\"venvs\":{\"black\":{\"metadata\":{\"main_package\":{\"package\":\"black\",\"package_version\":\"24.3.0\"}}}}}", "black", "24.3.0")]
    [InlineData("vscode-extension", "ms-python.python@2024.4.0\n", "ms-python.python", "2024.4.0")]
    [InlineData("uv-tool", "ruff v0.4.1\n- ruff\n", "ruff", "0.4.1")]
    public void Wrapped_scan_parsers_give_installed_rows_without_an_available_version(string sourceId, string output, string id, string version)
    {
        var packages = Source(sourceId).ParseInstalledList(output);

        Assert.Equal((id, version), Pair(Assert.Single(packages)));
    }

    [Fact]
    public void Installed_rows_are_shaped_like_the_scan_rows_and_accepted_by_the_uninstall_guard()
    {
        var npm = Source("npm");
        var items = npm.ToInstalledItems(npm.ParseInstalledList("{\"dependencies\":{\"typescript\":{\"version\":\"5.4.5\"}}}"));

        var item = Assert.Single(items);
        Assert.Equal("npm", item.SourceId);
        Assert.Equal("typescript", item.Id);
        Assert.Equal("5.4.5", item.InstalledVersion);
        Assert.False(item.RequiredByOthers);
        Assert.True(npm.CanUninstall(item));
    }

    [Fact]
    public void An_unknown_dependent_state_is_null_not_false()
    {
        var pnpm = Source("pnpm");
        var item = Assert.Single(pnpm.ToInstalledItems(pnpm.ParseInstalledList("[{\"dependencies\":{\"typescript\":{\"version\":\"5.4.5\"}}}]")));

        Assert.Null(item.RequiredByOthers);
    }

    [Fact]
    public void The_required_flag_survives_the_item_copies()
    {
        var item = new UpdateItem { Id = "a", SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = "a", RequiredByOthers = true };

        Assert.True(item.WithInstallScope("user").RequiredByOthers);
        Assert.True(item.WithPinnedVersion("1.0").RequiredByOthers);
        Assert.Null(new UpdateItem { Id = "a", SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = "a" }.WithPinnedVersion("1.0").RequiredByOthers);
    }

    // ---- fail closed ----

    [Fact]
    public void A_listing_that_timed_out_crashed_or_failed_throws_instead_of_reading_as_empty()
    {
        var gem = Source("gem");

        Assert.Throws<TimeoutException>(() => gem.EnsureListCompleted(new ProcessResult(0, "", "") { TimedOut = true }));
        Assert.Throws<ExternalToolCrashedException>(() => gem.EnsureListCompleted(new ProcessResult(unchecked((int)0xC0000005), "", "")));
        Assert.Throws<InvalidOperationException>(() => gem.EnsureListCompleted(new ProcessResult(2, "*** LOCAL GEMS ***", "boom")));
        gem.EnsureListCompleted(new ProcessResult(0, "", ""));
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pnpm")]
    public void Npm_style_listing_accepts_exit_one_only_with_a_report_and_never_with_an_error_or_nothing(string id)
    {
        var source = Source(id);

        source.EnsureListCompleted(new ProcessResult(1, "{\"dependencies\":{}}", "npm error missing peer"));
        Assert.Throws<InvalidOperationException>(() => source.EnsureListCompleted(new ProcessResult(1, "{\"error\":{\"code\":\"EACCES\"}}", "")));
        Assert.Throws<InvalidOperationException>(() => source.EnsureListCompleted(new ProcessResult(1, "", "boom")));
        Assert.Throws<InvalidOperationException>(() => source.EnsureListCompleted(new ProcessResult(1, "not json", "")));
        Assert.Throws<InvalidOperationException>(() => source.EnsureListCompleted(new ProcessResult(2, "{\"dependencies\":{}}", "")));
    }

    [Fact]
    public void Only_npm_and_pnpm_vouch_for_a_non_zero_listing()
    {
        var failing = new ProcessResult(1, "{\"dependencies\":{}}", "");

        foreach (var id in ExpectedIds())
        {
            var vouches = Source(id).IsCompleteInstalledListing(failing);
            Assert.Equal(id is "npm" or "pnpm", vouches);
        }

        static IEnumerable<string> ExpectedIds() =>
            UninstallCommandTests.ExpectedCanUninstall.Where(kv => kv.Value && kv.Key is not ("winget" or "steam"))
                .Select(kv => kv.Key)
                .Where(id => SourceRegistry.CreateAll(new LoggingService()).Single(s => s.Id == id) is CliUpdateSource);
    }

    // ---- coverage ----

    [Fact]
    public void Every_source_that_offers_removal_lists_its_installed_packages_except_winget_and_steam()
    {
        var sources = SourceRegistry.CreateAll(new LoggingService()).ToDictionary(s => s.Id);
        var missing = new List<string>();

        foreach (var (id, canUninstall) in UninstallCommandTests.ExpectedCanUninstall)
        {
            if (!canUninstall || id is "winget" or "steam")
                continue;

            var type = sources[id].GetType();
            var ownsListing = type.GetMethod(nameof(IUpdateSource.ListInstalledAsync), BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null;
            var namesCommand = sources[id] is CliUpdateSource cli && cli.ListInstalledArguments is not null;
            if (!ownsListing && !namesCommand)
                missing.Add(id);
        }

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Sources_without_removal_keep_the_empty_default_listing()
    {
        foreach (var source in SourceRegistry.CreateAll(new LoggingService()).Where(s => UninstallCommandTests.ExpectedCanUninstall.TryGetValue(s.Id, out var can) && !can))
        {
            var listed = source.ListInstalledAsync(CancellationToken.None);

            Assert.True(listed.IsCompletedSuccessfully, source.Id);
            Assert.Empty(await listed);
        }
    }

    // ---- duplicate rule ----

    [Theory]
    [InlineData("Git", null, "git", null, true)]
    [InlineData("Git", "The Git Development Community", "Git", null, true)]
    [InlineData("Git", null, "Git", "The Git Development Community", true)]
    [InlineData("Git", "Acme", "Git", "ACME", true)]
    [InlineData("Git", "Acme", "Git", "Other Corp", false)]
    [InlineData("Git", "Acme", "Git Extras", "Acme", false)]
    [InlineData("", null, "", null, false)]
    [InlineData(null, null, "Git", null, false)]
    [InlineData("  Git   Bash ", null, "git bash", null, true)]
    public void Duplicate_rule_is_equal_names_and_equal_publishers_when_both_are_known(string? nameA, string? publisherA, string? nameB, string? publisherB, bool expected)
    {
        Assert.Equal(expected, InstalledPackageDuplicates.IsDuplicate(nameA, publisherA, nameB, publisherB));
    }

    [Fact]
    public void Duplicate_rule_ignores_marks_non_breaking_spaces_and_compatibility_forms()
    {
        Assert.True(InstalledPackageDuplicates.IsDuplicate($"Acme{Registered} Studio{Trademark}", null, "Acme Studio", null));
        Assert.True(InstalledPackageDuplicates.IsDuplicate($"Acme (R) Studio (TM)", null, "Acme Studio", null));
        Assert.True(InstalledPackageDuplicates.IsDuplicate($"Acme{NonBreakingSpace}Studio", null, "acme studio", null));
        Assert.True(InstalledPackageDuplicates.IsDuplicate("Acme Studio", $"Acme Inc {Copyright}", "Acme Studio", "acme inc"));
        Assert.True(InstalledPackageDuplicates.IsDuplicate(char.ConvertFromUtf32(0xFF21) + "cme", null, "Acme", null));
    }
}
