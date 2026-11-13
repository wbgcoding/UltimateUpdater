using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The elevated worker must hand every selection it received over the pipe on to the service.</summary>
public class CleanupWorkerForwardingTests
{
    private sealed class RecordingService(LoggingService logging) : CleanupService(logging)
    {
        public IReadOnlyCollection<string>? PythonPaths;
        public IReadOnlyCollection<string>? DriverDevices;
        public IReadOnlyCollection<string>? DriverPackages;

        public override Task<CleanupOutcome> CleanAsync(
            CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
            PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
            IReadOnlyCollection<string>? selectedLeftoverPaths = null,
            IReadOnlyList<RegistryFinding>? registryFindings = null,
            IReadOnlyCollection<string>? excludedEntryPaths = null,
            bool recycleInsteadOfDelete = false, int recycleBinMinAgeDays = 0,
            IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
            IReadOnlyCollection<string>? selectedDriverPackageNames = null,
            IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
            IReadOnlyCollection<string>? confirmedEntryPaths = null)
        {
            PythonPaths = selectedPythonVersionPaths;
            DriverDevices = selectedDriverDeviceInstanceIds;
            DriverPackages = selectedDriverPackageNames;
            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }
    }

    private sealed class ThrowingService(LoggingService logging) : CleanupService(logging)
    {
        public override Task<CleanupOutcome> CleanAsync(
            CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
            PauseGate? pause = null, Models.Settings.RegistryCleanupSettings? registrySettings = null,
            IReadOnlyCollection<string>? selectedLeftoverPaths = null,
            IReadOnlyList<RegistryFinding>? registryFindings = null,
            IReadOnlyCollection<string>? excludedEntryPaths = null,
            bool recycleInsteadOfDelete = false, int recycleBinMinAgeDays = 0,
            IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
            IReadOnlyCollection<string>? selectedDriverPackageNames = null,
            IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
            IReadOnlyCollection<string>? confirmedEntryPaths = null) =>
            throw new InvalidOperationException("clean blew up");
    }

    /// <summary>A target that throws must reach the app as a failed result: with no result the app takes the
    /// target for one the helper dropped and cleans it again without rights.</summary>
    [Fact]
    public async Task A_target_that_throws_is_reported_as_failed_before_done()
    {
        var logging = new LoggingService();
        var command = new CleanupCommand
        {
            Command = "clean",
            Language = "en",
            TargetIds = ["old-python-versions"],
        };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        await CleanupWorker.RunCommandAsync(
            command, writer, "job-1", logging, CancellationToken.None, new PauseGate(), new ThrowingService(logging));

        var payloads = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement.GetProperty("payload"))
            .ToList();
        var types = payloads.Select(p => p.GetProperty("type").GetString()).ToList();

        var resultIndex = types.IndexOf("result");
        Assert.True(resultIndex >= 0, "no result message was sent");
        Assert.Equal("old-python-versions", payloads[resultIndex].GetProperty("id").GetString());
        Assert.True(payloads[resultIndex].GetProperty("failed").GetBoolean());
        Assert.True(resultIndex < types.LastIndexOf("done"));
    }

    [Fact]
    public async Task The_selected_python_version_paths_reach_the_service()
    {
        var logging = new LoggingService();
        var service = new RecordingService(logging);
        var command = new CleanupCommand
        {
            Command = "clean",
            Language = "en",
            TargetIds = ["old-python-versions"],
            SelectedPythonVersionPaths = [@"C:\Py\python.exe"],
            SelectedDriverPackageNames = ["oem1.inf"],
        };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        await CleanupWorker.RunCommandAsync(
            command, writer, "job-1", logging, CancellationToken.None, new PauseGate(), service);

        Assert.Equal([@"C:\Py\python.exe"], service.PythonPaths);
        Assert.Equal(["oem1.inf"], service.DriverPackages);
    }
}
