using System.Diagnostics;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class AfterUpdateHandoffTests
{
    [Theory]
    [InlineData("4711", 4711)]
    [InlineData("1", 1)]
    public void A_positive_process_id_after_the_argument_is_read(string value, int expected)
    {
        Assert.Equal(expected, AfterUpdateHandoff.ParseProcessId(["--after-update", value]));
        Assert.Equal(expected, AfterUpdateHandoff.ParseProcessId(["--mock-sources", "--AFTER-UPDATE", value]));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12x")]
    [InlineData("+7")]
    [InlineData(" 7")]
    [InlineData("99999999999")]
    [InlineData("")]
    [InlineData("--set-autostart")]
    public void A_garbage_value_is_ignored(string value)
    {
        Assert.Null(AfterUpdateHandoff.ParseProcessId(["--after-update", value]));
    }

    [Fact]
    public void A_missing_argument_or_missing_value_is_ignored()
    {
        Assert.Null(AfterUpdateHandoff.ParseProcessId([]));
        Assert.Null(AfterUpdateHandoff.ParseProcessId(["--mock-sources"]));
        Assert.Null(AfterUpdateHandoff.ParseProcessId(["--after-update"]));
    }

    [Fact]
    public void The_old_process_is_waited_for_and_cleaned_up_before_the_lock_is_taken()
    {
        var steps = new List<string>();

        var acquired = AfterUpdateHandoff.AcquireAfterWaiting(
            ["--after-update", "4242"],
            id => steps.Add($"wait {id}"),
            () => steps.Add("cleanup"),
            () =>
            {
                steps.Add("acquire");
                return true;
            });

        Assert.True(acquired);
        Assert.Equal(["wait 4242", "cleanup", "acquire"], steps);
    }

    [Fact]
    public void Without_the_argument_nothing_is_waited_for()
    {
        var steps = new List<string>();

        var acquired = AfterUpdateHandoff.AcquireAfterWaiting(
            ["--after-update", "garbage"],
            id => steps.Add($"wait {id}"),
            () => steps.Add("cleanup"),
            () =>
            {
                steps.Add("acquire");
                return false;
            });

        Assert.False(acquired);
        Assert.Equal(["acquire"], steps);
    }

    [Fact]
    public void A_process_that_does_not_exist_counts_as_gone()
    {
        var stopwatch = Stopwatch.StartNew();

        AfterUpdateHandoff.WaitForProcessExit(int.MaxValue);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_running_process_is_waited_for_only_up_to_the_bound()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), AfterUpdateHandoff.MaxWait);
    }
}
