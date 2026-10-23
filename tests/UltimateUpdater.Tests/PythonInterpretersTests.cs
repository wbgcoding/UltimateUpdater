using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// Discovery through a fabricated PEP 514 registry rather than the real machine, the same
/// <see cref="IRegistryView"/> seam <see cref="GogGalaxySource"/>/<see cref="SteamSource"/> already use.
/// Mirrors a real host: two per-user interpreters (3.13 and 3.14) that PATH alone only ever resolved
/// one of.
/// </summary>
public class PythonInterpretersTests
{
    /// <summary>A registry made of nothing but the keys a test puts in it.</summary>
    private sealed class FakeRegistry : IRegistryView
    {
        private readonly Dictionary<string, Dictionary<string, string>> _values =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _subKeys =
            new(StringComparer.OrdinalIgnoreCase);

        public FakeRegistry AddTag(string rootPath, string tag, string installPathKeyValue, string? executablePath = null)
        {
            if (!_subKeys.TryGetValue(rootPath, out var tags))
                _subKeys[rootPath] = tags = [];
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                tags.Add(tag);

            var installPathKey = $@"{rootPath}\{tag}\InstallPath";
            Set(installPathKey, string.Empty, installPathKeyValue);
            if (executablePath is not null)
                Set(installPathKey, "ExecutablePath", executablePath);

            return this;
        }

        public FakeRegistry AddUninstallEntry(
            string root, string key, string displayName, string uninstallString, string? quietUninstallString = null,
            string? installLocation = null, bool systemComponent = false)
        {
            if (!_subKeys.TryGetValue(root, out var keys))
                _subKeys[root] = keys = [];
            keys.Add(key);

            var keyPath = $@"{root}\{key}";
            Set(keyPath, "DisplayName", displayName);
            Set(keyPath, "UninstallString", uninstallString);
            if (quietUninstallString is not null)
                Set(keyPath, "QuietUninstallString", quietUninstallString);
            if (installLocation is not null)
                Set(keyPath, "InstallLocation", installLocation);
            if (systemComponent)
                Set(keyPath, "SystemComponent", "1");

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
    private const string HklmRoot = @"HKLM\SOFTWARE\Python\PythonCore";
    private const string Wow6432Root = @"HKLM\SOFTWARE\WOW6432Node\Python\PythonCore";

    [Fact]
    public void Finds_both_interpreters_of_a_two_version_per_user_install()
    {
        var registry = new FakeRegistry()
            .AddTag(HkcuRoot, "3.13", @"C:\Users\testuser\AppData\Local\Programs\Python\Python313",
                executablePath: @"C:\Users\testuser\AppData\Local\Programs\Python\Python313\python.exe")
            .AddTag(HkcuRoot, "3.14", @"C:\Users\testuser\AppData\Local\Programs\Python\Python314",
                executablePath: @"C:\Users\testuser\AppData\Local\Programs\Python\Python314\python.exe");

        var found = PythonInterpreters.Discover(registry, _ => true);

        Assert.Equal(2, found.Count);
        Assert.All(found, i => Assert.True(i.PerUser));
        // Newest first.
        Assert.Equal(new Version(3, 14), found[0].Version);
        Assert.Equal(new Version(3, 13), found[1].Version);
    }

    [Fact]
    public void Falls_back_to_python_exe_directly_under_installpath_when_executablepath_is_missing()
    {
        var registry = new FakeRegistry().AddTag(HklmRoot, "3.12", @"C:\Python312");

        var found = PythonInterpreters.Discover(registry, path => path == @"C:\Python312\python.exe");

        Assert.Single(found);
        Assert.Equal(@"C:\Python312\python.exe", found[0].PythonExe);
        Assert.False(found[0].PerUser);
    }

    [Fact]
    public void A_path_that_does_not_actually_exist_on_disk_is_not_reported()
    {
        var registry = new FakeRegistry().AddTag(HkcuRoot, "3.13", @"C:\ghost", executablePath: @"C:\ghost\python.exe");

        var found = PythonInterpreters.Discover(registry, _ => false);

        Assert.Empty(found);
    }

    [Fact]
    public void The_store_alias_under_windowsapps_is_never_reported()
    {
        var registry = new FakeRegistry().AddTag(
            HkcuRoot, "3.11",
            @"C:\Users\testuser\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.11_qbz5n2kfra8p0",
            executablePath: @"C:\Users\testuser\AppData\Local\Microsoft\WindowsApps\python3.11.exe");

        var found = PythonInterpreters.Discover(registry, _ => true);

        Assert.Empty(found);
    }

    [Fact]
    public void The_same_interpreter_registered_under_two_keys_is_reported_only_once()
    {
        // A 64-bit interpreter can end up registered both directly and via a stale WOW6432Node entry;
        // whichever is seen first, the same real path must only ever be counted once.
        var registry = new FakeRegistry()
            .AddTag(HklmRoot, "3.13", @"C:\Python313", executablePath: @"C:\Python313\python.exe")
            .AddTag(Wow6432Root, "3.13", @"C:\Python313", executablePath: @"C:\Python313\python.exe");

        var found = PythonInterpreters.Discover(registry, _ => true);

        Assert.Single(found);
    }

    [Fact]
    public void A_wow6432node_entry_is_reported_as_32_bit()
    {
        var registry = new FakeRegistry().AddTag(
            Wow6432Root, "3.13-32", @"C:\Python313-32", executablePath: @"C:\Python313-32\python.exe");

        var found = PythonInterpreters.Discover(registry, _ => true);

        Assert.Single(found);
        Assert.False(found[0].Is64Bit);
    }

    [Fact]
    public void No_registered_interpreter_at_all_yields_an_empty_list()
    {
        Assert.Empty(PythonInterpreters.Discover(new FakeRegistry(), _ => true));
    }

    [Theory]
    [InlineData("3.13", "3.13")]
    [InlineData("3.13-32", "3.13")]
    [InlineData("3.13-arm64", "3.13")]
    [InlineData("3", "3.0")]
    [InlineData("garbage", "0.0")]
    public void Tag_version_parsing_ignores_the_architecture_suffix(string tag, string expectedVersion)
    {
        Assert.Equal(Version.Parse(expectedVersion), PythonInterpreters.ParseVersion(tag));
    }

    [Fact]
    public void Finds_older_versions_of_the_same_architecture_only()
    {
        var interpreters = new[]
        {
            new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true),
            new PythonInterpreter(@"C:\Python314\python.exe", new Version(3, 14), PerUser: true, Is64Bit: true),
            // Only 32-bit interpreter on the machine: never "older" on its own.
            new PythonInterpreter(@"C:\Python312-32\python.exe", new Version(3, 12), PerUser: false, Is64Bit: false),
        };

        var older = PythonInterpreters.FindOlderVersions(interpreters);

        Assert.Single(older);
        Assert.Equal(new Version(3, 13), older[0].Version);
    }

