using System.IO;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// An update pip lists but cannot install on this interpreter (a dependency without a build for this
/// Python, or a resolver conflict) used to end as "installed, version unchanged": a plain upgrade makes pip
/// silently fall back to the old version. The install pins the offered version so the real reason shows,
/// the reason is classified, and the blocked version is remembered so the row stops coming back.
/// </summary>
public class PipBlockedUpdateTests : IDisposable
{
    private const string Python = @"C:\Python315\python.exe";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pip-blocked-" + Guid.NewGuid().ToString("N"));

    public PipBlockedUpdateTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string StorePath => Path.Combine(_directory, "pip-blocked.json");

    private readonly Clock _clock = new();

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    private PipBlockedStore NewStore(bool? writable = null) =>
        new(StorePath, new LoggingService(), writable, _clock, path => !_missing.Contains(path));

    private static UpdateItem Item(string package, string? available, string python = Python) => new()
    {
        Id = package, SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = package,
        InstalledVersion = "1.0", AvailableVersion = available,
        Meta = new Dictionary<string, string> { ["pipPackage"] = package, ["pythonPath"] = python },
    };

    private static OutdatedPackage Outdated(string id, string installed, string available) =>
        new(id, id, installed, available);

    // ---- the pin ----

    [Fact]
    public void The_install_pins_the_offered_version()
    {
        var args = PipSource.InstallArgumentsFor(Item("pymupdf", "2.0"));

        Assert.Equal(new[] { "-m", "pip", "install", "--upgrade", "--disable-pip-version-check", "pymupdf==2.0" }, args);
    }

    [Fact]
    public void The_user_scope_retry_pins_the_offered_version_too()
    {
        var denied = new ProcessResult(1, "", "ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied");

        var args = PipSource.RetryArgumentsFor(Item("pymupdf", "2.0"), denied);

        Assert.NotNull(args);
        Assert.Contains("--user", args!);
        Assert.Equal("pymupdf==2.0", args[^1]);
    }

