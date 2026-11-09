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
        TempTreeCleanup.Delete(_root);
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

    /// <summary>Stamps a folder, everything under it and its site-packages folder two days old: a fresh
    /// "~" folder, or one in a site-packages folder written just now, is a running pip install and is never
    /// offered.</summary>
    private static void MakeStale(string folder)
    {
        var stamp = DateTime.UtcNow.AddDays(-2);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(entry, stamp);
            File.SetLastWriteTimeUtc(entry, stamp);
        }

        Directory.SetCreationTimeUtc(folder, stamp);
        Directory.SetLastWriteTimeUtc(folder, stamp);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(folder)!, stamp);
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
        MakeStale(broken);

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
        MakeStale(broken);

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
        MakeStale(Path.Combine(first, "Lib", "site-packages", "~pip"));
        MakeStale(Path.Combine(second, "Lib", "site-packages", "~numpy"));

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
    public void Only_the_interpreters_the_measure_filter_accepts_get_their_site_packages_walked()
    {
        var oldest = CreateInterpreter("Python311");
        var middle = CreateInterpreter("Python312");
        var newer = CreateInterpreter("Python313");
        var oldestExe = Path.Combine(oldest, "python.exe");
        var middleExe = Path.Combine(middle, "python.exe");
        foreach (var folder in new[] { oldest, middle })
        {
            var package = Path.Combine(folder, "Lib", "site-packages", "requests");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "__init__.py"), new string('x', 1000));
            Directory.CreateDirectory(Path.Combine(folder, "Lib", "site-packages", "requests-2.0.dist-info"));
        }

        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.11", oldestExe)
            .AddTag(HkcuRoot, "3.12", middleExe)
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.11}", "Python 3.11.9 (64-bit)",
                uninstallString: oldestExe, quietUninstallString: oldestExe + " /quiet")
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
                uninstallString: middleExe, quietUninstallString: middleExe + " /quiet");

        var found = PythonCleanupDiscovery.DiscoverOldVersions(
            registry, _ => null, exe => string.Equals(exe, middleExe, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, found.Count);
        var skippedRow = Assert.Single(found, c => string.Equals(c.PythonExe, oldestExe, StringComparison.OrdinalIgnoreCase));
        var measuredRow = Assert.Single(found, c => string.Equals(c.PythonExe, middleExe, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, skippedRow.SizeBytes);
        Assert.Equal(0, skippedRow.PackageCount);
        Assert.True(measuredRow.SizeBytes >= 1000);
        Assert.Equal(1, measuredRow.PackageCount);
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

    // ---- virtual environments that still use an old interpreter ----

    private string MakeVenv(string parent, string name, string home)
    {
        var venv = Path.Combine(parent, name);
        Directory.CreateDirectory(venv);
        File.WriteAllText(Path.Combine(venv, "pyvenv.cfg"),
            $"home = {home}\ninclude-system-site-packages = false\nversion = 3.11.9\n");
        return venv;
    }

    private (IReadOnlyList<PythonVersionCandidate> Found, string OldFolder, string NewFolder) OldAndNewCandidates()
    {
        var older = CreateInterpreter("Python311");
        var newer = CreateInterpreter("Python313");
        var olderExe = Path.Combine(older, "python.exe");
        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.11", olderExe)
            .AddTag(HkcuRoot, "3.13", Path.Combine(newer, "python.exe"))
            .AddUninstallEntry(HkcuUninstallRoot, "{python-3.11}", "Python 3.11.9 (64-bit)",
                uninstallString: olderExe, quietUninstallString: olderExe + " /quiet");
        return (PythonCleanupDiscovery.DiscoverOldVersions(registry, _ => null), older, newer);
    }

    private static IReadOnlyList<PythonCleanupDiscovery.VenvSearchRoot> RootsOf(string path, int depth = 6) =>
        [new PythonCleanupDiscovery.VenvSearchRoot(path, depth)];

    [Fact]
    public void A_venv_pointing_into_the_old_version_flags_the_candidate_and_it_stays_unticked()
    {
        var (found, older, newer) = OldAndNewCandidates();
        var projects = Path.Combine(_root, "profile", "projects", "app");
        MakeVenv(projects, ".venv", older);
        MakeVenv(projects, "other-venv", newer);

        var counted = PythonCleanupDiscovery.CountVenvUses(
            found, RootsOf(Path.Combine(_root, "profile")), TimeSpan.FromSeconds(5), CancellationToken.None);

        var candidate = Assert.Single(counted);
        Assert.Equal(1, candidate.VenvUseCount);

        var row = new UltimateUpdater.ViewModels.PythonVersionCandidateViewModel(new PythonVersionCandidateDto
        {
            PythonExe = candidate.PythonExe,
            DisplayLabel = candidate.DisplayLabel,
            VenvUseCount = candidate.VenvUseCount,
        });
        Assert.False(row.IsSelected);
        Assert.True(row.HasVenvUses);
        Assert.Contains("1", row.VenvUseText);
    }

    [Fact]
    public void The_plain_discovery_the_executor_uses_never_walks_for_venvs()
    {
        var (found, older, _) = OldAndNewCandidates();
        Assert.All(found, c => Assert.Equal(0, c.VenvUseCount));
        Assert.True(Directory.Exists(older));
    }

    [Fact]
    public void A_venv_with_its_home_in_a_subfolder_of_the_version_counts_and_a_sibling_prefix_does_not()
    {
        var (found, older, _) = OldAndNewCandidates();
        var profile = Path.Combine(_root, "profile");
        MakeVenv(profile, "a", Path.Combine(older, "Scripts"));
        MakeVenv(profile, "b", older + "9"); // Python3119 is another folder, not inside Python311

        var counted = PythonCleanupDiscovery.CountVenvUses(
            found, RootsOf(profile), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(1, Assert.Single(counted).VenvUseCount);
    }

    [Fact]
    public void Folders_the_walk_must_skip_and_venvs_beyond_the_depth_are_not_counted()
    {
        var (found, older, _) = OldAndNewCandidates();
        var profile = Path.Combine(_root, "profile");
        MakeVenv(Path.Combine(profile, "node_modules", "pkg"), ".venv", older);
        MakeVenv(Path.Combine(profile, "repo", ".git"), "v", older);
        MakeVenv(Path.Combine(profile, "a", "b", "c"), "deep", older);

        var counted = PythonCleanupDiscovery.CountVenvUses(
            found, RootsOf(profile, depth: 2), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(0, Assert.Single(counted).VenvUseCount);
    }

    [Fact]
    public void A_search_root_that_is_itself_a_junction_is_not_walked()
    {
        var (found, older, _) = OldAndNewCandidates();
        var outside = Path.Combine(_root, "outside");
        MakeVenv(outside, ".venv", older);
        var link = Path.Combine(_root, "linked-root");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            var counted = PythonCleanupDiscovery.CountVenvUses(
                found, RootsOf(link), TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Equal(0, Assert.Single(counted).VenvUseCount);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_junction_in_the_walked_tree_is_not_followed()
    {
        var (found, older, _) = OldAndNewCandidates();
        var profile = Path.Combine(_root, "profile");
        var outside = Path.Combine(_root, "outside");
        MakeVenv(outside, ".venv", older);
        Directory.CreateDirectory(profile);
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{Path.Combine(profile, "link")}\" \"{outside}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            var counted = PythonCleanupDiscovery.CountVenvUses(
                found, RootsOf(profile), TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.Equal(0, Assert.Single(counted).VenvUseCount);
            Assert.True(File.Exists(Path.Combine(outside, ".venv", "pyvenv.cfg")));
        }
        finally
        {
            // Remove the link itself first so the recursive teardown never walks through it.
            Directory.Delete(Path.Combine(profile, "link"));
        }
    }

    [Fact]
    public void The_search_roots_follow_the_profile_the_caller_reported()
    {
        var profile = Path.Combine(_root, "signed-in");
        Directory.CreateDirectory(profile);

        var roots = PythonCleanupDiscovery.DefaultVenvRoots(profile);

        Assert.Equal(profile, roots[0].Path);
        Assert.Contains(roots, r => r.Path == Path.Combine(profile, "AppData", "Local", "pipx", "venvs"));
        Assert.Contains(roots, r => r.Path == Path.Combine(profile, "AppData", "Roaming", "pypoetry", "virtualenvs"));
        Assert.Contains(roots, r => r.Path == Path.Combine(profile, "AppData", "Local", "pypoetry", "Cache", "virtualenvs"));
        Assert.All(roots, r => Assert.StartsWith(profile, r.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\folder")]
    [InlineData("C:\\no-such-folder-for-the-venv-search")]
    public void An_unusable_caller_profile_falls_back_to_the_process_own_profile(string? reported)
    {
        var own = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(own, PythonCleanupDiscovery.ResolveProfile(reported));
    }

    [Fact]
    public void The_reported_profiles_own_temp_folder_is_not_walked()
    {
        var (found, older, _) = OldAndNewCandidates();
        var profile = Path.Combine(_root, "signed-in");
        MakeVenv(Path.Combine(profile, "AppData", "Local", "Temp", "scratch"), ".venv", older);
        MakeVenv(Path.Combine(profile, "projects"), ".venv", older);

        var counted = PythonCleanupDiscovery.CountVenvUses(found, profile, CancellationToken.None);

        Assert.Equal(1, Assert.Single(counted).VenvUseCount);
    }

    private string FabricatedRoaming()
    {
        var roaming = Path.Combine(_root, "Roaming");
        Directory.CreateDirectory(Path.Combine(roaming, "Python", "Python313", "site-packages"));
        return roaming;
    }

    [Fact]
    public void An_old_tilde_folder_in_the_roaming_user_site_packages_is_found_but_a_fresh_one_and_a_real_package_are_not()
    {
        var roaming = FabricatedRoaming();
        var sitePackages = Path.Combine(roaming, "Python", "Python313", "site-packages");
        var old = Path.Combine(sitePackages, "~ip");
        var fresh = Path.Combine(sitePackages, "~ewer");
        var real = Path.Combine(sitePackages, "requests");
        foreach (var folder in new[] { old, fresh, real })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "x.py"), "x");
        }

        MakeStale(old);
        MakeStale(real);
        // MakeStale also ages the shared site-packages folder; the fresh folder is stamped after it.
        File.SetLastWriteTimeUtc(Path.Combine(fresh, "x.py"), DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);

        var found = PythonCleanupDiscovery.DiscoverPipLeftovers(new FakeRegistry(), roamingAppData: roaming);

        Assert.Equal(old, Assert.Single(found).Path);
    }

    [Fact]
    public void A_tilde_folder_outside_a_Python3_version_folder_of_the_roaming_profile_is_not_looked_at()
    {
        var roaming = FabricatedRoaming();
        var elsewhere = Path.Combine(roaming, "Python", "Python27", "site-packages", "~ip");
        var directChild = Path.Combine(roaming, "Python", "Python313", "~ip");
        var nested = Path.Combine(roaming, "Python", "Python313", "site-packages", "pkg", "~ip");
        foreach (var folder in new[] { elsewhere, directChild, nested })
        {
            Directory.CreateDirectory(folder);
            MakeStale(folder);
        }

        Assert.Empty(PythonCleanupDiscovery.DiscoverPipLeftovers(new FakeRegistry(), roamingAppData: roaming));
    }

    [Fact]
    public void A_roaming_tilde_folder_that_is_a_junction_is_not_offered()
    {
        var roaming = FabricatedRoaming();
        var sitePackages = Path.Combine(roaming, "Python", "Python313", "site-packages");
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "x");
        MakeStale(target);
        var link = Path.Combine(sitePackages, "~ip");
        CreateJunction(link, target);

        // A clock three days ahead makes the junction itself old enough, so only the link rule can keep it.
        var later = DateTime.UtcNow.AddDays(3);
        Assert.Empty(PythonCleanupDiscovery.DiscoverPipLeftovers(new FakeRegistry(), roamingAppData: roaming, utcNow: later));
        Assert.True(File.Exists(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public void A_version_folder_that_is_a_junction_is_not_searched()
    {
        var roaming = Path.Combine(_root, "Roaming");
        var real = Path.Combine(_root, "realpython", "site-packages", "~ip");
        Directory.CreateDirectory(real);
        MakeStale(real);
        Directory.CreateDirectory(Path.Combine(roaming, "Python"));
        CreateJunction(Path.Combine(roaming, "Python", "Python313"), Path.Combine(_root, "realpython"));

        Assert.Empty(PythonCleanupDiscovery.DiscoverPipLeftovers(new FakeRegistry(), roamingAppData: roaming));
    }

    private static void CreateJunction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
        Assert.Equal(0, mklink.ExitCode);
    }
}
