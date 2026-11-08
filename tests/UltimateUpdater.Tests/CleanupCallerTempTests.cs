using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Over-the-shoulder elevation: the elevated worker runs as another account, so the signed-in user's Temp is
/// not its own and would be listed (and cleaned with admin rights) as an "other user's" Temp. The unelevated
/// app sends its Temp folders in the command and the worker leaves them out. Driven from the command to the
/// catalogue, with a fake profiles folder.
/// </summary>
public class CleanupCallerTempTests : IDisposable
{
    private readonly string _usersRoot = Path.Combine(Path.GetTempPath(), "wu-users-" + Guid.NewGuid());

    private string ProfileTemp(string profile)
    {
        var temp = Path.Combine(_usersRoot, profile, "AppData", "Local", "Temp");
        Directory.CreateDirectory(temp);
        return temp;
    }

    private static IReadOnlyList<string> AllUsersTemp(CleanupCommand command, Action<string>? log, string usersRoot)
    {
        command.TargetIds = ["all-users-temp"];
        var targets = CleanupWorker.ResolveElevatedTargets(command, log, usersRoot);
        return Assert.Single(targets).Directories;
    }

    [Fact]
    public void The_callers_temp_is_left_out_of_all_users_temp_when_the_profiles_differ()
    {
        var callerTemp = ProfileTemp("signedin");
        var otherTemp = ProfileTemp("someoneelse");

        var directories = AllUsersTemp(new CleanupCommand { CallerTempPaths = [callerTemp] }, null, _usersRoot);

        Assert.Equal([otherTemp], directories);
    }

    [Fact]
    public void Without_the_callers_temp_both_profiles_are_listed()
    {
        var callerTemp = ProfileTemp("signedin");
        var otherTemp = ProfileTemp("someoneelse");

        var directories = AllUsersTemp(new CleanupCommand(), null, _usersRoot);

        Assert.Equal(
            new[] { callerTemp, otherTemp }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            directories.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_callers_temp_is_matched_by_folder_identity_not_by_spelling()
    {
        var callerTemp = ProfileTemp("signedin");
        var otherTemp = ProfileTemp("someoneelse");

        var directories = AllUsersTemp(
            new CleanupCommand { CallerTempPaths = [callerTemp.ToUpperInvariant() + Path.DirectorySeparatorChar] },
            null, _usersRoot);

        Assert.Equal([otherTemp], directories);
    }

    [Fact]
    public void A_caller_path_that_cannot_be_read_only_shrinks_the_list_and_is_logged()
    {
        ProfileTemp("signedin");
        ProfileTemp("someoneelse");
        var warnings = new List<string>();

        var directories = AllUsersTemp(
            new CleanupCommand { CallerTempPaths = [Path.Combine(_usersRoot, "no-such-temp")] },
            warnings.Add, _usersRoot);

        Assert.Empty(directories);
        Assert.Contains(warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unrelated_caller_path_never_adds_or_removes_a_profile()
    {
        var callerTemp = ProfileTemp("signedin");
        var otherTemp = ProfileTemp("someoneelse");
        var unrelated = Path.Combine(_usersRoot, "unrelated");
        Directory.CreateDirectory(unrelated);

        var directories = AllUsersTemp(new CleanupCommand { CallerTempPaths = [unrelated] }, null, _usersRoot);

        Assert.Equal(
            new[] { callerTemp, otherTemp }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            directories.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_apps_own_temp_paths_are_the_redirected_and_the_profile_temp()
    {
        var own = CleanupCatalog.OwnTempPaths();

        Assert.Contains(own, p => string.Equals(
            p, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase));
        Assert.All(own, p => Assert.True(Path.IsPathFullyQualified(p)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_usersRoot, recursive: true); } catch { }
    }
}