    [Fact]
    public void Pip_upgrading_itself_pins_as_well()
    {
        Assert.Equal("pip==25.3", PipSource.InstallArgumentsFor(Item("pip", "25.3"))[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("2.0 --index-url=x")]
    [InlineData(">=2.0")]
    [InlineData("2.0,<3")]
    [InlineData("-2.0")]
    [InlineData("2.0;python_version>'3'")]
    [InlineData("1..2")]
    public void A_missing_or_unsafe_version_keeps_the_unpinned_form(string? version)
    {
        Assert.Equal("pymupdf", PipSource.InstallArgumentsFor(Item("pymupdf", version))[^1]);
    }

    [Theory]
    [InlineData("1.28.2")]
    [InlineData("2.0rc1")]
    [InlineData("1.0.post1")]
    [InlineData("2024.1.0+local")]
    [InlineData("1!2.0")]
    public void Real_pep440_versions_are_pinned(string version)
    {
        Assert.Equal($"pkg=={version}", PipSource.InstallArgumentsFor(Item("pkg", version))[^1]);
    }

    // ---- the classifier ----

    private const string MissingDependencyOutput =
        "ERROR: Could not find a version that satisfies the requirement onnxruntime (from pymupdf) (from versions: none)\n" +
        "ERROR: No matching distribution found for onnxruntime";

    [Fact]
    public void A_dependency_without_a_build_is_a_missing_dependency()
    {
        var failure = PipInstallFailure.Classify(MissingDependencyOutput, "pymupdf");

        Assert.Equal(PipFailureKind.MissingDependency, failure.Kind);
        Assert.Equal("onnxruntime", failure.Dependency);
    }

    [Theory]
    [InlineData("ERROR: Cannot install semgrep==1.182.1 because these package versions have conflicting dependencies.")]
    [InlineData("ERROR: ResolutionImpossible: for help visit https://pip.pypa.io/en/latest/topics/dependency-resolution/")]
    public void A_resolver_conflict_is_a_conflict(string output)
    {
        var failure = PipInstallFailure.Classify(output, "semgrep");

        Assert.Equal(PipFailureKind.Conflict, failure.Kind);
        Assert.Null(failure.Dependency);
    }

    [Fact]
    public void The_package_itself_missing_from_the_index_is_not_a_dependency_problem()
    {
        var output = "ERROR: Could not find a version that satisfies the requirement pymupdf==2.0 (from versions: none)\n" +
                     "ERROR: No matching distribution found for pymupdf==2.0";

        Assert.Equal(PipFailureKind.None, PipInstallFailure.Classify(output, "pymupdf").Kind);
    }

    /// <summary>Offline, pip prints the same "no matching distribution" line; that is not a permanent block.</summary>
    [Theory]
    [InlineData("WARNING: Retrying (Retry(total=4, connect=None)) after connection broken by 'NewConnectionError(...)': /simple/onnxruntime/")]
    [InlineData("WARNING: Retrying (Retry(total=0)) after connection broken by 'ProxyError(...)'")]
    [InlineData("ERROR: Could not fetch URL https://pypi.org/simple/onnxruntime/: There was a problem confirming the ssl certificate")]
    [InlineData("pip._vendor.urllib3.exceptions.ReadTimeoutError: HTTPSConnectionPool(host='pypi.org', port=443): Read timed out.")]
    public void A_network_problem_is_never_classified(string networkLine)
    {
        var failure = PipInstallFailure.Classify(networkLine + "\n" + MissingDependencyOutput, "pymupdf");

        Assert.Equal(PipFailureKind.None, failure.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied")]
    public void Unrelated_output_is_not_classified(string? output)
    {
        Assert.Equal(PipFailureKind.None, PipInstallFailure.Classify(output, "x").Kind);
    }

    [Fact]
    public void A_dependency_name_with_markers_is_cut_to_the_bare_name()
    {
        var failure = PipInstallFailure.Classify("ERROR: No matching distribution found for onnxruntime>=1.20", "pymupdf");

        Assert.Equal("onnxruntime", failure.Dependency);
    }

    // ---- the message and the explanation ----

    [Fact]
    public void The_missing_dependency_message_round_trips_into_a_localised_explanation()
    {
        var message = PipInstallFailure.BuildMessage(
            "pymupdf", "2.0", new PipInstallFailure(PipFailureKind.MissingDependency, "onnxruntime"))
            + " ERROR: No matching distribution found for onnxruntime (exit code 1)";

        var explanation = InstallErrorExplainer.Explain("pip", message);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorPipMissingDependencyWhat", explanation!.WhatHappenedKey);
        Assert.Equal(new[] { "pymupdf", "2.0", "onnxruntime" }, explanation.WhatHappenedArgs.Select(a => (string)a));
        Assert.Equal("InstallErrorPipBlockedNow", explanation.WhatNowKey);
        Assert.False(explanation.AllowsInteractiveRetry);
        Assert.False(explanation.AllowsAutomaticRetry);
    }

    [Fact]
    public void The_conflict_message_round_trips_into_a_localised_explanation()
    {
        var message = PipInstallFailure.BuildMessage(
            "semgrep", "1.182.1", new PipInstallFailure(PipFailureKind.Conflict, null)) + " (exit code 1)";

        var explanation = InstallErrorExplainer.Explain("pip", message);

        Assert.NotNull(explanation);
        Assert.Equal("InstallErrorPipConflictWhat", explanation!.WhatHappenedKey);
        Assert.Equal(new[] { "semgrep", "1.182.1" }, explanation.WhatHappenedArgs.Select(a => (string)a));
    }

    [Fact]
    public void Another_source_with_the_same_wording_is_not_explained_this_way()
    {
        var message = PipInstallFailure.BuildMessage(
            "x", "1", new PipInstallFailure(PipFailureKind.Conflict, null));

        Assert.NotEqual("InstallErrorPipConflictWhat", InstallErrorExplainer.Explain("npm", message)?.WhatHappenedKey);
    }

    [Fact]
    public void The_explanation_text_names_package_version_and_dependency()
    {
        var message = PipInstallFailure.BuildMessage(
            "pymupdf", "2.0", new PipInstallFailure(PipFailureKind.MissingDependency, "onnxruntime"));

        var text = InstallErrorExplainer.Describe("pip", message, key => LocalizationService.Instance[key]);

        Assert.Contains("pymupdf 2.0", text);
        Assert.Contains("onnxruntime", text);
    }

    // ---- the store ----

    [Fact]
    public void A_blocked_version_survives_a_round_trip()
    {
        NewStore().Add(Python, "pymupdf", "2.0");

        var loaded = NewStore().Load();

        var entry = Assert.Single(loaded);
        Assert.Equal((Python, "pymupdf", "2.0"), (entry.Interpreter, entry.Package, entry.Version));
        Assert.Contains("\"version\": 2", File.ReadAllText(StorePath).Replace("\r", ""));
    }

    [Fact]
    public void Adding_the_same_entry_twice_keeps_one()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");
        store.Add(Python.ToUpperInvariant(), "PyMuPDF", "2.0");

        Assert.Single(store.Load());
    }

    [Fact]
    public void A_file_from_a_newer_format_is_rejected_cleanly()
    {
        File.WriteAllText(StorePath, "{\"version\": 99, \"blocked\": [{\"interpreter\":\"p\",\"package\":\"a\",\"version\":\"1\"}]}");

        Assert.Empty(NewStore().Load());
    }

    [Theory]
    [InlineData("{\"blocked\": [{\"interpreter\":\"p\",\"package\":\"a\",\"version\":\"1\"}]}")]
    [InlineData("{\"version\": 0, \"blocked\": []}")]
    public void A_file_without_a_usable_version_is_rejected(string json)
    {
        File.WriteAllText(StorePath, json);

        Assert.Empty(NewStore().Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"version\": 1, \"blocked\": 5}")]
    [InlineData("[1,2,3]")]
    public void A_corrupt_file_is_an_empty_set_and_never_a_crash(string content)
    {
        File.WriteAllText(StorePath, content);

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void A_missing_file_is_an_empty_set()
    {
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void An_entry_with_a_missing_field_is_dropped_and_the_rest_kept()
    {
        File.WriteAllText(StorePath,
            "{\"version\": 2, \"blocked\": [{\"interpreter\":\"p\",\"package\":\"a\",\"blockedAt\":\"2026-06-01T11:00:00Z\"}, " +
            "{\"interpreter\":\"p\",\"package\":\"b\",\"version\":\"2\",\"blockedAt\":\"2026-06-01T11:00:00Z\"}]}");

        var entry = Assert.Single(NewStore().Load());
        Assert.Equal("b", entry.Package);
    }

    [Fact]
    public void The_elevated_worker_never_writes_the_store()
    {
        var store = NewStore(writable: false);

        store.Add(Python, "pymupdf", "2.0");

        Assert.False(File.Exists(StorePath));
    }

    // ---- the scan filter ----

    [Fact]
    public void A_blocked_version_is_dropped_from_the_scan()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        var (visible, hidden) = store.Apply(Python, [Outdated("PyMuPDF", "1.28.2", "2.0"), Outdated("requests", "2.0", "2.1")]);

        Assert.Equal(new[] { "requests" }, visible.Select(p => p.Id));
        Assert.Equal(new[] { "PyMuPDF" }, hidden.Select(p => p.Id));
    }

    [Fact]
    public void A_newer_offered_version_shows_again_and_forgets_the_entry()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        var (visible, hidden) = store.Apply(Python, [Outdated("pymupdf", "1.28.2", "2.1")]);

        Assert.Single(visible);
        Assert.Empty(hidden);
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void A_block_on_one_interpreter_does_not_hide_the_same_package_on_another()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        var (visible, hidden) = store.Apply(@"C:\Python313\python.exe", [Outdated("pymupdf", "1.28.2", "2.0")]);

        Assert.Single(visible);
        Assert.Empty(hidden);
        Assert.Single(NewStore().Load());
    }

    [Fact]
    public void An_entry_whose_package_is_not_outdated_is_kept()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        store.Apply(Python, [Outdated("requests", "2.0", "2.1")]);

        Assert.Single(NewStore().Load());
    }

    // ---- size ----

    [Fact]
    public void A_full_store_keeps_the_newest_entries_and_stays_readable()
    {
        var store = NewStore();
        var python = @"C:\Users\someone\AppData\Local\Programs\Python\Python313\" + new string('x', 100) + @"\python.exe";
        for (var i = 0; i < 600; i++)
            store.Add(python, $"package-{i}", "1.0." + i);

        var loaded = NewStore().Load();

        Assert.Equal(500, loaded.Count);
        Assert.Equal("package-100", loaded[0].Package);
        Assert.Equal("package-599", loaded[^1].Package);
        Assert.True(new FileInfo(StorePath).Length < 256 * 1024);
    }

    [Fact]
    public void An_entry_for_a_missing_interpreter_is_pruned_by_apply()
    {
        const string gone = @"C:\Python311\python.exe";
        var store = NewStore();
        store.Add(gone, "pymupdf", "2.0");
        store.Add(Python, "requests", "2.1");
        _missing.Add(gone);

        var (visible, hidden) = store.Apply(Python, [Outdated("requests", "2.0", "2.1")]);

        Assert.Empty(visible);
        Assert.Single(hidden);
        Assert.Equal(new[] { "requests" }, NewStore().Load().Select(e => e.Package));
    }

    [Fact]
    public void Pruning_the_last_entry_still_saves_the_empty_file()
    {
        const string gone = @"C:\Python311\python.exe";
        var store = NewStore();
        store.Add(gone, "pymupdf", "2.0");
        _missing.Add(gone);

        var (visible, _) = store.Apply(gone, [Outdated("pymupdf", "1.0", "2.0")]);

        Assert.Single(visible);
        Assert.Empty(NewStore().Load());
    }

    // ---- expiry ----

    [Fact]
    public void An_entry_older_than_seven_days_no_longer_hides_its_version()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        _clock.Now += TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1);
        var (visible, hidden) = store.Apply(Python, [Outdated("pymupdf", "1.28.2", "2.0")]);

        Assert.Single(visible);
        Assert.Empty(hidden);
    }

