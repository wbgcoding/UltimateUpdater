using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

/// <summary>
/// The installer was renamed to UltimateUpdater-Setup-&lt;version&gt;.exe. Releases still carry an identical copy under
/// the old name for installed older versions, and the current updater takes the new name first, the old one only as
/// a fallback. Portable copies never take either.
/// </summary>
public sealed class SetupAssetNameTests
{
    private const string NewName = "UltimateUpdater-Setup-999.0.0.exe";
    private const string OldName = "Setup-UltimateUpdater-999.0.0.exe";

    private static readonly byte[] Content = [1, 2, 3, 4];

    private static SelfUpdateService Service(bool installed) =>
        new(new HttpClient(), new LoggingService(), new SelfUpdateOutcomeTests.ScriptedChannel(),
            publicKeyPem: null, isInstalled: () => installed);

    private static ReleaseInfo Release(params string[] assetNames)
    {
        var assets = string.Join(",", assetNames.Select(n =>
            $$"""{"name":"{{n}}","browser_download_url":"https://github.com/x/{{n}}","url":"https://api.github.com/x/{{n}}","size":10}"""));
        var release = UpdateManifest.ParseRelease($$"""{"tag_name":"v999.0.0","draft":false,"assets":[{{assets}}]}""");
        Assert.NotNull(release);
        return release!;
    }

    [Fact]
    public void An_installed_copy_takes_the_new_name_when_both_are_there()
    {
        var release = Release("UltimateUpdater.exe", OldName, NewName, "SHA256SUMS.txt");

        Assert.Equal(NewName, Service(installed: true).SelectAssetForThisMachine(release)?.Name);
    }

    [Fact]
    public void An_installed_copy_falls_back_to_the_old_name_when_it_is_the_only_one()
    {
        var release = Release("UltimateUpdater.exe", OldName, "SHA256SUMS.txt");

        Assert.Equal(OldName, Service(installed: true).SelectAssetForThisMachine(release)?.Name);
    }

    [Fact]
    public void An_installed_copy_takes_no_other_setup_name()
    {
        var release = Release("UltimateUpdater.exe", "UltimateUpdater-Setup-998.0.0.exe", "Setup-UltimateUpdater-998.0.0.exe", "Setup.exe");

        Assert.Null(Service(installed: true).SelectAssetForThisMachine(release));
    }

    [Fact]
    public void A_portable_copy_never_takes_a_setup_asset()
    {
        var release = Release(OldName, NewName);
        Assert.Null(Service(installed: false).SelectAssetForThisMachine(release));

        Assert.Equal("UltimateUpdater.exe",
            Service(installed: false).SelectAssetForThisMachine(Release(OldName, NewName, "UltimateUpdater.exe"))?.Name);
    }

    [Theory]
    [InlineData(NewName)]
    [InlineData(OldName)]
    public async Task Either_name_downloads_when_the_signature_and_the_checksum_match(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var channel = new SelfUpdateOutcomeTests.ScriptedChannel
        {
            Get = (url, _) => SelfUpdateOutcomeTests.SignedFeed(key, url, Content, Content, "999.0.0", name),
            Download = (path, _) =>
            {
                File.WriteAllBytes(path, Content);
                return Task.FromResult(true);
            },
        };
        var service = new SelfUpdateService(
            new HttpClient(), new LoggingService(), channel,
            publicKeyPem: key.ExportSubjectPublicKeyInfoPem(), isInstalled: () => true);
        var update = new AvailableUpdate(
            "999.0.0",
            new ReleaseAsset(name, "https://github.com/x/y/s.exe", "", 10),
            new ReleaseAsset("SHA256SUMS.txt", "https://github.com/x/y/SHA256SUMS.txt", "", 10),
            string.Empty,
            new ReleaseAsset(name + ".sig", "https://github.com/x/y/s.exe.sig", "", 90),
            new ReleaseAsset("SHA256SUMS.txt.sig", "https://github.com/x/y/SHA256SUMS.txt.sig", "", 90));

        var result = await service.DownloadVerifiedAsync(update, CancellationToken.None);

        Assert.Equal(SelfUpdateDownloadOutcome.Downloaded, result.Outcome);
        service.DiscardVerified(result.Path!);
    }

    [Fact]
    public void The_build_ships_both_names_and_lists_both_in_the_checksums()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;
        Assert.True(directory is not null, "the repository root is not above the test binaries any more");

        var build = File.ReadAllText(Path.Combine(directory!.FullName, "build.bat")).Replace("\r\n", "\n");
        var script = File.ReadAllText(Path.Combine(directory.FullName, "installer", "UltimateUpdater.iss"));

        Assert.Contains("OutputBaseFilename=UltimateUpdater-Setup-{#AppVersion}", script);
        Assert.Contains("copy /y \"%DIST%\\UltimateUpdater-Setup-%VERSION%.exe\" \"%DIST%\\Setup-UltimateUpdater-%VERSION%.exe\"", build);

        // the copy is made after the new name was signed (byte-identical) and before the checksums and signatures
        var signed = build.IndexOf(":nosignsetup", StringComparison.Ordinal);
        var copied = build.IndexOf("\"%DIST%\\Setup-UltimateUpdater-%VERSION%.exe\"", StringComparison.Ordinal);
        var sums = build.IndexOf("=== checksums ===", StringComparison.Ordinal);
        var signatures = build.IndexOf("=== release signatures ===", StringComparison.Ordinal);
        Assert.True(signed > 0 && signed < copied && copied < sums && sums < signatures);

        // checksums and signatures cover every exe in dist, so both names get a line and a .sig
        Assert.Contains("Get-ChildItem -Path '%DIST%\\*.exe'", build);
        Assert.Contains("sign-release.py", build);

        // no ARM64 portable lands in dist: only the x64 exe is copied there
        Assert.DoesNotContain("copy /y \"%STAGE%\\win-arm64", build);
        Assert.Contains("if /i \"%%a\"==\"x64\" (", build);
    }
}
