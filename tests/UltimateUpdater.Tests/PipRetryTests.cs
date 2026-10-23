using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using UltimateUpdater.Services.Sources.PackageManagers;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// pip on a machine-wide interpreter needs admin to write its site-packages, so an unelevated upgrade
/// fails with a permission error. Rather than report it, the source retries the upgrade as a per-user
/// install, which is pip's own recommendation and needs no elevation.
/// </summary>
public class PipRetryTests
{
    private static ProcessResult Result(int exitCode, string stderr = "", string stdout = "") =>
        new(exitCode, stdout, stderr);

    [Theory]
    [InlineData("ERROR: Could not install packages due to an OSError: [WinError 5] Access is denied")]
    [InlineData("ERROR: Could not install packages due to an OSError: [Errno 13] Permission denied")]
    [InlineData("Consider using the `--user` option or check the permissions.")]
    public void A_permission_failure_asks_for_a_user_scope_retry(string stderr)
    {
        Assert.True(PipSource.NeedsUserScopeRetry(Result(1, stderr)));
    }

    [Fact]
    public void A_successful_install_is_never_retried()
    {
        Assert.False(PipSource.NeedsUserScopeRetry(Result(0, "Successfully installed requests-2.32.0")));
    }

    /// <summary>
    /// A locked file reads as "access denied" too, but the location is writable, retrying with --user
    /// would plant a shadowing second copy instead of updating the one in use. rapidocr hits exactly
    /// this through onnxruntime's native DLLs.
    /// </summary>
    [Theory]
    [InlineData("ERROR: Could not install packages due to an OSError: [WinError 32] The process cannot " +
        "access the file because it is being used by another process: 'onnxruntime.dll'")]
    [InlineData("PermissionError: [WinError 32] used by another process: 'onnxruntime_pybind11_state.pyd'")]
    public void A_locked_file_is_not_retried_with_user_scope(string stderr)
    {
        Assert.False(PipSource.NeedsUserScopeRetry(Result(1, stderr)));
    }

    [Fact]
    public void An_unrelated_failure_is_not_treated_as_a_permission_problem()
    {
        Assert.False(PipSource.NeedsUserScopeRetry(
            Result(1, "ERROR: Could not find a version that satisfies the requirement bogus")));
    }

    private static UpdateItem Item(string id) => new()
    {
        Id = id, SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = id,
    };

    [Theory]
    [InlineData("pip")]
    [InlineData("PIP")]
    [InlineData("Pip")]
    public void Pip_updating_itself_is_recognized_regardless_of_case(string id)
    {
        Assert.True(PipSource.IsSelfUpgrade(Item(id)));
    }

    [Theory]
    [InlineData("headroom-ai")]
    [InlineData("requests")]
    [InlineData("pip-tools")]
    public void An_ordinary_package_is_not_mistaken_for_pip_itself(string id)
    {
        Assert.False(PipSource.IsSelfUpgrade(Item(id)));
    }

    /// <summary>The exact wording pip prints when its own package is half-replaced by a prior self-upgrade
    /// gone wrong.</summary>
    [Fact]
    public void A_broken_pip_installation_is_recognized_from_its_traceback()
    {
        const string traceback =
            "Traceback (most recent call last): · File \"<frozen runpy>\", line 203, in _run_module_as_main " +
            "· from pip._internal.cli.main import main · ModuleNotFoundError: No module named 'pip' (exit code 1)";

        Assert.True(PipSource.LooksLikeBrokenPip(traceback));
    }

    [Fact]
    public void An_ordinary_install_failure_is_not_mistaken_for_a_broken_pip()
    {
        Assert.False(PipSource.LooksLikeBrokenPip("ERROR: Could not find a version that satisfies the requirement bogus"));
        Assert.False(PipSource.LooksLikeBrokenPip(null));
    }
}

/// <summary>
/// A scan failure must say more than "exited with code 1" to be any help in the pill's hover tooltip:
/// <see cref="CliUpdateSource.EnsureScanCompleted"/> folds in the tool's last non-empty output line,
/// the same way an install failure already carries its own detail.
/// </summary>
public class CliScanFailureDetailTests
{
    private static ProcessResult Result(int exitCode, string stderr = "", string stdout = "") =>
        new(exitCode, stdout, stderr);

    [Fact]
    public void A_scan_failure_names_the_tools_own_last_output_line()
    {
        var source = new PipSource(new LoggingService(), new HttpClient());
        var result = Result(1, stderr: "Warming up…\nERROR: Could not find pip\n");

        var ex = Assert.Throws<InvalidOperationException>(() => source.EnsureScanCompleted(result));

        Assert.Contains("exited with code 1", ex.Message);
        Assert.Contains("ERROR: Could not find pip", ex.Message);
    }

    /// <summary>stdout is the fallback only when the tool printed nothing to stderr.</summary>
    [Fact]
    public void Stdout_is_used_when_stderr_is_empty()
    {
        var source = new PipSource(new LoggingService(), new HttpClient());
        var result = Result(1, stderr: "", stdout: "some diagnostic on stdout");

        var ex = Assert.Throws<InvalidOperationException>(() => source.EnsureScanCompleted(result));

        Assert.Contains("some diagnostic on stdout", ex.Message);
    }

    [Fact]
    public void No_output_at_all_still_names_the_tool_and_code()
    {
        var source = new PipSource(new LoggingService(), new HttpClient());

        var ex = Assert.Throws<InvalidOperationException>(() => source.EnsureScanCompleted(Result(1)));

        Assert.Contains("exited with code 1", ex.Message);
    }

    /// <summary>The user's own profile path must never reach the pill's tooltip unredacted.</summary>
    [Fact]
    public void The_profile_path_is_redacted_in_the_scan_failure_detail()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        var source = new PipSource(new LoggingService(), new HttpClient());
        var result = Result(1, stderr: $@"ERROR: cannot write to {profile}\AppData\Local\pip\cache");

        var ex = Assert.Throws<InvalidOperationException>(() => source.EnsureScanCompleted(result));

        Assert.DoesNotContain(profile, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", ex.Message);
    }

    [Fact]
    public void A_successful_scan_result_passes_through_unchanged()
    {
        var source = new PipSource(new LoggingService(), new HttpClient());
        var result = Result(0);

        Assert.Same(result, source.EnsureScanCompleted(result));
    }
}
