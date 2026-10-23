using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The two rows the cleanup dialog builds on top of <see cref="PythonInterpreters"/>'s own discovery and
/// uninstall-command logic: which "~"-prefixed folders are offered, and which interpreters are offered
/// with which uninstall command. Driven entirely from fabricated interpreter sets, fabricated
/// site-packages trees under a real temp folder, and a fabricated PEP 514/Uninstall registry, the same
/// discipline <see cref="DriverCleanupDiscoveryTests"/> already applies to its own category.
/// </summary>
public class PythonCleanupDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-python-cleanup-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A registry made of nothing but the keys a test puts in it, the same fake
    /// <see cref="PythonInterpretersTests"/> already uses for PEP 514 discovery and uninstall lookups.</summary>
    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _values =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _subKeys =
            new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry AddTag(string rootPath, string tag, string executablePath)
        {
            if (!_subKeys.TryGetValue(rootPath, out var tags))
                _subKeys[rootPath] = tags = [];
            tags.Add(tag);

            var installPathKey = $@"{rootPath}\{tag}\InstallPath";
            Set(installPathKey, "ExecutablePath", executablePath);
            return this;
        }

        public FakeRegistry AddUninstallEntry(
            string root, string key, string displayName, string uninstallString, string? quietUninstallString = null)
        {
            if (!_subKeys.TryGetValue(root, out var keys))
                _subKeys[root] = keys = [];
            keys.Add(key);

            var keyPath = $@"{root}\{key}";
            Set(keyPath, "DisplayName", displayName);
            Set(keyPath, "UninstallString", uninstallString);
            if (quietUninstallString is not null)
                Set(keyPath, "QuietUninstallString", quietUninstallString);
            return this;
        }

        private void Set(string keyPath, string valueName, string value)
        {
            if (!_values.TryGetValue(keyPath, out var values))
                _values[keyPath] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            values[valueName] = value;
        }

        public IReadOnlyList<string> SubKeyNames(string keyPath) =>
            _subKeys.TryGetValue(keyPath, out var tags) ? tags : [];

        public string? Value(string keyPath, string valueName) =>
            _values.TryGetValue(keyPath, out var values) ? values.GetValueOrDefault(valueName) : null;
    }

    private const string HkcuRoot = @"HKCU\SOFTWARE\Python\PythonCore";
    private const string HkcuUninstallRoot = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string HklmRoot = @"HKLM\SOFTWARE\Python\PythonCore";
    private const string HklmUninstallRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>A real, on-disk interpreter folder: python.exe exists (Discover's public overload checks
    /// File.Exists), plus Lib\site-packages underneath it a test can populate freely.</summary>
    private string CreateInterpreter(string label)
    {
        var interpreterRoot = Path.Combine(_root, label);
        var sitePackages = Path.Combine(interpreterRoot, "Lib", "site-packages");
        Directory.CreateDirectory(sitePackages);
        File.WriteAllText(Path.Combine(interpreterRoot, "python.exe"), "not a real binary");
        return interpreterRoot;
    }

    [Fact]
    public void Only_the_tilde_prefixed_folder_is_offered_never_a_real_package()
    {
        var interpreterRoot = CreateInterpreter("Python313");
        var sitePackages = Path.Combine(interpreterRoot, "Lib", "site-packages");
        var broken = Path.Combine(sitePackages, "~orchvision");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "large.bin"), new string('x', 500));
        Directory.CreateDirectory(Path.Combine(sitePackages, "requests"));

        var registry = new FakeRegistry().AddTag(HkcuRoot, "3.13", Path.Combine(interpreterRoot, "python.exe"));

        var found = PythonCleanupDiscovery.DiscoverPipLeftovers(registry);

        Assert.Single(found);
        Assert.Equal(broken, found[0].Path);
        Assert.Equal(500, found[0].SizeBytes);
    }

    [Fact]
    public void Discovery_without_measuring_finds_the_same_folders_with_no_size()
    {
        var interpreterRoot = CreateInterpreter("Python313");
        var broken = Path.Combine(interpreterRoot, "Lib", "site-packages", "~orchvision");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "large.bin"), new string('x', 500));

        var registry = new FakeRegistry().AddTag(HkcuRoot, "3.13", Path.Combine(interpreterRoot, "python.exe"));

        var found = PythonCleanupDiscovery.DiscoverPipLeftovers(registry, measure: false);

        Assert.Single(found);
        Assert.Equal(broken, found[0].Path);
        Assert.Equal(0, found[0].SizeBytes);
    }

    [Fact]
    public void Leftovers_are_aggregated_across_every_registered_interpreter()
    {
        var first = CreateInterpreter("Python312");
        var second = CreateInterpreter("Python313");
        Directory.CreateDirectory(Path.Combine(first, "Lib", "site-packages", "~pip"));
        Directory.CreateDirectory(Path.Combine(second, "Lib", "site-packages", "~numpy"));

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", Path.Combine(first, "python.exe"))
            .AddTag(HkcuRoot, "3.13", Path.Combine(second, "python.exe"));

        var found = PythonCleanupDiscovery.DiscoverPipLeftovers(registry);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void The_only_interpreter_on_the_machine_is_never_offered_as_older()
    {
        var interpreterRoot = CreateInterpreter("Python313");
        var registry = new FakeRegistry().AddTag(HkcuRoot, "3.13", Path.Combine(interpreterRoot, "python.exe"));

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null);

        Assert.Empty(found);
    }

    [Fact]
    public void The_older_of_two_interpreters_is_offered_with_its_uninstall_command_and_package_count()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var sitePackages = Path.Combine(older, "Lib", "site-packages");
        Directory.CreateDirectory(Path.Combine(sitePackages, "requests-2.31.0.dist-info"));
        Directory.CreateDirectory(Path.Combine(sitePackages, "urllib3-2.0.0.dist-info"));
        Directory.CreateDirectory(Path.Combine(sitePackages, "requests")); // the package's own code, not counted

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", Path.Combine(older, "python.exe"))
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
                uninstallString: Path.Combine(older, "uninstall.exe"),
                quietUninstallString: Path.Combine(older, "uninstall.exe") + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null);

        var candidate = Assert.Single(found);
        Assert.Equal(Path.Combine(older, "python.exe"), candidate.PythonExe);
        Assert.Equal(2, candidate.PackageCount);
        Assert.Equal(Path.Combine(older, "uninstall.exe") + " /quiet", candidate.UninstallCommand);
    }

    [Fact]
    public void An_older_interpreter_with_no_registered_uninstaller_is_never_offered()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", Path.Combine(older, "python.exe"))
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"));

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null);

        Assert.Empty(found);
    }

    [Fact]
    public void The_interpreter_path_itself_resolves_python_to_is_never_offered_even_when_older()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var olderExe = Path.Combine(older, "python.exe");

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", olderExe)
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
                uninstallString: olderExe, quietUninstallString: olderExe + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, name => name == "python" ? olderExe : null);

        Assert.Empty(found);
    }

    [Fact]
    public void The_interpreter_path_resolves_pip_to_via_its_scripts_folder_is_never_offered()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var olderExe = Path.Combine(older, "python.exe");
        var pipOnPath = Path.Combine(older, "Scripts", "pip.exe");

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", olderExe)
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
                uninstallString: olderExe, quietUninstallString: olderExe + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(
            registry, name => name == "pip" ? pipOnPath : null);

        Assert.Empty(found);
    }

    [Fact]
    public void An_unquoted_uninstall_path_with_spaces_splits_at_the_exe_extension_not_at_the_first_space()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(@"C:\Program Files\Python312\uninstall.exe /quiet");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Program Files\Python312\uninstall.exe", split!.Value.Executable);
        Assert.Equal(["/quiet"], split.Value.Arguments);
    }

    [Fact]
    public void An_exe_inside_a_folder_name_does_not_end_the_unquoted_executable()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(@"C:\my.exes\uninstall.exe /quiet");

        Assert.NotNull(split);
        Assert.Equal(@"C:\my.exes\uninstall.exe", split!.Value.Executable);
        Assert.Equal(["/quiet"], split.Value.Arguments);
    }

    [Fact]
    public void An_unquoted_executable_at_the_very_end_of_the_command_has_no_arguments()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(@"C:\Program Files\Python312\uninstall.exe");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Program Files\Python312\uninstall.exe", split!.Value.Executable);
        Assert.Empty(split.Value.Arguments);
    }

    [Fact]
    public void A_command_whose_exe_text_never_ends_a_token_fails_to_split()
    {
        Assert.Null(PythonVersionCleanupExecutor.SplitCommand(@"C:\my.exes\uninstall /quiet"));
    }

    [Fact]
    public void A_quoted_uninstall_path_with_several_arguments_splits_cleanly()
    {
        var split = PythonVersionCleanupExecutor.SplitCommand(
            @"""C:\Program Files\Python312\uninstall.exe"" /uninstall /quiet");

        Assert.NotNull(split);
        Assert.Equal(@"C:\Program Files\Python312\uninstall.exe", split!.Value.Executable);
        Assert.Equal(["/uninstall", "/quiet"], split.Value.Arguments);
    }

    [Fact]
    public void A_command_with_no_recognisable_executable_fails_to_split_rather_than_guessing()
    {
        Assert.Null(PythonVersionCleanupExecutor.SplitCommand("not a real command"));
        Assert.Null(PythonVersionCleanupExecutor.SplitCommand(""));
    }

    /// <summary>
    /// The security property the elevated worker rests on: even a fabricated HKCU entry that looks exactly
    /// like a real older interpreter, with a real uninstall command, must never be handed to the elevated
    /// worker's own allow-list. A same-user attacker can write anything under HKCU, so if this ever came
    /// back true, that attacker's planted uninstall command would run with administrator rights.
    /// </summary>
    [Fact]
    public void A_fabricated_per_user_HKCU_entry_is_never_eligible_for_the_elevated_worker()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var olderExe = Path.Combine(older, "python.exe");

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.12", olderExe)
            .AddTag(HklmRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{attacker-planted}", "Python 3.12.5 (64-bit)",
                uninstallString: Path.Combine(older, "uninstall.exe"),
                quietUninstallString: Path.Combine(older, "uninstall.exe") + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null);

        var candidate = Assert.Single(found);
        Assert.True(candidate.PerUser);

        var eligibleForElevatedWorker = PythonVersionCleanupExecutor.CandidatesEligibleForElevatedWorker(found);

        Assert.Empty(eligibleForElevatedWorker);
    }

    /// <summary>The other half of the same split: a machine-wide (HKLM) entry is exactly what the elevated
    /// worker exists to act on, so it must still come through.</summary>
    [Fact]
    public void A_machine_wide_HKLM_entry_is_eligible_for_the_elevated_worker()
    {
        var older = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var olderExe = Path.Combine(older, "python.exe");

        var registry = new FakeRegistry()
            .AddTag(HklmRoot, "3.12", olderExe)
            .AddTag(HklmRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HklmUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
                uninstallString: Path.Combine(older, "uninstall.exe"),
                quietUninstallString: Path.Combine(older, "uninstall.exe") + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null);

        var candidate = Assert.Single(found);
        Assert.False(candidate.PerUser);

        var eligibleForElevatedWorker = PythonVersionCleanupExecutor.CandidatesEligibleForElevatedWorker(found);

        Assert.Single(eligibleForElevatedWorker);
    }
}
