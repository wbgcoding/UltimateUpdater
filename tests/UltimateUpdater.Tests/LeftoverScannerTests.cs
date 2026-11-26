using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The temp tree and fake registry every leftover test points the scanner at.</summary>
internal sealed class LeftoverTree : IDisposable
{
    internal const string RemovedKey = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Gone";

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "uu-leftover-" + Guid.NewGuid().ToString("N"));

    internal FakeUninstallRegistry Registry { get; } = new();

    /// <summary>Shortcut file to the target it points at (null: no target).</summary>
    internal Dictionary<string, string?> ShortcutTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal string AppData => Path.Combine(Root, "AppData", "Roaming");

    internal string LocalAppData => Path.Combine(Root, "AppData", "Local");

    internal string ProgramData => Path.Combine(Root, "ProgramData");

    internal string ProgramFiles => Path.Combine(Root, "Program Files");

    internal string UserStartMenu => Path.Combine(AppData, "Microsoft", "Windows", "Start Menu", "Programs");

    internal string MachineStartMenu => Path.Combine(ProgramData, "Microsoft", "Windows", "Start Menu", "Programs");

    internal string Desktop => Path.Combine(Root, "Desktop");

    internal string Documents => Path.Combine(Root, "Documents");

    internal LeftoverTree()
    {
        foreach (var folder in new[] { AppData, LocalAppData, ProgramData, ProgramFiles, UserStartMenu, MachineStartMenu, Desktop, Documents })
            Directory.CreateDirectory(folder);
    }

    internal LeftoverRoots Roots => new(
        AppData, LocalAppData, ProgramData, [ProgramFiles], [UserStartMenu, Desktop], [MachineStartMenu],
        [Documents, Desktop, Root, AppData, LocalAppData, ProgramData, ProgramFiles]);

    internal LeftoverScanner Scanner() => new(
        Roots, Registry, exists: path => File.Exists(path) || Directory.Exists(path),
        readTargets: files => files.ToDictionary(f => f, f => ShortcutTargets.GetValueOrDefault(f), StringComparer.OrdinalIgnoreCase));

    internal LeftoverClaims Claims(string? excludeKey = null) => LeftoverClaims.FromRegistry(Registry, excludeKey);

    internal string Folder(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "data.txt"), "x");
        return path;
    }

    internal string Shortcut(string folder, string name, string? target)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, name + ".lnk");
        File.WriteAllText(file, "lnk");
        ShortcutTargets[file] = target;
        return file;
    }

    /// <summary>A program that is still installed, listed in the Uninstall keys.</summary>
    internal void StillInstalled(string id, string name, string publisher, string? installLocation = null)
    {
        var key = $@"{FakeUninstallRegistry.Hklm64}\{id}";
        Registry.Program(FakeUninstallRegistry.Hklm64, id, name).Set(key, "Publisher", publisher);
        if (installLocation is not null)
            Registry.Set(key, "InstallLocation", installLocation);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scratch folder left behind in Temp is harmless.
        }
    }
}

public sealed class LeftoverScannerTests
{
    private static readonly LeftoverSubject Notes = new(LeftoverTree.RemovedKey, "Contoso Notes", "Contoso Ltd.", string.Empty);

    private static IReadOnlyList<LeftoverItem> Scan(LeftoverTree tree, LeftoverSubject subject, LeftoverLane? lane = null) =>
        tree.Scanner().Scan(subject, tree.Claims(subject.Key), lane, CancellationToken.None);

    [Fact]
    public void A_folder_named_exactly_like_the_program_is_found_but_not_preselected()
    {
        using var tree = new LeftoverTree();
        var folder = tree.Folder("AppData", "Roaming", "Contoso Notes");

        var item = Assert.Single(Scan(tree, Notes));

        Assert.Equal(LeftoverKind.Folder, item.Kind);
        Assert.Equal(folder, item.Path);
        Assert.Equal(LeftoverLane.User, item.Lane);
        Assert.False(item.Preselected);
        Assert.True(item.SizeBytes > 0);
    }

