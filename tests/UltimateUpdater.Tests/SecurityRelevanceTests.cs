using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The critical mark, from both sides: the software it has to catch, and the ordinary software it has
/// to leave alone. A badge on the wrong row is worse than a row without one, so the negative cases
/// carry as much weight here as the positive ones.
/// </summary>
public class SecurityRelevanceTests
{
    [Theory]
    // winget ids are Publisher.Product, sometimes with a version tail
    [InlineData("Mozilla.Firefox", "Mozilla Firefox")]
    [InlineData("Google.Chrome", "Google Chrome")]
    [InlineData("Microsoft.Edge", "Microsoft Edge")]
    [InlineData("Brave.Brave", "Brave")]
    [InlineData("VivaldiTechnologies.Vivaldi", "Vivaldi")]
    [InlineData("Opera.Opera", "Opera")]
    [InlineData("TorProject.TorBrowser", "Tor Browser")]
    [InlineData("OpenVPNTechnologies.OpenVPN", "OpenVPN")]
    [InlineData("WireGuard.WireGuard", "WireGuard")]
    [InlineData("Malwarebytes.Malwarebytes", "Malwarebytes")]
    [InlineData("DominikReichl.KeePass", "KeePass")]
    [InlineData("KeePassXCTeam.KeePassXC", "KeePassXC")]
    [InlineData("Bitwarden.Bitwarden", "Bitwarden")]
    [InlineData("Oracle.JDK.21", "Java SE Development Kit 21")]
    [InlineData("EclipseAdoptium.Temurin.21.JDK", "Eclipse Temurin JDK 21")]
    [InlineData("OpenJS.NodeJS.LTS", "Node.js")]
    [InlineData("Python.Python.3.12", "Python 3.12.4 (64-bit)")]
    [InlineData("Microsoft.DotNet.DesktopRuntime.8", ".NET Windows Desktop Runtime")]
    [InlineData("ShiningLight.OpenSSL.Light", "OpenSSL 3.4.0 Light")]
    [InlineData("PHP.PHP", "PHP 8.3")]
    public void Security_relevant_software_is_critical(string id, string name) =>
        Assert.True(SecurityRelevance.IsSecurityRelevant(id, name));

    [Theory]
    // package managers use one plain id, and the row's name is the same string
    [InlineData("firefox")]
    [InlineData("googlechrome")]
    [InlineData("microsoft-edge")]
    [InlineData("keepass")]
    [InlineData("bitwarden")]
    [InlineData("wireguard")]
    [InlineData("openssl")]
    [InlineData("nodejs-lts")]
    [InlineData("python")]
    [InlineData("openjdk")]
    [InlineData("dotnet-runtime")]
    public void A_package_manager_id_is_recognised_on_its_own(string packageId) =>
        Assert.True(SecurityRelevance.IsSecurityRelevant(packageId, packageId));

    [Theory]
    [InlineData("7zip.7zip", "7-Zip 24.08")]                      // an archiver is not what this mark is for
    [InlineData("VideoLAN.VLC", "VLC media player")]
    [InlineData("Notepad++.Notepad++", "Notepad++")]
    [InlineData("Discord.Discord", "Discord")]
    [InlineData("Valve.Steam", "Steam")]
    [InlineData("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code")]
    [InlineData("Google.ChromeRemoteDesktop", "Chrome Remote Desktop")]   // shares a word with the browser, and is not it
    [InlineData("dotnet-ef", "Entity Framework Core tools")]             // a.NET tool is not the.NET runtime
    [InlineData("eslint", "eslint")]
    public void Ordinary_software_is_not_critical(string id, string name) =>
        Assert.False(SecurityRelevance.IsSecurityRelevant(id, name));

    /// <summary>Sources that report a display name and a version but no useful id still have to match.</summary>
    [Theory]
    [InlineData("Mozilla Firefox")]
    [InlineData("Google Chrome 129")]
    [InlineData("Node.js")]
    public void A_name_alone_is_enough(string name) =>
        Assert.True(SecurityRelevance.IsSecurityRelevant(null, name));

    [Fact]
    public void Nothing_at_all_is_not_critical()
    {
        Assert.False(SecurityRelevance.IsSecurityRelevant(null, null));
        Assert.False(SecurityRelevance.IsSecurityRelevant(string.Empty, "   "));
    }

    [Theory]
    [InlineData("Critical", true)]
    [InlineData("Important", true)]
    [InlineData("Moderate", false)]
    [InlineData("Low", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Windows_update_keeps_its_own_severity(string? severity, bool expected) =>
        Assert.Equal(expected, SecurityRelevance.IsCriticalSeverity(severity));

    /// <summary>
    /// A Windows Update security item is critical on Microsoft's word, not on the table's: its title
    /// matches nothing in the table, and it stays critical all the same.
    /// </summary>
    [Fact]
    public void A_windows_update_security_item_stays_critical()
    {
        const string title = "2026-07 Cumulative Update for Windows 11 (KB5062553)";

        Assert.False(SecurityRelevance.IsSecurityRelevant($"{Guid.Empty}:1", title));
        Assert.True(SecurityRelevance.IsCriticalSeverity("Critical"));
    }

    /// <summary>
    /// The mark has to reach the rows, not just exist: this is the shared mapping every package manager
    /// builds its rows with.
    /// </summary>
    [Fact]
    public void A_package_manager_row_carries_the_mark()
    {
        var source = new TestCliSource(new LoggingService());

        Assert.True(source.Row("firefox", "Mozilla Firefox").IsCritical);
        Assert.False(source.Row("7zip", "7-Zip").IsCritical);
    }

    /// <summary>Stands in for chocolatey, scoop, pip, npm and the rest, which share this base class.</summary>
    private sealed class TestCliSource(LoggingService logging) : CliUpdateSource(logging)
    {
        public override string Id => "test";

        public override UpdateCategory Category => UpdateCategory.Software;

        protected override string Executable => "cmd";

        public UpdateItem Row(string id, string name) => CreateItem(id, name, "1.0", "2.0");

        protected override Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UpdateItem>>([]);

        protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => [];
    }
}
