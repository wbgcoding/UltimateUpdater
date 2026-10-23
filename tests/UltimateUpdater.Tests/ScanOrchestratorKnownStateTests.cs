using System.Runtime.CompilerServices;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// <see cref="ScanOrchestrator.IsKnownNotInstalled"/> is what a caller outside the scan itself
/// (the settings window, opened long after the last scan finished) reads to decide whether a source's tool
/// is on this machine, without needing to have been subscribed to <see
/// cref="ScanOrchestrator.ProviderStatusChanged"/> at the time.
/// </summary>
public class ScanOrchestratorKnownStateTests
{
    [Fact]
    public async Task A_source_never_scanned_is_not_known_to_be_missing()
    {
        var orchestrator = new ScanOrchestrator([new FakeSource("never-scanned", available: true)], new LoggingService());

        Assert.False(orchestrator.IsKnownNotInstalled("never-scanned"));
    }

    [Fact]
    public async Task An_absent_tool_is_known_not_installed_after_a_scan()
    {
        var orchestrator = new ScanOrchestrator([new FakeSource("absent", available: false)], new LoggingService());

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.True(orchestrator.IsKnownNotInstalled("absent"));
    }

    [Fact]
    public async Task A_present_tool_is_not_known_not_installed_after_a_scan()
    {
        var orchestrator = new ScanOrchestrator([new FakeSource("present", available: true)], new LoggingService());

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.False(orchestrator.IsKnownNotInstalled("present"));
    }

    private sealed class FakeSource(string id, bool available) : IUpdateSource
    {
        public string Id => id;

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