    [Fact]
    public void A_publisher_and_program_folder_is_preselected_and_the_publisher_folder_is_not_offered()
    {
        using var tree = new LeftoverTree();
        var program = tree.Folder("Program Files", "Contoso", "ContosoNotes");
        var other = tree.Folder("Program Files", "Contoso", "Contoso Mail");

        var items = Scan(tree, Notes);

        var item = Assert.Single(items);
        Assert.Equal(program, item.Path);
        Assert.True(item.Preselected);
        Assert.Equal(LeftoverLane.Machine, item.Lane);
        Assert.DoesNotContain(items, i => i.Path == other || i.Path == Path.GetDirectoryName(program));
    }

    [Fact]
    public void The_install_folder_is_found_and_preselected_when_it_sits_where_programs_are_installed()
    {
        using var tree = new LeftoverTree();
        var install = tree.Folder("Program Files", "Notes Tool");
        var subject = Notes with { InstallLocation = install };

        var item = Assert.Single(Scan(tree, subject));

        Assert.Equal(install, item.Path);
        Assert.True(item.Preselected);
    }

    [Fact]
    public void An_install_folder_in_an_unusual_place_is_offered_but_not_preselected()
    {
        using var tree = new LeftoverTree();
        var install = tree.Folder("Games", "Stuff", "Deep", "Notes Tool");
        var subject = Notes with { InstallLocation = install };

        var item = Assert.Single(Scan(tree, subject));

        Assert.Equal(install, item.Path);
        Assert.False(item.Preselected);
    }

    [Fact]
    public void A_shared_publisher_folder_of_a_still_installed_program_is_not_offered()
    {
        using var tree = new LeftoverTree();
        var shared = tree.Folder("Program Files", "Contoso");
        var mail = tree.Folder("Program Files", "Contoso", "Contoso Mail");
        tree.StillInstalled("mail", "Contoso Mail", "Contoso Ltd.", mail);

        // The removed program's entry points at the publisher folder itself.
        var subject = Notes with { InstallLocation = shared };

        Assert.Empty(Scan(tree, subject));
    }

    [Fact]
    public void A_folder_a_still_installed_program_lives_in_is_never_offered_as_a_leftover()
    {
        using var tree = new LeftoverTree();
        var install = tree.Folder("Program Files", "Contoso Notes");
        tree.StillInstalled("notes64", "Contoso Notes Suite", "Contoso Ltd.", install);

        Assert.Empty(Scan(tree, Notes with { InstallLocation = install }));
        Assert.Empty(Scan(tree, Notes));
    }

    [Fact]
    public void A_substring_look_alike_is_not_offered()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes Pro");
        tree.Folder("AppData", "Roaming", "Notes");
        tree.Folder("AppData", "Local", "ContosoNotesPlus");
        tree.Folder("Program Files", "Contoso", "Contoso Notes Pro");
        tree.Folder("ProgramData", "Contoso Notes Pro");

