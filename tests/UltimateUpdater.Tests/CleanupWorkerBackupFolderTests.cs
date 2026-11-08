using System.IO;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The registry backup folder arrives over the pipe from the unelevated side; the elevated worker
/// may write and prune only in a folder an ordinary user can write anyway, or its own default.</summary>
public class CleanupWorkerBackupFolderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wu-backupfolder-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class RecordingService(LoggingService logging) : CleanupService(logging)
    {
        public RegistryCleanupSettings? Settings;

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
            Settings = registrySettings;
            return Task.FromResult(new CleanupOutcome(0, 0, 0));
        }
    }

    private static async Task<string> FolderSeenByTheServiceAsync(string requested, Func<string, bool> isTrusted)
    {
        var logging = new LoggingService();
        var service = new RecordingService(logging);
        var command = new CleanupCommand
        {
            Command = "clean",
            Language = "en",
            TargetIds = ["old-python-versions"],
            RegistryBackupFolder = requested,
        };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };
        await CleanupWorker.RunCommandAsync(
            command, writer, "job-1", logging, CancellationToken.None, new PauseGate(), service, isTrusted);

        return service.Settings!.BackupFolder;
    }

    [Fact]
    public async Task A_folder_only_administrators_can_change_is_replaced_by_the_default()
    {
        var adminOnly = Path.Combine(_root, "admin-only", "backups");
        Directory.CreateDirectory(Path.Combine(_root, "admin-only"));

        var folder = await FolderSeenByTheServiceAsync(adminOnly, _ => true);

        Assert.Equal(RegistryCleanupSettings.DefaultBackupFolder, folder);
    }

    [Fact]
    public async Task A_folder_the_user_can_write_anyway_is_used()
    {
        var userFolder = Path.Combine(_root, "mine");
        Directory.CreateDirectory(userFolder);

        var folder = await FolderSeenByTheServiceAsync(userFolder, _ => false);

        Assert.Equal(userFolder, folder);
    }

    [Fact]
    public async Task A_not_yet_created_folder_is_judged_by_its_nearest_existing_parent()
    {
        var asked = new List<string>();
        var future = Path.Combine(_root, "new", "deeper");

        var folder = await FolderSeenByTheServiceAsync(future, path => { asked.Add(path); return false; });

        Assert.Equal(future, folder);
        Assert.Equal([_root], asked);
    }

    [Fact]
    public void The_workers_own_default_needs_no_trust_question()
    {
        var logging = new LoggingService();
        var fallback = Path.Combine(_root, "default");

        var chosen = CleanupWorker.ChooseRegistryBackupFolder(
            fallback + Path.DirectorySeparatorChar, fallback, logging, _ => throw new InvalidOperationException("asked"));

        Assert.Equal(fallback, chosen);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\backups")]
    [InlineData("\\\\server\\share\\backups")]
    public void An_empty_relative_or_unreachable_folder_falls_back(string requested)
    {
        var fallback = Path.Combine(_root, "default");

        var chosen = CleanupWorker.ChooseRegistryBackupFolder(requested, fallback, new LoggingService(), _ => false);

        Assert.Equal(fallback, chosen);
    }
}
