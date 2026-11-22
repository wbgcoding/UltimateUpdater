using System.IO;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels.Cleanup;
using Xunit;

namespace UltimateUpdater.Tests;

public sealed class AppDataLeftoverDiscoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-900);

    private readonly LeftoverTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>A folder with a data file and an executable, all of it last touched 900 days ago.</summary>
    private string OldFolder(string root, string name, bool withExecutable = true)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var files = new List<string> { Path.Combine(folder, "data.txt") };
        if (withExecutable)
            files.Add(Path.Combine(folder, name + ".exe"));

        foreach (var file in files)
        {
            File.WriteAllText(file, "x");
            File.SetCreationTimeUtc(file, LongAgo);
            File.SetLastWriteTimeUtc(file, LongAgo);
            File.SetLastAccessTimeUtc(file, LongAgo);
        }

        Directory.SetLastWriteTimeUtc(folder, LongAgo);
        return folder;
    }

    private AppDataLeftoverDiscovery.Verdict VerdictOf(string folder, LeftoverEvidence evidence) =>
        AppDataLeftoverDiscovery.Judge(_tree.Roots, evidence, Now, CancellationToken.None)
            .Single(v => string.Equals(v.Path, folder, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void An_old_folder_that_nothing_on_the_machine_explains_is_offered()
    {
        var orphan = OldFolder(_tree.AppData, "Zorblax Draftsman");
        var verdicts = AppDataLeftoverDiscovery.Judge(_tree.Roots, new LeftoverEvidence(), Now, CancellationToken.None);

        Assert.Contains(verdicts, v => v.Path == orphan && v.Reason is null);

        var offers = AppDataLeftoverDiscovery.Offers(verdicts, CancellationToken.None);
        var offer = Assert.Single(offers);
        Assert.Equal("Zorblax Draftsman", offer.DisplayName);
        Assert.True(offer.SizeBytes > 0);
    }

    [Fact]
    public void A_folder_named_like_an_installed_program_is_not_offered()
    {
        var folder = OldFolder(_tree.LocalAppData, "ContosoNotes");
        var evidence = new LeftoverEvidence();
        evidence.AddName("Contoso Notes");

        Assert.NotNull(VerdictOf(folder, evidence).Reason);
    }

    [Fact]
    public void A_folder_whose_name_differs_from_the_product_is_explained_by_the_executable_name()
    {
        // "TS3Client" next to a product called "TeamSpeak 3 Client": the exe stem is the link.
        var folder = OldFolder(_tree.AppData, "Ts3client");
        var evidence = new LeftoverEvidence();
        evidence.AddName("TeamSpeak 3 Client");
        evidence.AddName("ts3client_win64");

        Assert.NotNull(VerdictOf(folder, evidence).Reason);
    }

    [Fact]
    public void A_folder_a_running_service_or_task_lives_in_is_not_offered()
    {
        var publisher = OldFolder(_tree.ProgramFiles, "Fabrikam");
        var program = Path.Combine(publisher, "Agent");
        Directory.CreateDirectory(program);
        var evidence = new LeftoverEvidence();
        evidence.AddPath("\"" + Path.Combine(program, "agent.exe") + "\" --service");

        Assert.NotNull(VerdictOf(publisher, evidence).Reason);
    }

    [Fact]
    public void A_folder_below_a_folder_an_installed_program_owns_is_not_offered()
    {
        var holder = OldFolder(_tree.ProgramData, "Litware");
        var evidence = new LeftoverEvidence();
        evidence.AddPath(Path.Combine(_tree.ProgramData, "Litware", "Data"));

        Assert.NotNull(VerdictOf(holder, evidence).Reason);
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Packages")]
    [InlineData("Temp")]
    [InlineData("NVIDIA Corporation")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BattlEye")]
    [InlineData("MicrosoftEdgeX")]
    public void A_protected_folder_is_never_offered_however_old_it_is(string name)
    {
        var folder = OldFolder(_tree.LocalAppData, name);

        Assert.NotNull(VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void The_apps_own_data_folder_is_never_offered()
    {
        var own = OldFolder(_tree.AppData, "UltimateUpdater");

        Assert.NotNull(VerdictOf(own, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void A_folder_that_was_touched_recently_is_not_offered()
    {
        var folder = OldFolder(_tree.AppData, "Quuxbar Studio");
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");

        Assert.NotNull(VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void A_folder_with_a_recently_copied_executable_is_not_offered()
    {
        // A copy keeps the old write time but gets a new creation time.
        var folder = OldFolder(_tree.LocalAppData, "Plugh Portable");
        var exe = Path.Combine(folder, "Plugh Portable.exe");
        File.SetCreationTimeUtc(exe, DateTime.UtcNow.AddDays(-3));

        Assert.NotNull(VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void A_folder_whose_data_was_opened_recently_is_not_offered()
    {
        var folder = OldFolder(_tree.AppData, "Wibble Tools", withExecutable: false);
        File.SetLastAccessTimeUtc(Path.Combine(folder, "data.txt"), DateTime.UtcNow.AddDays(-20));

        Assert.NotNull(VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void An_empty_folder_is_not_offered()
    {
        var folder = Path.Combine(_tree.LocalAppData, "Hollow Thing");
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, LongAgo);

        Assert.Equal("empty", VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void A_folder_the_walk_cannot_see_in_full_is_not_offered()
    {
        var folder = OldFolder(_tree.AppData, "Deep Archive", withExecutable: false);
        var deep = Path.Combine(folder, "a", "b", "c", "d");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "x.txt"), "x");

        Assert.NotNull(VerdictOf(folder, new LeftoverEvidence()).Reason);
    }

    [Fact]
    public void A_junction_in_a_scanned_root_is_not_followed_or_offered()
    {
        var target = OldFolder(_tree.Documents, "Precious");
        var link = Path.Combine(_tree.AppData, "Xyzzy Link");
        LeftoverTestLinks.CreateJunction(link, target);

        var verdicts = AppDataLeftoverDiscovery.Judge(_tree.Roots, new LeftoverEvidence(), Now, CancellationToken.None);

        Assert.DoesNotContain(verdicts, v => string.Equals(v.Path, link, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_direct_children_of_the_roots_are_judged()
    {
        OldFolder(Path.Combine(_tree.AppData, "Zorblax"), "Nested Thing");
        var verdicts = AppDataLeftoverDiscovery.Judge(_tree.Roots, new LeftoverEvidence(), Now, CancellationToken.None);

        Assert.DoesNotContain(verdicts, v => v.Path.EndsWith("Nested Thing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_scanned_roots_themselves_and_the_Programs_folder_are_never_offered()
    {
        var programs = OldFolder(_tree.LocalAppData, "Programs");
        var verdicts = AppDataLeftoverDiscovery.Judge(_tree.Roots, new LeftoverEvidence(), Now, CancellationToken.None);

        Assert.NotNull(verdicts.Single(v => string.Equals(v.Path, programs, StringComparison.OrdinalIgnoreCase)).Reason);
    }

    [Fact]
    public void A_folder_is_explained_by_a_word_of_an_installed_product_name()
    {
        var folder = OldFolder(_tree.AppData, "Bitwarden");
        var evidence = new LeftoverEvidence();
        evidence.AddName("Bitwarden Desktop 2024");

        Assert.NotNull(VerdictOf(folder, evidence).Reason);
    }

    [Fact]
    public void The_cleanup_row_exists_once_off_by_default_risky_and_without_fixed_folders()
    {
        var row = Assert.Single(CleanupCatalog.Build(), t => t.Kind == CleanupKind.LeftoverInstalls);

        Assert.False(row.DefaultEnabled);
        Assert.Equal(CleanupRiskLevel.Risky, row.RiskLevel);
        Assert.Empty(row.Directories);
        Assert.Equal(CleanupGroupKey.Caches, CleanupGroups.For(row));
    }

    [Fact]
    public void No_leftover_candidate_starts_ticked()
    {
        var candidate = new LeftoverCandidateViewModel(new LeftoverCandidateDto { Path = @"C:\x", DisplayName = "x" });

        Assert.False(candidate.IsSelected);
    }
}
