using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// A source that overrides <see cref="CliUpdateSource.ExecutableFor"/> must get that executable for every
/// command it runs for an item, not only for the install.
/// </summary>
public class CliUpdateSourceExecutableForTests
{
    private static readonly UpdateItem Item = new()
    {
        Id = "pkg",
        SourceId = "test-exe",
        Category = UpdateCategory.Software,
        Name = "pkg",
        InstalledVersion = "1.0",
        AvailableVersion = "2.0",
    };

    [Fact]
    public async Task Uninstall_runs_through_the_per_item_executable()
    {
        var source = new RoutedSource(new LoggingService());

        // The default Executable does not exist; only the per-item one (cmd) can succeed.
        await source.UninstallAsync(Item, new Progress<InstallProgress>(), CancellationToken.None);
    }

    [Fact]
    public async Task The_version_recheck_runs_through_the_per_item_executable()
    {
        var source = new RoutedSource(new LoggingService());

        Assert.Equal("7.7", await source.GetInstalledVersionAsync(Item, CancellationToken.None));
    }

    private sealed class RoutedSource(LoggingService logging) : CliUpdateSource(logging)
    {
        public override string Id => "test-exe";

        public override UpdateCategory Category => UpdateCategory.Software;

        protected override string Executable => "uu-no-such-tool-a";

        protected override string ExecutableFor(UpdateItem item) => "cmd";

        protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["/c", "exit 0"];

        protected override IReadOnlyList<string>? VersionCheckArguments(string id) => ["/c", "echo 7.7"];

        protected override string? ParseInstalledVersion(string stdout, string id) => stdout.Trim();

        protected override Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UpdateItem>>([]);

        protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => [];
    }
}