    [Fact]
    public void A_fresh_entry_still_hides_its_version()
    {
        var store = NewStore();
        store.Add(Python, "pymupdf", "2.0");

        _clock.Now += TimeSpan.FromDays(6);
        var (visible, hidden) = store.Apply(Python, [Outdated("pymupdf", "1.28.2", "2.0")]);

        Assert.Empty(visible);
        Assert.Single(hidden);
    }

    [Fact]
    public void An_expired_entry_is_dropped_on_the_next_write()
    {
        var store = NewStore();
        store.Add(Python, "old", "1.0");
        _clock.Now += TimeSpan.FromDays(8);

        store.Add(Python, "new", "2.0");

        Assert.Equal(new[] { "new" }, NewStore().Load().Select(e => e.Package));
        Assert.DoesNotContain("\"old\"", File.ReadAllText(StorePath));
    }

    [Fact]
    public void An_entry_without_a_timestamp_counts_as_expired()
    {
        File.WriteAllText(StorePath, "{\"version\": 2, \"blocked\": [{\"interpreter\":\"p\",\"package\":\"a\",\"version\":\"1\"}]}");

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void A_version_1_file_is_read_and_its_entries_count_as_expired()
    {
        File.WriteAllText(StorePath, "{\"version\": 1, \"blocked\": [{\"interpreter\":\"p\",\"package\":\"a\",\"version\":\"1\"}]}");

        var (visible, hidden) = NewStore().Apply("p", [Outdated("a", "0.9", "1")]);

        Assert.Single(visible);
        Assert.Empty(hidden);
    }
}
