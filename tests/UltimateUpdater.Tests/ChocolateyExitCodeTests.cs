using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

public class ChocolateyExitCodeTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1605, true)]
    [InlineData(1614, true)]
    [InlineData(1641, true)]
    [InlineData(3010, true)]
    [InlineData(1, false)]
    [InlineData(1603, false)]
    [InlineData(-1, false)]
    public void Only_the_documented_codes_count_as_a_finished_run(int exitCode, bool expected) =>
        Assert.Equal(expected, ChocolateySource.IsSuccessExitCode(exitCode));

    [Theory]
    [InlineData(3010, true)]
    [InlineData(1641, true)]
    [InlineData(0, false)]
    [InlineData(1605, false)]
    [InlineData(1614, false)]
    public void A_restart_is_reported_for_exactly_the_two_restart_codes(int exitCode, bool expected) =>
        Assert.Equal(expected, ChocolateySource.IsRebootExitCode(exitCode));

    /// <summary>The shared install path has to carry a source's restart verdict into the final progress report.</summary>
    [Theory]
    [InlineData(3010, true)]
    [InlineData(0, false)]
    public async Task The_final_progress_report_carries_the_sources_restart_verdict(int exitCode, bool expected)
    {
        var source = new ExitCodeSource(new LoggingService(), exitCode);
        var reports = new List<InstallProgress>();

        await source.InstallAsync(
            new UpdateItem { Id = "pkg", Name = "Pkg", SourceId = source.Id, Category = UpdateCategory.Software, InstalledVersion = "1", AvailableVersion = "2" },
            new Progress<InstallProgress>(reports.Add), CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(expected, reports[^1].RebootRequired);
    }

    private sealed class ExitCodeSource(LoggingService logging, int exitCode) : CliUpdateSource(logging)
    {
        public override string Id => "test-exit";

        public override UpdateCategory Category => UpdateCategory.Software;

        protected override string Executable => "cmd";

        protected override Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UpdateItem>>([]);

        protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["/c", $"exit {exitCode}"];

        protected override bool IsInstallSuccess(ProcessResult result) => result.ExitCode is 0 or 3010;

        protected override bool IsRebootRequired(ProcessResult result) => result.ExitCode == 3010;
    }
}
