using System.IO;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Over-the-shoulder elevation runs the worker as another administrator, whose HKCU is not the
/// signed-in user's: per-user registry targets must then not run at all.
/// </summary>
public class RegistryUserHiveGuardTests
{
    private const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string AdminSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";

    private static CleanupTarget Registry(string id) =>
        RegistryCleanupCatalog.Build().Single(t => t.Id == id);

    private static CleanupTarget SharedDllsOnly() => new(
        "test-shared", "x", "y", CleanupKind.RegistryCleanup, DefaultEnabled: false,
        RiskLevel: CleanupRiskLevel.Risky, ConsequenceKey: "x", NeedsElevation: true)
    {
        RegistryCategories = [RegistryCleanupCategory.SharedDlls],
    };

    [Theory]
    [InlineData("registry-orphaned-references")]
    [InlineData("registry-orphaned-uninstall")]
    public void A_per_user_registry_target_is_skipped_for_another_account(string id)
    {
        Assert.NotNull(CleanupWorker.UserHiveMismatchReason(Registry(id), UserSid, AdminSid));
    }

    [Theory]
    [InlineData("registry-orphaned-references")]
    [InlineData("registry-orphaned-uninstall")]
    public void A_per_user_registry_target_runs_for_the_same_account(string id)
    {
        Assert.Null(CleanupWorker.UserHiveMismatchReason(Registry(id), UserSid, UserSid.ToLowerInvariant()));
    }

    [Theory]
    [InlineData(null, UserSid)]
    [InlineData(UserSid, null)]
    [InlineData("", UserSid)]
    [InlineData(UserSid, " ")]
    public void A_missing_sid_on_either_side_counts_as_another_account(string? caller, string? worker)
    {
        Assert.NotNull(CleanupWorker.UserHiveMismatchReason(Registry("registry-orphaned-uninstall"), caller, worker));
    }

    [Fact]
    public void A_machine_wide_only_target_and_file_targets_are_never_held_back()
    {
        Assert.Null(CleanupWorker.UserHiveMismatchReason(SharedDllsOnly(), UserSid, AdminSid));

        var directories = new CleanupTarget(
            "test-dirs", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x");
        Assert.Null(CleanupWorker.UserHiveMismatchReason(directories, UserSid, AdminSid));
    }

    private sealed class RecordingService(LoggingService logging) : CleanupService(logging)
    {
        public int Cleaned;
        public int Scanned;

        public override Task<CleanupOutcome> CleanAsync(
            CleanupTarget target, IProgress<CleanupProgress>? progress, CancellationToken cancellationToken,
            PauseGate? pause = null, RegistryCleanupSettings? registrySettings = null,
            IReadOnlyCollection<string>? selectedLeftoverPaths = null,
            IReadOnlyList<RegistryFinding>? registryFindings = null,
            IReadOnlyCollection<string>? excludedEntryPaths = null,
            bool recycleInsteadOfDelete = false, int recycleBinMinAgeDays = 0,
            IReadOnlyCollection<string>? selectedDriverDeviceInstanceIds = null,
            IReadOnlyCollection<string>? selectedDriverPackageNames = null,
            IReadOnlyCollection<string>? selectedPythonVersionPaths = null,
            IReadOnlyCollection<string>? confirmedEntryPaths = null)
        {
            Cleaned++;
            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }

        public override Task<CleanupScanResult> ScanAsync(
            CleanupTarget target, CancellationToken cancellationToken, int recycleBinMinAgeDays = 0)
        {
            Scanned++;
            return Task.FromResult(new CleanupScanResult(0, 0, 0));
        }
    }

    private static async Task<(RecordingService Service, string Output)> RunAsync(string command, string? callerSid, string workerSid)
    {
        var logging = new LoggingService();
        var service = new RecordingService(logging);
        var request = new CleanupCommand
        {
            Command = command,
            Language = "en",
            TargetIds = ["registry-orphaned-uninstall"],
            CallerSid = callerSid,
        };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        await CleanupWorker.RunCommandAsync(
            request, writer, "job-1", logging, CancellationToken.None, new PauseGate(), service, workerSidOverride: workerSid);

        return (service, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task The_worker_does_not_clean_a_per_user_registry_target_for_another_account()
    {
        var (service, output) = await RunAsync("clean", UserSid, AdminSid);

        Assert.Equal(0, service.Cleaned);
        Assert.Contains("\"failed\":true", output.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_worker_does_not_scan_a_per_user_registry_target_for_another_account()
    {
        var (service, _) = await RunAsync("scan", UserSid, AdminSid);

        Assert.Equal(0, service.Scanned);
    }

    [Fact]
    public async Task The_worker_cleans_a_per_user_registry_target_for_the_same_account()
    {
        var (service, _) = await RunAsync("clean", UserSid, UserSid);

        Assert.Equal(1, service.Cleaned);
    }

    [Fact]
    public async Task A_command_without_a_caller_sid_does_not_clean_a_per_user_registry_target()
    {
        var (service, _) = await RunAsync("clean", null, UserSid);

        Assert.Equal(0, service.Cleaned);
    }
}
