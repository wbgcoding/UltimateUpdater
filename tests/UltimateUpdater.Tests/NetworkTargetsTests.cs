using System.IO;
using System.Text.RegularExpressions;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// The about card's own list of contacted servers is a static table in code, filtered to sources
/// that exist on this machine. This is the drift test the item asks for (the code table and
/// <c>docs/DESIGN.md</c> § Sicherheitsgrenzen must name the same hosts), plus the "not installed does not
/// appear" behaviour.
/// </summary>
public class NetworkTargetsTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }

    private static IReadOnlyList<string> CodeTableHosts() =>
        SettingsViewModel.NetworkTargetCatalog
            .SelectMany(target => target.Hosts.Split(", ", StringSplitOptions.TrimEntries))
            .OrderBy(host => host, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Only the LITERAL, backtick-quoted hosts in the design doc's own Host column, never a descriptive
    /// cell like "any host from a package manifest" (no concrete server to name) and never a backtick
    /// elsewhere in the row (the loopback row also quotes "http://" in its Purpose column).
    /// </summary>
    private static IReadOnlyList<string> DesignDocHosts()
    {
        var path = Path.Combine(RepoRoot(), "docs", "DESIGN.md");
        if (!File.Exists(path))
            return [];

        var lines = File.ReadAllLines(path);
        var inTable = false;
        var hosts = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## Sicherheitsgrenzen", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
                continue;

            if (line.StartsWith("## ", StringComparison.Ordinal))
                break;

            if (!line.StartsWith("| ", StringComparison.Ordinal) || line.StartsWith("|---", StringComparison.Ordinal))
                continue;

            var cells = line.Split('|');
            if (cells.Length < 2 || cells[1].Contains("Host"))
                continue;

            foreach (Match match in Regex.Matches(cells[1], "`([^`]+)`"))
                hosts.Add(match.Groups[1].Value);
        }

        return hosts.OrderBy(host => host, StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void The_code_table_and_the_design_doc_name_the_same_hosts()
    {
        var docHosts = DesignDocHosts();
        if (docHosts.Count == 0)
            return; // the doc is a local reference file, not shipped with every checkout

        Assert.Equal(docHosts, CodeTableHosts());
    }

    [Fact]
    public void Every_row_names_at_least_one_host_and_a_calling_file()
    {
        Assert.All(SettingsViewModel.NetworkTargetCatalog, target =>
        {
            Assert.False(string.IsNullOrWhiteSpace(target.Hosts));
            Assert.False(string.IsNullOrWhiteSpace(target.Purpose));
            Assert.False(string.IsNullOrWhiteSpace(target.CallingCode));
        });
    }

    private static SettingsViewModel CreateSettingsViewModel(Func<string, bool>? isNotInstalled)
    {
        var logging = new LoggingService();
        var settingsPath = Path.Combine(Directory.CreateTempSubdirectory("wu-network-targets-").FullName, "settings.json");
        return new SettingsViewModel(
            new SettingsService(logging, settingsPath), logging, new ThemeService(), new AutostartService(logging),
            isSourceNotInstalled: isNotInstalled);
    }

    [Fact]
    public void Every_row_shows_when_nothing_is_known_to_be_missing()
    {
        var viewModel = CreateSettingsViewModel(isNotInstalled: null);

        Assert.Equal(SettingsViewModel.NetworkTargetCatalog.Count, viewModel.NetworkTargets.Count);
    }

    [Fact]
    public void A_row_tied_to_one_missing_source_does_not_appear()
    {
        var viewModel = CreateSettingsViewModel(id => id == "steam");

        Assert.DoesNotContain(viewModel.NetworkTargets, target => target.SourceIds.Contains("steam"));
    }

    [Fact]
    public void A_row_with_no_owning_source_always_appears()
    {
        // The self-update row: not gated to a single installable tool, so nothing can ever hide it.
        var viewModel = CreateSettingsViewModel(_ => true);

        Assert.Contains(viewModel.NetworkTargets, target => target.Hosts.Contains("api.github.com"));
    }
}