        Assert.Empty(Scan(tree, Notes));
    }

    [Fact]
    public void The_same_program_still_listed_means_nothing_is_left_behind()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes");
        tree.StillInstalled("notes32", "Contoso Notes", "Contoso Ltd.");

        Assert.Empty(Scan(tree, Notes));
    }

    [Theory]
    [InlineData("App")]
    [InlineData("Data")]
    [InlineData("Microsoft")]
    [InlineData("QQ")]
    public void A_name_too_short_or_too_generic_to_identify_a_program_matches_nothing(string name)
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", name);
        tree.Folder("ProgramData", name);

        Assert.Empty(Scan(tree, new LeftoverSubject(LeftoverTree.RemovedKey, name, "Contoso", string.Empty)));
    }

    [Fact]
    public void A_store_app_is_never_scanned()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes");

        Assert.Empty(Scan(tree, Notes with { Key = "appx:Contoso.Notes_1.0_x64__abc" }));
    }

    [Fact]
    public void Protected_and_root_folders_are_never_offered_even_when_the_entry_names_them()
    {
        using var tree = new LeftoverTree();
        foreach (var place in new[] { tree.ProgramFiles, tree.AppData, tree.ProgramData, tree.Documents, tree.Desktop, tree.Root,
                     Path.GetPathRoot(tree.Root)!, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
        {
            Assert.Empty(Scan(tree, Notes with { InstallLocation = place }));
        }
    }

    [Fact]
    public void A_folder_that_holds_a_protected_folder_is_never_offered()
    {
        using var tree = new LeftoverTree();
        var holder = tree.Folder("Program Files", "Contoso Notes");
        var roots = tree.Roots with { ProtectedFolders = [Path.Combine(holder, "Keep")] };
        Directory.CreateDirectory(Path.Combine(holder, "Keep"));

        var scanner = new LeftoverScanner(roots, tree.Registry);
        var items = scanner.Scan(Notes with { InstallLocation = holder }, tree.Claims(), null, CancellationToken.None);

        Assert.Empty(items);
    }

    [Fact]
    public void A_junction_is_not_followed_and_not_offered()
    {
        using var tree = new LeftoverTree();
        var target = tree.Folder("Elsewhere", "Contoso Notes");
        var link = Path.Combine(tree.AppData, "Contoso Notes");
        TestLinks.CreateJunction(link, target);
        try
        {
            Assert.Empty(Scan(tree, Notes));
            Assert.Empty(Scan(tree, Notes with { InstallLocation = link }));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void A_registry_key_below_publisher_and_program_is_found_and_preselected_but_never_the_publisher_key()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Notes", "Setting", "1");
        tree.Registry.Set(@"HKCU\Software\Contoso\Contoso Mail", "Setting", "1");
        tree.Registry.Set(@"HKLM\Software\Contoso Ltd\ContosoNotes", "Setting", "1");
        tree.Registry.Set(@"HKLM\Software\WOW6432Node\Contoso\Contoso Notes", "Setting", "1");

        var keys = Scan(tree, Notes).Where(i => i.Kind == LeftoverKind.RegistryKey).ToList();

        Assert.Equal(
            [@"HKCU\Software\Contoso\Contoso Notes", @"HKLM\Software\Contoso Ltd\ContosoNotes", @"HKLM\Software\WOW6432Node\Contoso\Contoso Notes"],
            keys.Select(k => k.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.All(keys, k => Assert.True(k.Preselected));
        Assert.Equal(LeftoverLane.User, keys.Single(k => k.Path.StartsWith("HKCU")).Lane);
        Assert.All(keys.Where(k => k.Path.StartsWith("HKLM")), k => Assert.Equal(LeftoverLane.Machine, k.Lane));
        Assert.DoesNotContain(keys, k => k.Path.EndsWith("Contoso") || k.Path.EndsWith("Contoso Mail"));
    }

    [Fact]
    public void A_registry_key_is_never_offered_for_a_generic_publisher_or_a_name_without_a_publisher_match()
    {
        using var tree = new LeftoverTree();
        tree.Registry.Set(@"HKLM\Software\Microsoft\Contoso Notes", "Setting", "1");
        tree.Registry.Set(@"HKCU\Software\Contoso Notes", "Setting", "1");
        tree.Registry.Set(@"HKCU\Software\Fabrikam\Contoso Notes", "Setting", "1");

        Assert.DoesNotContain(Scan(tree, Notes), i => i.Kind == LeftoverKind.RegistryKey);
        Assert.DoesNotContain(Scan(tree, Notes with { Publisher = "Microsoft Corporation" }), i => i.Kind == LeftoverKind.RegistryKey);
        Assert.DoesNotContain(Scan(tree, Notes with { Publisher = string.Empty }), i => i.Kind == LeftoverKind.RegistryKey);
    }

    [Fact]
    public void A_shortcut_whose_target_is_gone_is_found()
    {
        using var tree = new LeftoverTree();
        var gone = Path.Combine(tree.ProgramFiles, "Contoso Notes", "notes.exe");
        var shortcut = tree.Shortcut(tree.UserStartMenu, "Contoso Notes", gone);
        tree.Shortcut(tree.Desktop, "Contoso Notes Pro", gone);

        var item = Assert.Single(Scan(tree, Notes));

        Assert.Equal(LeftoverKind.Shortcut, item.Kind);
        Assert.Equal(shortcut, item.Path);
        Assert.False(item.Preselected);
        Assert.Equal(LeftoverLane.User, item.Lane);
    }

    [Fact]
    public void A_shortcut_in_a_start_menu_folder_named_like_the_program_is_found_in_the_machine_lane()
    {
        using var tree = new LeftoverTree();
        var shortcut = tree.Shortcut(
            Path.Combine(tree.MachineStartMenu, "Contoso Notes"), "Uninstall", Path.Combine(tree.ProgramFiles, "Contoso Notes", "unins.exe"));

        var item = Assert.Single(Scan(tree, Notes));

        Assert.Equal(shortcut, item.Path);
        Assert.Equal(LeftoverLane.Machine, item.Lane);
    }

    [Fact]
    public void A_shortcut_whose_target_still_exists_or_has_none_is_not_offered()
    {
        using var tree = new LeftoverTree();
        var alive = tree.Folder("Tools");
        var exe = Path.Combine(alive, "notes.exe");
        File.WriteAllText(exe, "x");
        tree.Shortcut(tree.UserStartMenu, "Contoso Notes", exe);
        tree.Shortcut(tree.Desktop, "Contoso Notes", null);
        tree.Shortcut(tree.MachineStartMenu, "Contoso Notes", @"\\server\share\notes.exe");
        tree.Shortcut(Path.Combine(tree.UserStartMenu, "Contoso Notes"), "Docs", @"relative\notes.exe");

        Assert.Empty(Scan(tree, Notes));
    }

    [Fact]
    public void A_shortcut_into_the_install_folder_is_preselected_whatever_it_is_called()
    {
        using var tree = new LeftoverTree();
        var install = Path.Combine(tree.ProgramFiles, "Notes Tool");
        var subject = Notes with { InstallLocation = install };
        var shortcut = tree.Shortcut(tree.UserStartMenu, "My editor", Path.Combine(install, "bin", "edit.exe"));
        tree.Shortcut(tree.UserStartMenu, "Other thing", Path.Combine(tree.ProgramFiles, "Elsewhere", "x.exe"));

        var item = Assert.Single(Scan(tree, subject));

        Assert.Equal(shortcut, item.Path);
        Assert.True(item.Preselected);
    }

    [Fact]
    public void A_shortcut_whose_target_a_still_installed_program_owns_is_not_offered()
    {
        using var tree = new LeftoverTree();
        var shared = Path.Combine(tree.ProgramFiles, "Suite");
        tree.StillInstalled("suite", "Suite", "Contoso", shared);
        tree.Shortcut(tree.UserStartMenu, "Contoso Notes", Path.Combine(shared, "notes.exe"));

        Assert.Empty(Scan(tree, Notes));
    }

    [Fact]
    public void The_lane_filter_keeps_only_that_lane()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes");
        tree.Folder("Program Files", "Contoso", "Contoso Notes");

        Assert.Equal(LeftoverLane.User, Assert.Single(Scan(tree, Notes, LeftoverLane.User)).Lane);
        Assert.Equal(LeftoverLane.Machine, Assert.Single(Scan(tree, Notes, LeftoverLane.Machine)).Lane);
    }

    [Fact]
    public void The_result_lists_the_certain_matches_first()
    {
        using var tree = new LeftoverTree();
        tree.Folder("AppData", "Roaming", "Contoso Notes");
        tree.Folder("Program Files", "Contoso", "Contoso Notes");

        var items = Scan(tree, Notes);

        Assert.True(items[0].Preselected);
        Assert.False(items[^1].Preselected);
    }
}
