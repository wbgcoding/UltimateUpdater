using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The uninstaller's saved listing: roundtrip, damaged files, size cap, and the cheap recheck of cached entries.</summary>
public sealed class UninstallCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uu-uninstall-cache-" + Guid.NewGuid().ToString("N"));

    public UninstallCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string FilePath => Path.Combine(_dir, UninstallCache.FileName);

    private static InstalledProgramEntry Entry(string name, UninstallEntryKind kind = UninstallEntryKind.Program) => new()
    {
        Key = kind == UninstallEntryKind.Program ? FakeUninstallRegistry.Hklm64 + "\\" + name : "x:" + name,
        Kind = kind,
        Name = name,
        Publisher = "Acme",
        Version = "1.2.3",
        InstallDate = new DateOnly(2025, 4, 5),
        SizeBytes = 1234,
        InstallLocation = @"C:\Program Files\" + name,
        Scope = UninstallScope.User,
        UninstallString = @"C:\Program Files\" + name + @"\unins000.exe",
        MsiProductCode = "{00000000-0000-0000-0000-000000000001}",
        WingetId = "Acme." + name,
        PackageFullName = null,
        WindowsAllowsRemoval = false,
    };

    [Fact]
    public void A_saved_listing_reads_back_equal_with_its_time()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(1));
        var cache = new UninstallCache(FilePath, () => at);
        InstalledProgramEntry[] entries = [Entry("Alpha"), Entry("Beta", UninstallEntryKind.Package)];

        Assert.True(cache.Save(entries));
        var loaded = cache.Load();

        Assert.NotNull(loaded);
        Assert.Equal(at, loaded.SavedAt);
        Assert.Equal(entries, loaded.Entries);
    }

    [Fact]
    public void No_file_is_no_cache()
    {
        Assert.Null(new UninstallCache(FilePath).Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":1")]
    [InlineData("{\"version\":2,\"entries\":[]}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"entries\":[{\"name\":\"NoKey\"}]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"key\":\"  \",\"name\":\"Blank\"}]}")]
    [InlineData("{\"version\":1,\"entries\":[null]}")]
    public void A_damaged_or_foreign_file_is_ignored(string content)
    {
        File.WriteAllText(FilePath, content);

        Assert.Null(new UninstallCache(FilePath).Load());
    }

    [Fact]
    public void A_file_over_the_cap_is_ignored()
    {
        using (var stream = new FileStream(FilePath, FileMode.Create))
            stream.SetLength(UninstallCache.MaxBytes + 1);

        Assert.Null(new UninstallCache(FilePath).Load());
    }

    [Fact]
    public void A_listing_over_the_cap_is_not_written_and_leaves_the_old_file()
    {
        var cache = new UninstallCache(FilePath);
        Assert.True(cache.Save([Entry("Small")]));

        var huge = new InstalledProgramEntry { Key = "k", Name = "Huge", Publisher = new string('x', (int)UninstallCache.MaxBytes) };

        Assert.False(cache.Save([huge]));
        Assert.Equal("Small", Assert.Single(cache.Load()!.Entries).Name);
    }

    [Fact]
    public void Saving_into_a_folder_that_cannot_be_created_fails_quietly()
    {
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");

        Assert.False(new UninstallCache(Path.Combine(blocker, UninstallCache.FileName)).Save([Entry("A")]));
    }

    [Fact]
    public void A_second_save_replaces_the_first_and_leaves_no_temp_file()
    {
        var cache = new UninstallCache(FilePath);
        cache.Save([Entry("One")]);
        cache.Save([Entry("Two"), Entry("Three")]);

        Assert.Equal(2, cache.Load()!.Entries.Count);
        Assert.Equal([FilePath], Directory.GetFiles(_dir));
    }

    private static readonly string Windows = @"C:\Windows";

    [Fact]
    public void The_recheck_drops_a_program_whose_key_is_gone_and_keeps_the_rest()
    {
        var registry = new FakeUninstallRegistry().Program(FakeUninstallRegistry.Hklm64, "Alpha", "Alpha");
        var package = Entry("Pkg", UninstallEntryKind.Package);

        var kept = UninstallCacheRecheck.Keep([Entry("Alpha"), Entry("Gone"), package], registry, Windows, _ => true);

        Assert.Equal(["Alpha", "Pkg"], kept.Select(entry => entry.Name));
    }

    [Fact]
    public void An_unreadable_uninstall_root_keeps_the_entry()
    {
        var kept = UninstallCacheRecheck.Keep([Entry("Alpha")], new FakeUninstallRegistry(), Windows, _ => true);

        Assert.Single(kept);
    }

    [Fact]
    public void The_recheck_drops_a_store_app_that_is_no_longer_registered()
    {
        var registry = new FakeUninstallRegistry().Set(UninstallCacheRecheck.AppxRepositoryKey + @"\Here_1.0_x64__abc", "Path", "p");
        var here = Entry("Here", UninstallEntryKind.StoreApp) with { PackageFullName = "Here_1.0_x64__abc" };
        var gone = Entry("Gone", UninstallEntryKind.StoreApp) with { PackageFullName = "Gone_1.0_x64__abc" };

        var kept = UninstallCacheRecheck.Keep([here, gone], registry, Windows, _ => true);

        Assert.Equal("Here", Assert.Single(kept).Name);
    }

    [Fact]
    public void An_unreadable_package_list_keeps_every_store_app()
    {
        var app = Entry("App", UninstallEntryKind.StoreApp) with { PackageFullName = "App_1.0_x64__abc" };

        Assert.Single(UninstallCacheRecheck.Keep([app], new FakeUninstallRegistry(), Windows, _ => true));
    }

    [Fact]
    public void The_recheck_drops_a_driver_whose_inf_is_gone()
    {
        var present = Entry("Here", UninstallEntryKind.Driver) with { Key = "driver:oem12.inf" };
        var gone = Entry("Gone", UninstallEntryKind.Driver) with { Key = "driver:oem13.inf" };

        var kept = UninstallCacheRecheck.Keep(
            [present, gone], new FakeUninstallRegistry(), Windows, path => path.EndsWith("oem12.inf", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Here", Assert.Single(kept).Name);
    }

    [Fact]
    public void The_recheck_drops_an_update_the_servicing_stack_no_longer_lists()
    {
        const string Still = "Package_for_KB5031234~31bf3856ad364e35~amd64~~10.0.1.0";
        const string Gone = "Package_for_KB5031235~31bf3856ad364e35~amd64~~10.0.1.0";
        var registry = new FakeUninstallRegistry()
            .Set(WindowsUpdateDiscovery.CbsPackagesKey + "\\" + Still, "CurrentState", "112");
        var still = Entry("Still", UninstallEntryKind.WindowsUpdate) with { Key = "update:" + Still };
        var gone = Entry("Gone", UninstallEntryKind.WindowsUpdate) with { Key = "update:" + Gone };

        var kept = UninstallCacheRecheck.Keep([still, gone], registry, Windows, _ => true);

        Assert.Equal("Still", Assert.Single(kept).Name);
    }
}