    [Fact]
    public void A_single_interpreter_machine_has_no_older_versions()
    {
        var interpreters = new[]
        {
            new PythonInterpreter(@"C:\Python314\python.exe", new Version(3, 14), PerUser: true, Is64Bit: true),
        };

        Assert.Empty(PythonInterpreters.FindOlderVersions(interpreters));
    }

    [Fact]
    public void Finds_only_the_tilde_prefixed_leftover_folders_in_site_packages()
    {
        var sitePackages = Directory.CreateTempSubdirectory("wu-sitepkgs-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(sitePackages, "~ip"));
            Directory.CreateDirectory(Path.Combine(sitePackages, "~orchvision"));
            Directory.CreateDirectory(Path.Combine(sitePackages, "requests"));
            Directory.CreateDirectory(Path.Combine(sitePackages, "requests-2.32.0.dist-info"));

            var leftovers = PythonInterpreters.FindBrokenInstallLeftovers(sitePackages);

            Assert.Equal(2, leftovers.Count);
            Assert.All(leftovers, l => Assert.StartsWith("~", Path.GetFileName(l)));
        }
        finally
        {
            Directory.Delete(sitePackages, recursive: true);
        }
    }

    [Fact]
    public void A_missing_site_packages_folder_yields_no_leftovers()
    {
        Assert.Empty(PythonInterpreters.FindBrokenInstallLeftovers(@"C:\does-not-exist-wu-test"));
    }

    [Fact]
    public void Site_packages_is_derived_from_the_interpreters_own_install_root()
    {
        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Equal(@"C:\Python313\Lib\site-packages", PythonInterpreters.SitePackagesFor(interpreter));
    }

