using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>
/// The winget setup end to end against a fake release server and a fake installer: what the user is told
/// on success and on each kind of failure, and how the dependency packages are handled.
/// </summary>
public sealed class WingetBootstrapOutcomeTests : IDisposable
{
    private const string Base = "https://github.com/microsoft/winget-cli/releases/download/v1.10.340/";
    private const string BundleName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    private const string DependenciesName = "DesktopAppInstaller_Dependencies.zip";

    private readonly string _staging = Path.Combine(Path.GetTempPath(), "uu-bootstrap-test-" + Guid.NewGuid().ToString("N"));

    public WingetBootstrapOutcomeTests() => Directory.CreateDirectory(_staging);

    public void Dispose()
    {
        if (Directory.Exists(_staging))
            Directory.Delete(_staging, recursive: true);
    }

    private static string Release(bool withDependencies) => $$"""
        {
            "tag_name": "v1.10.340", "draft": false, "prerelease": false,
            "assets": [
                {"name": "{{BundleName}}", "browser_download_url": "{{Base}}{{BundleName}}", "url": "https://api.github.com/repos/microsoft/winget-cli/releases/assets/1"}
                {{(withDependencies
                    ? $$""", {"name": "{{DependenciesName}}", "browser_download_url": "{{Base}}{{DependenciesName}}", "url": "https://api.github.com/repos/microsoft/winget-cli/releases/assets/2"}"""
                    : "")}}
            ]
        }
        """;

