using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class CleanupPerDirectoryPassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-per-dir-" + Guid.NewGuid());

    public void Dispose()
    {
        CleanupService.EnumerateFilesHookForTest.Value = null;
        CleanupService.BeforeFileDeleteForTest.Value = null;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static CleanupTarget TargetFor(params string[] directories) =>
        new("test-target", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x")
        {
            Directories = directories,
        };

    private static Task<CleanupOutcome> Clean(CleanupTarget target, IProgress<CleanupProgress>? progress = null) =>
        new CleanupService(new LoggingService()).CleanAsync(target, progress, CancellationToken.None);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufferSize);

    [Fact]
    public async Task One_directory_named_twice_is_listed_and_counted_once()
    {
        var dirA = Path.Combine(_root, "A");
        Directory.CreateDirectory(dirA);
        File.WriteAllText(Path.Combine(dirA, "one.tmp"), "1");
        File.WriteAllText(Path.Combine(dirA, "two.tmp"), "2");

        var walked = new List<string>();
        CleanupService.EnumerateFilesHookForTest.Value = walked.Add;

        var outcome = await Clean(TargetFor(dirA, dirA.ToUpperInvariant()));

        Assert.Single(walked);
        Assert.Equal(2, outcome.DeletedFiles);
        Assert.Equal(0, outcome.SkippedFiles);
    }

    [Fact]
    public async Task The_short_name_and_the_long_name_of_one_directory_are_listed_once()
    {
        var dirA = Path.Combine(_root, "A long directory name");
        Directory.CreateDirectory(dirA);
        File.WriteAllText(Path.Combine(dirA, "one.tmp"), "1");

        var shortName = new StringBuilder(520);
        if (GetShortPathNameW(dirA, shortName, (uint)shortName.Capacity) == 0
            || string.Equals(shortName.ToString(), dirA, StringComparison.OrdinalIgnoreCase))
        {
            return; // 8.3 names are switched off on this volume: nothing to compare
        }

        var walked = new List<string>();
        CleanupService.EnumerateFilesHookForTest.Value = walked.Add;

        var outcome = await Clean(TargetFor(dirA, shortName.ToString()));

        Assert.Single(walked);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(0, outcome.SkippedFiles);
    }

    [Fact]
    public async Task A_file_that_vanished_before_its_delete_is_neither_deleted_nor_skipped()
    {
        var dirA = Path.Combine(_root, "A");
        Directory.CreateDirectory(dirA);
        var gone = Path.Combine(dirA, "gone.tmp");
        File.WriteAllText(gone, "1");

        CleanupService.BeforeFileDeleteForTest.Value = file =>
        {
            if (file.EndsWith("gone.tmp", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        };

        var outcome = await Clean(TargetFor(dirA));

        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(0, outcome.SkippedFiles);
    }

    [Fact]
    public async Task A_directory_is_locked_only_while_its_own_pass_runs()
    {
        var dirA = Path.Combine(_root, "A");
        var dirB = Path.Combine(_root, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        File.WriteAllText(Path.Combine(dirA, "a.tmp"), "1");
        File.WriteAllText(Path.Combine(dirB, "b.tmp"), "2");

        bool? renameOfListedFailed = null;
        bool? renameOfOtherSucceeded = null;
        bool? renameOfFinishedSucceeded = null;
        CleanupService.EnumerateFilesHookForTest.Value = listed =>
        {
            if (listed.EndsWith("B", StringComparison.Ordinal))
            {
                // A's pass is over: its lock must be gone before B's pass starts.
                try { Directory.Move(dirA, dirA + "-moved"); Directory.Move(dirA + "-moved", dirA); renameOfFinishedSucceeded = true; }
                catch (IOException) { renameOfFinishedSucceeded = false; }
                catch (UnauthorizedAccessException) { renameOfFinishedSucceeded = false; }
                return;
            }

            if (!listed.EndsWith("A", StringComparison.Ordinal))
                return;

            // The directory being listed is locked ...
            try { Directory.Move(dirA, dirA + "-moved"); renameOfListedFailed = false; Directory.Move(dirA + "-moved", dirA); }
            catch (IOException) { renameOfListedFailed = true; }
            catch (UnauthorizedAccessException) { renameOfListedFailed = true; }

            // ... and the one whose turn has not come is not yet.
            try { Directory.Move(dirB, dirB + "-moved"); Directory.Move(dirB + "-moved", dirB); renameOfOtherSucceeded = true; }
            catch (IOException) { renameOfOtherSucceeded = false; }
            catch (UnauthorizedAccessException) { renameOfOtherSucceeded = false; }
        };

        var outcome = await Clean(TargetFor(dirA, dirB));

        Assert.Equal(2, outcome.DeletedFiles);
        Assert.True(renameOfListedFailed, "the listed directory must be locked while it is listed");
        Assert.True(renameOfOtherSucceeded, "a directory must not be locked before its own pass");
        Assert.True(renameOfFinishedSucceeded, "a directory must be released when its own pass is over");
    }

    [Fact]
    public async Task Progress_is_the_directory_share_and_never_moves_backward()
    {
        var dirA = Path.Combine(_root, "A");
        var dirB = Path.Combine(_root, "B");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(dirA, $"a{i}.tmp"), "1");
        for (var i = 0; i < 10; i++)
            File.WriteAllText(Path.Combine(dirB, $"b{i}.tmp"), "1");

        var reports = new List<double>();
        var progress = new SynchronousProgress<CleanupProgress>(p => reports.Add(p.Percent));

        await Clean(TargetFor(dirA, dirB), progress);

        Assert.NotEmpty(reports);
        Assert.Equal(reports.OrderBy(p => p), reports);
        // Directory A holds two thirds of the files but only half of the bar: after its last file the bar
        // stands at 50, and directory B's first report starts above that.
        Assert.Contains(reports, p => Math.Abs(p - 50.0) < 0.001);
        Assert.All(reports, p => Assert.InRange(p, 0.0, 100.0));
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task A_refused_file_is_counted_apart_from_a_file_in_use()
    {
        var dirA = Path.Combine(_root, "A");
        Directory.CreateDirectory(dirA);
        var busy = Path.Combine(dirA, "busy.tmp");
        File.WriteAllText(busy, "12345");
        File.WriteAllText(Path.Combine(dirA, "denied.tmp"), "1");
        File.WriteAllText(Path.Combine(dirA, "free.tmp"), "1");

        CleanupService.BeforeFileDeleteForTest.Value = file =>
        {
            if (file.EndsWith("denied.tmp", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Access is denied.");
        };

        CleanupOutcome outcome;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
            outcome = await Clean(TargetFor(dirA));

        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(2, outcome.SkippedFiles);
        Assert.Equal(1, outcome.ProtectedFiles);
    }

    [Fact]
    public async Task Folders_that_stay_are_logged_as_one_line_not_one_each()
    {
        var dirA = Path.Combine(_root, "A");
        var deepest = Path.Combine(dirA, "a", "b", "c", "d");
        Directory.CreateDirectory(deepest);
        var held = Path.Combine(deepest, "held.tmp");
        File.WriteAllText(held, "1");

        var lines = new List<string>();
        var logging = new LoggingService(line => { lock (lines) lines.Add(line); });
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
            await new CleanupService(logging).CleanAsync(TargetFor(dirA), null, CancellationToken.None);
        logging.Flush();

        List<string> kept;
        lock (lines)
            kept = lines.Where(l => l.Contains("not removed", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(kept);
        Assert.Contains("4 folder(s)", line, StringComparison.Ordinal);
    }
}
