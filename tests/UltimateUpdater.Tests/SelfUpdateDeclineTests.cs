using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

/// <summary>A declined elevation prompt keeps the verified download for the next click; every other end discards it.</summary>
public sealed class SelfUpdateDeclineTests
{
    private const string SetupName = "Setup-UltimateUpdater-999.0.0.exe";

    private static readonly byte[] Content = [1, 2, 3, 4];

    private static AvailableUpdate Update() => new(
        "999.0.0",
        new ReleaseAsset(SetupName, "https://github.com/x/y/s.exe", "", 10),
        new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/SHA256SUMS.txt", "", 10),
        string.Empty,
        new ReleaseAsset(SetupName + ".sig", "https://github.com/x/y/s.exe.sig", "", 90),
        new ReleaseAsset("SHA256SUMS.txt.sig", "https://github.com/x/y/SHA256SUMS.txt.sig", "", 90));

    private static async Task<(SelfUpdateService Service, string Path)> VerifiedAsync(Func<ProcessStartInfo, Process?> startSetup)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var channel = new SelfUpdateOutcomeTests.ScriptedChannel
        {
            Get = (url, _) => SelfUpdateOutcomeTests.SignedFeed(key, url, Content, Content, "999.0.0", SetupName),
            Download = (path, _) =>
            {
                File.WriteAllBytes(path, Content);
                return Task.FromResult(true);
            },
        };
        var service = new SelfUpdateService(
            new HttpClient(), new LoggingService(), channel, publicKeyPem: key.ExportSubjectPublicKeyInfoPem(),
            isInstalled: () => true, startSetup: startSetup);

        var result = await service.DownloadVerifiedAsync(Update(), CancellationToken.None);
        Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, result.Outcome);
        return (service, result.Path!);
    }

    private static Process? Declined(ProcessStartInfo _) => throw new Win32Exception(1223);

    [Fact]
    public async Task A_declined_elevation_prompt_is_reported_and_keeps_the_verified_file()
    {
        var (service, path) = await VerifiedAsync(Declined);
        try
        {
            Assert.Equal(SelfUpdateApplyResult.Declined, service.Apply(path));

            Assert.True(File.Exists(path));
            Assert.True(service.HasVerified("999.0.0"));
            Assert.Equal(path, service.VerifiedPathFor("999.0.0"));
            Assert.False(service.HasVerified("1000.0.0"));
        }
        finally
        {
            service.DiscardVerified(path);
        }
    }

    [Fact]
    public async Task A_second_apply_after_a_decline_can_still_start_the_same_file()
    {
        var attempts = 0;
        var (service, path) = await VerifiedAsync(_ => ++attempts == 1 ? throw new Win32Exception(1223) : null);
        try
        {
            Assert.Equal(SelfUpdateApplyResult.Declined, service.Apply(path));
            Assert.Equal(SelfUpdateApplyResult.Started, service.Apply(path));
            Assert.Equal(2, attempts);
        }
        finally
        {
            service.DiscardVerified(path);
        }
    }

    [Fact]
    public async Task Any_other_start_failure_discards_the_verified_file()
    {
        var (service, path) = await VerifiedAsync(_ => throw new Win32Exception(5));

        Assert.Equal(SelfUpdateApplyResult.Failed, service.Apply(path));

        Assert.False(File.Exists(path));
        Assert.False(service.HasVerified("999.0.0"));
    }

    [Fact]
    public async Task A_newer_release_discards_the_held_file_of_an_older_one()
    {
        var (service, path) = await VerifiedAsync(Declined);
        service.Apply(path);

        service.DiscardVerifiedExcept("999.0.0");
        Assert.True(File.Exists(path));

        service.DiscardVerifiedExcept("1000.0.0");
        Assert.False(File.Exists(path));
        Assert.False(service.HasVerified("999.0.0"));
    }

    [Fact]
    public async Task The_held_file_is_removed_when_the_app_exits_without_having_started_it()
    {
        var (service, path) = await VerifiedAsync(Declined);
        service.Apply(path);

        service.DiscardUnstartedVerified();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_setup_that_is_running_is_not_discarded_at_exit()
    {
        var (service, path) = await VerifiedAsync(_ => null);
        try
        {
            Assert.Equal(SelfUpdateApplyResult.Started, service.Apply(path));

            service.DiscardUnstartedVerified();

            Assert.True(File.Exists(path));
        }
        finally
        {
            service.DiscardVerified(path);
        }
    }

    [Fact]
    public async Task An_unverified_path_is_never_applied()
    {
        var (service, path) = await VerifiedAsync(_ => null);
        try
        {
            Assert.Equal(SelfUpdateApplyResult.Failed, service.Apply(path + ".other"));
        }
        finally
        {
            service.DiscardVerified(path);
        }
    }
}