    private const string HkcuUninstallRoot = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string HklmUninstallRoot = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    [Fact]
    public void Prefers_the_quiet_uninstall_string_when_the_entry_has_one()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.13}", "Python 3.13.2 (64-bit)",
            uninstallString: @"C:\Python313\uninstall.exe",
            quietUninstallString: @"C:\Python313\uninstall.exe /quiet");

        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Equal(@"C:\Python313\uninstall.exe /quiet", PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void Falls_back_to_the_python_org_quiet_switches_when_only_the_interactive_string_exists()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.13}", "Python 3.13.2 (64-bit)",
            uninstallString: @"C:\Python313\uninstall.exe");

        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Equal(
            @"C:\Python313\uninstall.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void No_matching_uninstall_entry_returns_null()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{some-other-app}", "Totally Unrelated App 1.0",
            uninstallString: @"C:\Other\uninstall.exe");

        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void Python_3_1_never_matches_the_uninstall_entry_of_python_3_10()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.10}", "Python 3.10.11 (64-bit)",
            uninstallString: @"C:\Python310\uninstall.exe");
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.1}", "Python 3.1.5 (64-bit)",
            uninstallString: @"C:\Python31\uninstall.exe");

        var old = new PythonInterpreter(@"C:\Python31\python.exe", new Version(3, 1), PerUser: true, Is64Bit: true);
        var newer = new PythonInterpreter(@"C:\Python310\python.exe", new Version(3, 10), PerUser: true, Is64Bit: true);

        Assert.Equal(@"C:\Python31\uninstall.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, old));
        Assert.Equal(@"C:\Python310\uninstall.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, newer));
    }

    [Fact]
    public void Hidden_component_entries_of_a_bundle_do_not_make_its_uninstaller_ambiguous()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{bundle}", "Python 3.12.1 (64-bit)",
            uninstallString: @"C:\Users\x\Bundle.exe /uninstall",
            quietUninstallString: @"C:\Users\x\Bundle.exe /uninstall /quiet");
        registry.AddUninstallEntry(HkcuUninstallRoot, "{core}", "Python 3.12.1 Core Interpreter (64-bit)",
            uninstallString: @"MsiExec.exe /X{CORE}", systemComponent: true);
        registry.AddUninstallEntry(HkcuUninstallRoot, "{tcl}", "Python 3.12.1 Tcl/Tk Support (64-bit)",
            uninstallString: @"MsiExec.exe /X{TCL}", systemComponent: true);
        registry.AddUninstallEntry(HkcuUninstallRoot, "{docs}", "Python 3.12.1 Documentation (64-bit)",
            uninstallString: @"MsiExec.exe /X{DOCS}", systemComponent: true);

        var interpreter = new PythonInterpreter(@"C:\Python312\python.exe", new Version(3, 12), PerUser: true, Is64Bit: true);

        Assert.Equal(@"C:\Users\x\Bundle.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void Python_3_1_without_its_own_entry_does_not_fall_onto_python_3_10()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.10}", "Python 3.10.11 (64-bit)",
            uninstallString: @"C:\Python310\uninstall.exe");

        var old = new PythonInterpreter(@"C:\Python31\python.exe", new Version(3, 1), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, old));
    }

    [Fact]
    public void An_entry_whose_install_location_is_the_interpreters_directory_wins()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.13-a}", "Python 3.13.1 (64-bit)",
            uninstallString: @"C:\Other\uninstall.exe", installLocation: @"C:\Other\");
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.13-b}", "Python 3.13.2 (64-bit)",
            uninstallString: @"C:\Python313\uninstall.exe", installLocation: @"C:\Python313");

        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Equal(
            @"C:\Python313\uninstall.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void An_entry_that_names_a_different_install_location_is_not_used()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.13-a}", "Python 3.13.1 (64-bit)",
            uninstallString: @"C:\Other\uninstall.exe", installLocation: @"C:\Other");

        var interpreter = new PythonInterpreter(@"C:\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void A_machine_wide_interpreter_is_matched_against_the_hklm_uninstall_tree()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HklmUninstallRoot, "{python-3.12}", "Python 3.12.5 (64-bit)",
            uninstallString: @"C:\Program Files\Python312\uninstall.exe",
            quietUninstallString: @"C:\Program Files\Python312\uninstall.exe /quiet");

        var interpreter = new PythonInterpreter(@"C:\Program Files\Python312\python.exe", new Version(3, 12), PerUser: false, Is64Bit: true);

        Assert.Equal(
            @"C:\Program Files\Python312\uninstall.exe /quiet", PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void A_fallback_entry_of_the_other_architecture_is_never_used()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.11-64}", "Python 3.11.5 (64-bit)",
            uninstallString: @"C:\x64\uninstall.exe");
        registry.AddUninstallEntry(HkcuUninstallRoot, "{python-3.11-32}", "Python 3.11.5 (32-bit)",
            uninstallString: @"C:\x86\uninstall.exe");

        var x86 = new PythonInterpreter(@"C:\Py311-32\python.exe", new Version(3, 11), PerUser: true, Is64Bit: false);

        Assert.Equal(@"C:\x86\uninstall.exe /uninstall /quiet", PythonInterpreters.FindUninstallCommand(registry, x86));
    }

    [Fact]
    public void Two_fallback_entries_the_name_cannot_tell_apart_give_no_uninstaller()
    {
        var registry = new FakeRegistry();
        registry.AddUninstallEntry(HkcuUninstallRoot, "{a}", "Python 3.11.5", uninstallString: @"C:\a\uninstall.exe");
        registry.AddUninstallEntry(HkcuUninstallRoot, "{b}", "Python 3.11.4", uninstallString: @"C:\b\uninstall.exe");

        var interpreter = new PythonInterpreter(@"C:\Py311\python.exe", new Version(3, 11), PerUser: true, Is64Bit: true);

        Assert.Null(PythonInterpreters.FindUninstallCommand(registry, interpreter));
    }

    [Fact]
    public void A_per_user_tag_ending_in_minus_32_is_reported_as_32_bit()
    {
        var registry = new FakeRegistry().AddTag(HkcuRoot, "3.11-32", @"C:\Py311-32");

        var interpreter = Assert.Single(PythonInterpreters.Discover(registry, _ => true));
        Assert.False(interpreter.Is64Bit);
    }
}
