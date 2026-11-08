using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Install;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Process detection runs entirely against an INJECTED process-name list, never real running processes,
/// so these are deterministic regardless of what happens to be running on the host.
/// </summary>
public class InstallBlockersTests
{
    private static UpdateItem Item(string id, string name = "Some App", string? installLocation = null) => new()
    {
        Id = id,
        SourceId = "winget",
        Category = UpdateCategory.Software,
        Name = name,
        Meta = installLocation is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["installLocation"] = installLocation },
    };

    [Fact]
    public void Visual_studio_processes_including_servicehub_children_are_matched_by_prefix()
    {
        var blockers = new InstallBlockers(
            runningProcessNames: () => ["devenv", "ServiceHub.Host.CLR.x86", "explorer", "notepad"]);

        var blocked = blockers.Detect(Item("Microsoft.VisualStudio.Product.Community"));

        Assert.NotNull(blocked);
        Assert.Equal("Visual Studio", blocked!.DisplayProgramName);
        Assert.Contains("devenv", blocked.ProcessNames);
        Assert.Contains("ServiceHub.Host.CLR.x86", blocked.ProcessNames);
        Assert.DoesNotContain("explorer", blocked.ProcessNames);
    }

    [Fact]
    public void Corsair_icue_is_matched_by_the_known_table()
    {
        var blockers = new InstallBlockers(runningProcessNames: () => ["iCUE", "explorer"]);

        var blocked = blockers.Detect(Item("Corsair.iCUE"));

        Assert.NotNull(blocked);
        Assert.Equal("Corsair iCUE", blocked!.DisplayProgramName);
    }

    private const string GitFolder = @"C:\Program Files\Git";

    private static InstallBlockers GitBlockers(
        IReadOnlyList<string> names, Func<string, IReadOnlyList<string?>> imagePathsOf, string? gitFolder = GitFolder) =>
        new(runningProcessNames: () => names, imagePathsOf: imagePathsOf, gitInstallFolder: () => gitFolder);

    /// <summary>Git's silent setup asks to close its own running shells and, with message boxes suppressed,
    /// asks again for ever at full CPU. A terminal on Git Bash keeps bash running all day, so the row is
    /// skipped for the run instead of asking the user to close anything.</summary>
    [Fact]
    public void Git_is_skipped_while_its_own_shells_run_and_never_offered_for_closing()
    {
        var blockers = GitBlockers(
            ["bash", "git", "GitHubDesktop", "ShareX", "explorer"],
            name => name switch
            {
                "bash" => [$@"{GitFolder}\usr\bin\bash.exe", $@"{GitFolder}\bin\bash.exe", $@"{GitFolder}\usr\bin\bash.exe"],
                "git" => [$@"{GitFolder}\cmd\git.exe"],
                _ => [],
            });

        var blocked = blockers.Detect(Item("Git.Git"));

        Assert.NotNull(blocked);
        Assert.True(blocked!.SkipOnly);
        Assert.Equal("Git", blocked.DisplayProgramName);
        Assert.Equal("bash ×3, git", blocked.DescribeProcesses());
    }

    /// <summary>Windows' own OpenSSH shares the ssh name but lives in the Windows folder; it must never count.</summary>
    [Fact]
    public void Git_ignores_an_ssh_that_runs_from_the_windows_folder()
    {
        var blockers = GitBlockers(
            ["ssh", "sshd-session", "explorer"],
            name => name == "ssh" ? [@"C:\Windows\System32\OpenSSH\ssh.exe"] : []);

        Assert.Null(blockers.Detect(Item("Git.Git")));
    }

    [Fact]
    public void Git_counts_its_own_ssh_even_when_a_windows_ssh_runs_too()
    {
        var blockers = GitBlockers(
            ["ssh"],
            _ => [@"C:\Windows\System32\OpenSSH\ssh.exe", $@"{GitFolder}\usr\bin\ssh.exe"]);

        var blocked = blockers.Detect(Item("Git.Git"));

        Assert.NotNull(blocked);
        Assert.Equal(["ssh"], blocked!.ProcessNames);
    }

    [Fact]
    public void A_folder_next_to_gits_with_the_same_start_does_not_count()
    {
        var blockers = GitBlockers(["bash"], _ => [@"C:\Program Files\Git2\bin\bash.exe"]);

        Assert.Null(blockers.Detect(Item("Git.Git")));
    }

    [Fact]
    public void Git_without_a_findable_install_folder_is_not_skipped()
    {
        var blockers = GitBlockers(["bash"], _ => [$@"{GitFolder}\bin\bash.exe"], gitFolder: null);

        Assert.Null(blockers.Detect(Item("Git.Git")));
    }

    [Fact]
    public void A_process_whose_image_cannot_be_read_does_not_hold_git_back()
    {
        var blockers = GitBlockers(["bash"], _ => []);

        Assert.Null(blockers.Detect(Item("Git.Git")));
    }

    [Fact]
    public void An_unrelated_package_with_the_known_processes_running_is_not_blocked()
    {
        var blockers = new InstallBlockers(runningProcessNames: () => ["devenv", "iCUE"]);

        Assert.Null(blockers.Detect(Item("Some.Unrelated.Package")));
    }

    [Fact]
    public void Nothing_running_means_no_blocker_regardless_of_the_table()
    {
        var blockers = new InstallBlockers(runningProcessNames: () => []);

        Assert.Null(blockers.Detect(Item("Microsoft.VisualStudio.Product.Community")));
    }

    [Fact]
    public void The_generic_path_matches_a_top_level_executable_in_the_resolved_install_directory()
    {
        const string folder = @"C:\Program Files\Some Vendor\Thing";
        var resolver = new InstallLocationResolver(
            new EmptyRegistry(),
            directoryExists: dir => dir == folder,
            fileExists: _ => true,
            executablesIn: _ => [$@"{folder}\thing.exe", $@"{folder}\updater.exe"]);

        var blockers = new InstallBlockers(
            resolver,
            runningProcessNames: () => ["thing"],
            topLevelExecutableNames: _ => ["thing", "updater"]);

        var item = Item("Some.Vendor.Thing", "Some Vendor Thing", installLocation: folder);

        var blocked = blockers.Detect(item);

        Assert.NotNull(blocked);
        Assert.Equal("Some Vendor Thing", blocked!.DisplayProgramName);
        Assert.Contains("thing", blocked.ProcessNames);
    }

    private static InstallBlockers GenericBlockers(Func<string, IReadOnlyList<string?>> imagePathsOf, out UpdateItem item)
    {
        const string folder = @"C:\Python312";
        var resolver = new InstallLocationResolver(
            new EmptyRegistry(),
            directoryExists: dir => dir == folder,
            fileExists: _ => true,
            executablesIn: _ => [$@"{folder}\python.exe"]);
        item = Item("Python.Python.3.12", "Python 3.12", installLocation: folder);
        return new InstallBlockers(
            resolver,
            runningProcessNames: () => ["python"],
            topLevelExecutableNames: _ => ["python"],
            imagePathsOf: imagePathsOf);
    }

    /// <summary>A script running on another Python must not block the update of this one.</summary>
    [Fact]
    public void The_generic_path_ignores_a_same_named_program_running_from_another_folder()
    {
        var blockers = GenericBlockers(_ => [@"C:\Python311\python.exe", @"C:\Python3120\python.exe"], out var item);

        Assert.Null(blockers.Detect(item));
    }

    [Fact]
    public void The_generic_path_blocks_when_one_instance_runs_from_the_install_folder()
    {
        var blockers = GenericBlockers(_ => [@"C:\Python311\python.exe", @"C:\Python312\python.exe"], out var item);

        var blocked = blockers.Detect(item);

        Assert.NotNull(blocked);
        Assert.Equal(["python"], blocked!.ProcessNames);
    }

    [Fact]
    public void The_generic_path_blocks_when_an_instances_image_cannot_be_read()
    {
        var blockers = GenericBlockers(_ => [@"C:\Python311\python.exe", null], out var item);

        Assert.NotNull(blockers.Detect(item));
    }

    /// <summary>
    /// Defense in depth: a system process must never be suggested or closed,
    /// EVEN IF the generic install-directory scan would otherwise match one of these names. This cannot
    /// happen through a real install directory in practice, the point of this test is that the guard
    /// exists and actually refuses, not that the scenario is realistic.
    /// </summary>
    [Fact]
    public void A_system_process_name_is_never_suggested_even_if_the_generic_scan_would_otherwise_match_it()
    {
        const string folder = @"C:\Windows";
        var resolver = new InstallLocationResolver(
            new EmptyRegistry(),
            directoryExists: dir => dir == folder,
            fileExists: _ => true,
            executablesIn: _ => [$@"{folder}\explorer.exe"]);

        var blockers = new InstallBlockers(
            resolver,
            runningProcessNames: () => ["explorer"],
            topLevelExecutableNames: _ => ["explorer"]);

        var item = Item("Hypothetical.Package", installLocation: folder);

        Assert.Null(blockers.Detect(item));
    }

    /// <summary>
    /// The same refusal has to be available to whoever does the closing: detection and termination are
    /// separated by several layers, and only the terminating end holds a live process.
    /// </summary>
    [Theory]
    [InlineData("explorer", true)]
    [InlineData("lsass", true)]
    [InlineData("LSASS", true)]
    [InlineData("svchost", true)]
    [InlineData("devenv", false)]
    [InlineData("Discord", false)]
    public void The_system_process_rule_is_available_to_the_caller_that_closes(string processName, bool refused) =>
        Assert.Equal(refused, InstallBlockers.IsNeverClosed(processName));

    /// <summary>
    /// The bug this guards: two update rows blocked by the same running program (e.g. two Visual Studio
    /// components, both waiting on devenv) used to be asked about one at a time, so the same real-world
    /// decision produced two separate prompts. <see cref="InstallBlockers.GroupBlockedRows{TRow}"/> is the
    /// pure grouping step the ViewModel now runs BEFORE asking anything, so the caller shows exactly one
    /// dialog per group and applies its answer to every row inside.
    /// </summary>
    [Fact]
    public void Two_rows_blocked_by_the_same_program_group_into_a_single_entry()
    {
        var blockers = new InstallBlockers(runningProcessNames: () => ["devenv"]);

        var blockerA = blockers.Detect(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio Community"));
        var blockerB = blockers.Detect(Item("Microsoft.VisualStudio.Product.Professional", "Visual Studio Professional"));
        Assert.NotNull(blockerA);
        Assert.NotNull(blockerB);

        var grouped = InstallBlockers.GroupBlockedRows(new[]
        {
            ("row-community", blockerA!),
            ("row-professional", blockerB!),
        });

        var group = Assert.Single(grouped);
        Assert.Equal("Visual Studio", group.Blocker.DisplayProgramName);
        Assert.Equal(2, group.Rows.Count);
        Assert.Contains("row-community", group.Rows);
        Assert.Contains("row-professional", group.Rows);
    }

    /// <summary>Different blocking programs must never be folded together, only the identical one.</summary>
    [Fact]
    public void Rows_blocked_by_different_programs_stay_in_separate_groups()
    {
        var blockers = new InstallBlockers(runningProcessNames: () => ["devenv", "Discord"]);

        var blockerA = blockers.Detect(Item("Microsoft.VisualStudio.Product.Community", "Visual Studio Community"));
        var blockerB = blockers.Detect(Item("Discord.Discord", "Discord"));
        Assert.NotNull(blockerA);
        Assert.NotNull(blockerB);

        var grouped = InstallBlockers.GroupBlockedRows(new[]
        {
            ("row-vs", blockerA!),
            ("row-discord", blockerB!),
        });

        Assert.Equal(2, grouped.Count);
    }

    /// <summary>
    /// <see cref="InstallBlockers.Detect(UpdateItem, IReadOnlyList{string})"/> is the batch-friendly path
    /// (one shared process-name snapshot instead of one <see cref="System.Diagnostics.Process.GetProcesses"/>
    /// snapshot per row): it must reach exactly the same verdict as the per-row overload it factors out of,
    /// for the same effective snapshot.
    /// </summary>
    [Fact]
    public void Detect_with_a_shared_snapshot_matches_the_per_row_overload()
    {
        IReadOnlyList<string> snapshot = ["devenv", "ServiceHub.Host.CLR.x86", "explorer"];
        var blockers = new InstallBlockers(runningProcessNames: () => snapshot);
        var item = Item("Microsoft.VisualStudio.Product.Community");

        var viaSnapshot = blockers.Detect(item, snapshot);
        var viaPerRow = blockers.Detect(item);

        Assert.NotNull(viaSnapshot);
        Assert.NotNull(viaPerRow);
        Assert.Equal(viaPerRow!.DisplayProgramName, viaSnapshot!.DisplayProgramName);
        Assert.Equal(viaPerRow.ProcessNames, viaSnapshot.ProcessNames);
    }

    /// <summary>An empty shared snapshot must refuse exactly like an empty per-row one does.</summary>
    [Fact]
    public void Detect_with_an_empty_shared_snapshot_finds_no_blocker()
    {
        var blockers = new InstallBlockers();

        Assert.Null(blockers.Detect(Item("Microsoft.VisualStudio.Product.Community"), []));
    }

    /// <summary>
    /// <see cref="InstallBlockers.CaptureRunningProcessNames"/> is the real (uninjected)
    /// <see cref="System.Diagnostics.Process.GetProcesses"/> snapshot a caller takes ONCE per install batch;
    /// every Process object it reads a name from is disposed before returning (Detect's own doc comment).
    /// Exercised against the real process table (every Windows session has several processes running) since
    /// there is nothing to inject it against; a double-dispose or a read-after-dispose bug here would throw
    /// out of this call, not merely leak.
    /// </summary>
    [Fact]
    public void CaptureRunningProcessNames_returns_real_non_empty_process_names()
    {
        var names = InstallBlockers.CaptureRunningProcessNames();

        Assert.NotEmpty(names);
        Assert.All(names, Assert.NotNull);
    }

    private sealed class EmptyRegistry : IRegistryView
    {
        public IReadOnlyList<string> SubKeyNames(string keyPath) => [];

        public string? Value(string keyPath, string valueName) => null;
    }

    [Fact]
    public void The_top_level_executable_list_is_capped()
    {
        var directory = Directory.CreateTempSubdirectory("uu-blockers-").FullName;
        try
        {
            for (var i = 0; i < 300; i++)
                File.WriteAllBytes(Path.Combine(directory, $"tool{i}.exe"), []);

            Assert.Equal(InstallBlockers.MaxTopLevelExecutables, InstallBlockers.TopLevelExecutableNames(directory).Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_small_directory_returns_all_its_executables_and_a_missing_one_returns_none()
    {
        var directory = Directory.CreateTempSubdirectory("uu-blockers-").FullName;
        try
        {
            foreach (var name in new[] { "a.exe", "b.exe", "c.exe" })
                File.WriteAllBytes(Path.Combine(directory, name), []);

            Assert.Equal(3, InstallBlockers.TopLevelExecutableNames(directory).Count);
            Assert.Empty(InstallBlockers.TopLevelExecutableNames(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class PackageTable(string name, string root) : IRegistryView
    {
        private const string Full = "Zq.Chat_1.0.0.0_x64__0123456789abc";

        public IReadOnlyList<string> SubKeyNames(string keyPath) => keyPath == PackagedApps.PackagesKey ? [Full] : [];

        public string? Value(string keyPath, string valueName) => valueName switch
        {
            "DisplayName" => name,
            "PackageRootFolder" => root,
            _ => null,
        };
    }

    private const string StoreRoot = @"C:\Program Files\WindowsApps\Zq.Chat_1.0.0.0_x64__0123456789abc";

    private static InstallBlockers StoreBlockers(params (string Name, string Path)[] processes) => new(
        runningProcessNames: () => [.. processes.Select(p => p.Name)],
        packagedApps: new PackagedApps(new PackageTable("Zq Chat", StoreRoot)),
        processImages: () => processes);

    /// <summary>A Store app keeps its files in a folder of its own, its executables a few levels down, so the
    /// top-level scan of a normal install folder never sees them; the process path decides.</summary>
    [Fact]
    public void A_running_process_from_the_store_package_folder_blocks_the_update()
    {
        var blockers = StoreBlockers(
            ("Chat", StoreRoot + @"\app\Chat.exe"), ("Chat", @"D:\Other\Chat.exe"), ("explorer", @"C:\Windows\explorer.exe"));

        var blocked = blockers.Detect(Item("Zq.Chat", "Zq Chat"));

        Assert.NotNull(blocked);
        Assert.Equal("Zq Chat", blocked!.DisplayProgramName);
        Assert.Equal(["Chat"], blocked.ProcessNames);
        Assert.Equal(StoreRoot, blocked.OnlyInsideFolder);
        Assert.False(blocked.SkipOnly);
    }

    private sealed class TwoVersions(string oldRoot, string newRoot) : IRegistryView
    {
        private const string Old = "Zq.Chat_1.0.0.0_x64__0123456789abc";
        private const string New = "Zq.Chat_2.0.0.0_x64__0123456789abc";

        public IReadOnlyList<string> SubKeyNames(string keyPath) => keyPath == PackagedApps.PackagesKey ? [Old, New] : [];

        public string? Value(string keyPath, string valueName) => valueName switch
        {
            "DisplayName" => "Zq Chat",
            "PackageRootFolder" => keyPath.EndsWith(New, StringComparison.Ordinal) ? newRoot : oldRoot,
            _ => null,
        };
    }

    /// <summary>While a newer version waits to be applied, both are registered and the app still runs from
    /// the older folder; that running copy must still block, and closing is limited to that folder.</summary>
    [Fact]
    public void A_running_older_registration_of_the_store_app_still_blocks()
    {
        const string oldRoot = @"C:\Program Files\WindowsApps\Zq.Chat_1.0.0.0_x64__0123456789abc";
        const string newRoot = @"C:\Program Files\WindowsApps\Zq.Chat_2.0.0.0_x64__0123456789abc";
        var blockers = new InstallBlockers(
            runningProcessNames: () => ["Chat"],
            packagedApps: new PackagedApps(new TwoVersions(oldRoot, newRoot)),
            processImages: () => [("Chat", oldRoot + @"\app\Chat.exe")]);

        var blocked = blockers.Detect(Item("Zq.Chat", "Zq Chat"));

        Assert.NotNull(blocked);
        Assert.Equal(oldRoot, blocked!.OnlyInsideFolder);
    }

    [Fact]
    public void A_same_named_process_from_another_folder_does_not_block_a_store_update()
    {
        var blockers = StoreBlockers(("Chat", @"D:\Other\Chat.exe"), ("Chat", StoreRoot + "-sibling" + @"\Chat.exe"));

        Assert.Null(blockers.Detect(Item("Zq.Chat", "Zq Chat")));
    }

    [Fact]
    public void Only_a_winget_row_with_the_exact_package_name_is_looked_up_as_a_store_app()
    {
        var blockers = StoreBlockers(("Chat", StoreRoot + @"\Chat.exe"));

        Assert.Null(blockers.Detect(Item("Zq.Chat", "Zq Chat Pro")));
        Assert.Null(blockers.Detect(new UpdateItem
        {
            Id = "Zq.Chat", SourceId = "pip", Category = UpdateCategory.Software, Name = "Zq Chat",
        }));
    }

    [Fact]
    public void Without_a_package_table_no_store_app_is_looked_for()
    {
        var blockers = new InstallBlockers(
            runningProcessNames: () => ["Chat"], processImages: () => [("Chat", StoreRoot + @"\Chat.exe")]);

        Assert.Null(blockers.Detect(Item("Zq.Chat", "Zq Chat")));
    }

    [Fact]
    public void Rows_with_the_same_process_name_but_different_package_folders_are_not_one_group()
    {
        var item = Item("Zq.Chat", "Zq Chat");
        var inPackage = new BlockedInstall(item, "Zq Chat", ["Chat"], OnlyInsideFolder: StoreRoot);
        var elsewhere = new BlockedInstall(item, "Zq Chat", ["Chat"], OnlyInsideFolder: @"C:\Other");

        var groups = InstallBlockers.GroupBlockedRows([(1, inPackage), (2, elsewhere), (3, inPackage)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal([1, 3], groups[0].Rows);
    }
}
