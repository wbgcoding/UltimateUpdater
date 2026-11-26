using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Tests;

/// <summary>The fast "System Restore is off" check before the helper is asked, and the log lines around the slow part.</summary>
public class UninstallRestorePointAvailabilityTests
{
    [Fact]
    public void A_registry_with_no_switch_says_nothing_so_an_absent_service_key_is_not_a_refusal()
    {
        // Windows 11 has no srservice key and still makes restore points.
        Assert.Null(SystemRestoreAvailability.UnavailableReason(new FakeUninstallRegistry()));
    }

    [Theory]
    [InlineData(SystemRestoreAvailability.PolicyKey, "DisableSR", "1", "policy")]
    [InlineData(SystemRestoreAvailability.SettingKey, "DisableSR", "1", "turned off")]
    [InlineData(SystemRestoreAvailability.ServicesKey + @"\srservice", "Start", "4", "srservice")]
    [InlineData(SystemRestoreAvailability.ServicesKey + @"\VSS", "Start", "4", "VSS")]
    [InlineData(SystemRestoreAvailability.ServicesKey + @"\swprv", "Start", "4", "swprv")]
    public void An_explicit_off_switch_is_reported_with_its_cause(string key, string name, string value, string cause)
    {
        var registry = new FakeUninstallRegistry().Set(key, name, value);

        Assert.Contains(cause, SystemRestoreAvailability.UnavailableReason(registry));
    }

    [Theory]
    [InlineData(SystemRestoreAvailability.PolicyKey, "DisableSR", "0")]
    [InlineData(SystemRestoreAvailability.SettingKey, "DisableSR", "0")]
    [InlineData(SystemRestoreAvailability.ServicesKey + @"\VSS", "Start", "3")]
    [InlineData(SystemRestoreAvailability.ServicesKey + @"\srservice", "Start", "2")]
    public void A_switch_that_is_on_is_not_a_refusal(string key, string name, string value)
    {
        var registry = new FakeUninstallRegistry().Set(key, name, value);

        Assert.Null(SystemRestoreAvailability.UnavailableReason(registry));
    }

    private static (UninstallRestorePointService Service, List<string> Log, Func<int> Made) Service(
        Func<string?> unavailable, Func<DateTimeOffset?>? created = null)
    {
        var log = new List<string>();
        var made = 0;
        var service = new UninstallRestorePointService(
            new LoggingService(line => log.Add(line)),
            _ =>
            {
                made++;
                return Task.FromResult(created?.Invoke());
            },
            () => null, _ => { }, new RestorePointReservation(), () => DateTimeOffset.UtcNow, unavailable);
        return (service, log, () => made);
    }

    [Fact]
    public async Task When_system_restore_is_off_the_helper_is_never_asked_and_the_result_is_a_failure_at_once()
    {
        var (service, log, made) = Service(() => "System Restore is turned off by policy");

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Failed, result.Outcome);
        Assert.Equal(0, made());
        Assert.Contains(log, line => line.Contains("not attempted") && line.Contains("turned off by policy"));
    }

    [Fact]
    public async Task The_slow_part_logs_its_start_and_its_end_with_the_duration()
    {
        var now = DateTimeOffset.UtcNow;
        var (service, log, _) = Service(() => null, () => now);

        await service.EnsureAsync(CancellationToken.None);

        Assert.Contains(log, line => line.Contains("started"));
        Assert.Contains(log, line => line.Contains("created in") && line.Contains(" s"));
    }

    [Fact]
    public async Task A_helper_that_makes_nothing_logs_the_failure_with_the_duration()
    {
        var (service, log, _) = Service(() => null, () => null);

        var result = await service.EnsureAsync(CancellationToken.None);

        Assert.Equal(UninstallRestorePointOutcome.Failed, result.Outcome);
        Assert.Contains(log, line => line.Contains("not created") && line.Contains(" s"));
    }

    [Fact]
    public async Task A_helper_job_that_throws_logs_the_failure_with_the_duration_and_still_throws()
    {
        var log = new List<string>();
        var service = new UninstallRestorePointService(
            new LoggingService(line => log.Add(line)),
            _ => throw new InvalidOperationException("pipe broke"),
            () => null, _ => { }, new RestorePointReservation(), () => DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(CancellationToken.None));

        Assert.Contains(log, line => line.Contains("failed after") && line.Contains("pipe broke"));
    }
}
