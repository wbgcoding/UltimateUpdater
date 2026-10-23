using System.IO;
using System.Net.Http;
using UltimateUpdater.Services;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public partial class MainViewModelTests
{
    private const string NewerReleaseJson = """
        {"tag_name":"v999.0.0","draft":false,"body":"","assets":[
          {"name":"UltimateUpdater.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/a.exe","url":"https://api.github.com/repos/x/y/releases/assets/1","size":10},
          {"name":"Setup-UltimateUpdater-999.0.0.exe","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/b.exe","url":"https://api.github.com/repos/x/y/releases/assets/2","size":10},
          {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v999.0.0/SHA256SUMS.txt","url":"https://api.github.com/repos/x/y/releases/assets/3","size":10}
        ]}
        """;

    /// <summary>Runs the install command against a scripted channel and returns the headlines of the
    /// warnings it raised. The portable swap is pointed at a temp folder whose backup slot is blocked, so a
    /// download that verifies still fails to apply without ever touching the test host's own executable.</summary>
    private static List<string> RunSelfUpdateInstall(
        Func<string, CancellationToken, Task<string?>> get,
        Func<string, CancellationToken, Task<bool>> download)
    {
        Assert.False(AppPaths.IsInstalled);

        var directory = Directory.CreateTempSubdirectory("wu-selfupdate-vm-").FullName;
        var imagePath = Path.Combine(directory, "UltimateUpdater.exe");
        File.WriteAllBytes(imagePath, [1]);
        Directory.CreateDirectory(imagePath + ".old");
        File.WriteAllBytes(Path.Combine(imagePath + ".old", "keep"), [0]);

        var warnings = new List<string>();
        Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            var channel = new SelfUpdateOutcomeTests.ScriptedChannel { Get = get, Download = download };
            var logging = new LoggingService();
            var selfUpdate = new SelfUpdateService(new HttpClient(), logging, channel, () => imagePath);
            var viewModel = Create(selfUpdate: selfUpdate);

            Assert.Equal(
                SelfUpdateCheckOutcome.Found, Pump(viewModel.CheckForUpdatesInteractiveAsync()));
            Pump(viewModel.InstallSelfUpdateCommand.ExecuteAsync(null));
            return warnings;
        }
        finally
        {
            Dialogs.WarnObserverForTests = null;
            ElevationService.ReleaseSelfImageLock();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static Task<string?> ReleaseFeed(string url) =>
        Task.FromResult<string?>(url.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? SelfUpdateOutcomeTests.ChecksumLine([7, 7, 7])
            : NewerReleaseJson);

    [Fact]
    public void A_download_that_never_arrives_says_the_download_failed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var warnings = RunSelfUpdateInstall((url, _) => ReleaseFeed(url), (_, _) => Task.FromResult(false));

            Assert.Equal([LocalizationService.Instance["SelfUpdateDownloadFailed"]], warnings);
        });
    }

    [Fact]
    public void A_file_that_fails_verification_says_it_was_discarded()
    {
        WpfHarness.OnUiThread(() =>
        {
            var warnings = RunSelfUpdateInstall((url, _) => ReleaseFeed(url), (path, _) =>
            {
                File.WriteAllBytes(path, [9, 9, 9]);
                return Task.FromResult(true);
            });

            Assert.Equal([LocalizationService.Instance["SelfUpdateFailed"]], warnings);
        });
    }

    [Fact]
    public void A_verified_file_that_cannot_be_applied_says_the_install_failed()
    {
        WpfHarness.OnUiThread(() =>
        {
            var warnings = RunSelfUpdateInstall((url, _) => ReleaseFeed(url), (path, _) =>
            {
                File.WriteAllBytes(path, [7, 7, 7]);
                return Task.FromResult(true);
            });

            Assert.Equal([LocalizationService.Instance["SelfUpdateApplyFailed"]], warnings);
        });
    }

    [Fact]
    public void The_interactive_check_tells_found_none_and_failed_apart()
    {
        WpfHarness.OnUiThread(() =>
        {
            SelfUpdateCheckOutcome Check(string? feed) => Pump(Create(
                selfUpdate: new SelfUpdateService(
                    new HttpClient(), new LoggingService(),
                    new SelfUpdateOutcomeTests.ScriptedChannel { Get = (_, _) => Task.FromResult(feed) }))
                .CheckForUpdatesInteractiveAsync());

            Assert.Equal(SelfUpdateCheckOutcome.Found, Check(NewerReleaseJson));
            Assert.Equal(SelfUpdateCheckOutcome.None, Check("""{"tag_name":"v0.0.1","draft":false,"assets":[]}"""));
            Assert.Equal(SelfUpdateCheckOutcome.Failed, Check(null));
        });
    }

    private static T Pump<T>(Task<T> task)
    {
        Pump((Task)task);
        return task.GetAwaiter().GetResult();
    }
}
