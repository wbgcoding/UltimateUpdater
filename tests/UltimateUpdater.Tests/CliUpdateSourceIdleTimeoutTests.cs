using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The idle-timeout mechanism ("a source is judged by silence, not by total runtime") existed since this
/// fix, but no real source actually passed it in. This is the wiring test for the scan helper every CLI
/// source shares (<see cref="CliUpdateSource.RunAsync"/> via
/// <see cref="CliUpdateSource.ScanIdleTimeout"/>), same shape as <c>TestCliSource</c> in
/// <c>SecurityRelevanceTests</c>.
/// </summary>
public class CliUpdateSourceIdleTimeoutTests
{
    /// <summary>`ping -n 5 127.0.0.1` prints nothing at all until it finishes, about four seconds later;
    /// a 1 second idle timeout has to cut it off long before that.</summary>
    [Fact]
    public async Task A_silent_tool_is_cut_off_by_the_sources_own_idle_timeout()
    {
        var source = new SilentTestSource(new LoggingService());

        async Task Drain()
        {
            await foreach (var _ in source.ScanAsync(CancellationToken.None))
            {
            }
        }

        await Assert.ThrowsAsync<ProcessIdleTimeoutException>(Drain);
    }

    private sealed class SilentTestSource(LoggingService logging) : CliUpdateSource(logging)
    {
        public override string Id => "test-idle";

        public override UpdateCategory Category => UpdateCategory.Software;

        protected override string Executable => "cmd";

        protected override TimeSpan ScanIdleTimeout => TimeSpan.FromSeconds(1);

        protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
        {
            await RunAsync(["/c", "ping -n 5 127.0.0.1 >nul"], cancellationToken);
            return [];
        }

        protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => [];
    }
}