    private static byte[] DependencyZip()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "x64/Microsoft.VCLibs.appx", "x64/Microsoft.UI.Xaml.appx", "arm64/Microsoft.VCLibs.appx", "x64/readme.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("package " + name);
            }
        }

        return stream.ToArray();
    }

    private sealed class FakeGitHub(string? releaseJson, byte[]? dependencies, int apiStatus = 200) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            HttpResponseMessage response;
            if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
                response = new HttpResponseMessage((HttpStatusCode)apiStatus) { Content = new StringContent(releaseJson ?? "") };
            else if (url.EndsWith(BundleName, StringComparison.Ordinal))
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[300_000]) };
            else if (url.EndsWith(DependenciesName, StringComparison.Ordinal) && dependencies is not null)
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(dependencies) };
            else
                response = new HttpResponseMessage(HttpStatusCode.NotFound);

            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class Recording : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    private WingetBootstrapService Service(
        string? releaseJson, byte[]? dependencies, List<string> installed, Func<string, bool>? trusted = null,
        Func<string, ProcessResult>? install = null, int apiStatus = 200) =>
        new(new HttpClient(new FakeGitHub(releaseJson, dependencies, apiStatus)), new LoggingService(), () => false)
        {
            StagingRoot = _staging,
            ArchitectureFolder = "x64",
            IsTrustedPackage = trusted ?? (_ => true),
            AddPackageAsync = (path, _) =>
            {
                installed.Add(Path.GetFileName(path) == "installer.msixbundle" ? "bundle" : File.ReadAllText(path));
                return Task.FromResult(install?.Invoke(path) ?? new ProcessResult(0, "", ""));
            },
        };

    [Fact]
    public async Task A_successful_setup_installs_the_dependencies_first_then_the_bundle_and_cleans_up()
    {
        var installed = new List<string>();
        var progress = new Recording();
        var service = Service(Release(withDependencies: true), DependencyZip(), installed);

        var outcome = await service.InstallAsync(progress, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Installed, outcome.Status);
        Assert.Equal(["package x64/Microsoft.VCLibs.appx", "package x64/Microsoft.UI.Xaml.appx", "bundle"], installed);
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task A_release_without_a_dependency_archive_still_installs_the_bundle()
    {
        var installed = new List<string>();
        var service = Service(Release(withDependencies: false), null, installed);

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Installed, outcome.Status);
        Assert.Equal(["bundle"], installed);
    }

    [Fact]
    public async Task A_dependency_that_is_not_microsoft_signed_skips_all_dependencies_but_not_the_bundle()
    {
        var installed = new List<string>();
        var service = Service(
            Release(withDependencies: true), DependencyZip(), installed,
            trusted: path => !path.EndsWith(".appx", StringComparison.Ordinal));

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Installed, outcome.Status);
        Assert.Equal(["bundle"], installed);
    }

    [Fact]
    public async Task A_cancel_while_the_dependencies_are_checked_releases_them_and_removes_the_staging_folder()
    {
        var installed = new List<string>();
        using var cancel = new CancellationTokenSource();
        var service = Service(
            Release(withDependencies: true), DependencyZip(), installed,
            trusted: path =>
            {
                // The bundle check passes; the first pinned dependency is where the user cancels.
                if (path.EndsWith(".appx", StringComparison.Ordinal))
                    cancel.Cancel();
                return true;
            });

        var outcome = await service.InstallAsync(null, cancel.Token);

        Assert.Equal(WingetBootstrapStatus.Failed, outcome.Status);
        Assert.Empty(installed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task An_unsigned_bundle_fails_with_that_reason_and_installs_nothing()
    {
        var installed = new List<string>();
        var service = Service(Release(withDependencies: true), DependencyZip(), installed, trusted: _ => false);

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Failed, outcome.Status);
        Assert.Equal("WingetBootstrapReasonNotSigned", outcome.ReasonKey);
        Assert.Empty(installed);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_staging));
    }

    [Fact]
    public async Task A_refused_install_names_the_first_line_of_the_error()
    {
        var installed = new List<string>();
        var service = Service(
            Release(withDependencies: false), null, installed,
            install: _ => new ProcessResult(1, "", "\r\nDeployment failed: a dependency is missing.\r\nsecond line\r\n"));

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Failed, outcome.Status);
        Assert.Equal("WingetBootstrapReasonRefused", outcome.ReasonKey);
        Assert.Equal("Deployment failed: a dependency is missing.", outcome.Detail);
    }

    [Fact]
    public async Task An_unreadable_release_fails_with_that_reason()
    {
        var service = Service(null, null, [], apiStatus: 500);

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Failed, outcome.Status);
        Assert.Equal("WingetBootstrapReasonNoRelease", outcome.ReasonKey);
    }

    [Fact]
    public async Task A_runaway_release_response_is_cut_off_at_the_cap()
    {
        var service = Service(new string(' ', 5 * 1024 * 1024), null, []);

        var outcome = await service.InstallAsync(null, CancellationToken.None);

        Assert.Equal(WingetBootstrapStatus.Failed, outcome.Status);
        Assert.Equal("WingetBootstrapReasonNoRelease", outcome.ReasonKey);
    }

    [Theory]
    [InlineData("x64/Microsoft.VCLibs.appx", true)]
    [InlineData("Dependencies/X64/Microsoft.UI.Xaml.APPX", true)]
    [InlineData("x64\\Microsoft.VCLibs.appx", true)]
    [InlineData("arm64/Microsoft.VCLibs.appx", false)]
    [InlineData("x64/readme.txt", false)]
    [InlineData("Microsoft.VCLibs.appx", false)]
    [InlineData("x64/../evil.appx", false)]
    public void Only_appx_files_under_the_architecture_folder_are_dependencies(string entry, bool expected) =>
        Assert.Equal(expected, WingetBootstrapService.IsDependencyEntry(entry, "x64"));

    [Fact]
    public void The_pill_shows_the_download_percent_as_its_state_text_while_a_setup_runs()
    {
        var pill = new ProviderStatusViewModel("winget");
        pill.Apply(new ProviderStatus { SourceId = "winget", State = ProviderScanState.NotInstalled });
        Assert.False(pill.IsNoteworthy);

        pill.SetupPercent = 42;

        Assert.Contains("42", pill.StateText);
        Assert.True(pill.HasStateText);
        Assert.True(pill.IsNoteworthy);

        pill.SetupPercent = null;

        Assert.Null(pill.StateText);
        Assert.False(pill.IsNoteworthy);
    }
}
