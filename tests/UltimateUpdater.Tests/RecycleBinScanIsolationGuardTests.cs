using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// This fix's seam: <c>CleanupService.ScanRecycleBin</c> walks the host's own real fixed drives and its
/// real <c>$Recycle.Bin</c> folders unless a test points <c>CleanupService.RecycleBinDriveRootsOverride</c>
/// at a fake root first. A test that scans the recycle bin through <c>CleanupService.ScanAsync</c> without
/// installing that override is exactly the bug shape this guards against: asserting something the real,
/// machine-dependent bin cannot promise. The one accepted exception is a deliberate, allowlisted live-run
/// smoke test that asserts only structural invariants, never specific content, same carve-out VERIFY.md
/// gives a destructive/privileged feature's one real run.
/// Two reach limits, not fixed here: the detector only sees a violation when <c>CleanupKind.RecycleBin</c>
/// and <c>.ScanAsync(</c> appear as literal text in the SAME method body (a scan split across a helper
/// method, or reached through an indirection that never spells either literal, is invisible to it), and it
/// only walks <c>tests/UltimateUpdater.Tests</c> itself (<c>SearchOption.TopDirectoryOnly</c>), never a
/// subdirectory a future reorganisation might add. Passing proves neither absent case; it only proves the
/// shape this file actually looks for is not present.
/// </summary>
public class RecycleBinScanIsolationGuardTests
{
    /// <summary>Deliberate real-machine tests, named explicitly so a new one added later needs the same
    /// justification written down here rather than slipping in silently.</summary>
    private static readonly (string File, string Method)[] AllowedLiveRuns =
    [
        ("CleanupServiceTests.cs", "ScanAsync_for_the_recycle_bin_lists_real_files_without_throwing"),
    ];

    [Fact]
    public void Every_test_scanning_the_recycle_bin_installs_the_override_or_is_allowlisted()
    {
        var root = RepoRoot();
        var testDir = Path.Combine(root, "tests", "UltimateUpdater.Tests");
        var findings = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(file);
            if (fileName == nameof(RecycleBinScanIsolationGuardTests) + ".cs")
                continue;

            foreach (var (name, body) in TestMethods(File.ReadAllText(file)))
            {
                if (!TouchesRealRecycleBinScan(body))
                    continue;

                if (AllowedLiveRuns.Contains((fileName, name)))
                    continue;

                findings.Add($"{fileName}.{name}");
            }
        }

        Assert.True(findings.Count == 0,
            $"scans the real recycle bin without the test override or an allowlist entry: {string.Join(", ", findings)}");
    }

    /// <summary>Proves the guard actually fires: a method that scans through <c>ScanAsync</c> with a
    /// RecycleBin target and never mentions the override reads as a violation; the same shape WITH the
    /// override installed, or without <c>CleanupKind.RecycleBin</c> at all, reads clean.</summary>
    [Fact]
    public void Detector_fires_on_the_planted_violation_and_stays_quiet_on_the_isolated_and_unrelated_shapes()
    {
        const string violation = """
            public async Task Planted_violation()
            {
                var target = new CleanupTarget("x", "x", "x", CleanupKind.RecycleBin, DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");
                var result = await new CleanupService(_logging).ScanAsync(target, CancellationToken.None);
            }
            """;
        const string isolated = """
            public async Task Isolated_scan()
            {
                CleanupService.RecycleBinDriveRootsOverride = [(fakeDriveRoot, "Fake")];
                var target = new CleanupTarget("x", "x", "x", CleanupKind.RecycleBin, DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");
                var result = await new CleanupService(_logging).ScanAsync(target, CancellationToken.None);
                CleanupService.RecycleBinDriveRootsOverride = null;
            }
            """;
        const string unrelated = """
            public async Task Unrelated_scan()
            {
                var target = new CleanupTarget("x", "x", "x", CleanupKind.Directories, DefaultEnabled: false, RiskLevel: CleanupRiskLevel.Considered, ConsequenceKey: "x");
                var result = await new CleanupService(_logging).ScanAsync(target, CancellationToken.None);
            }
            """;

        Assert.True(TouchesRealRecycleBinScan(violation));
        Assert.False(TouchesRealRecycleBinScan(isolated));
        Assert.False(TouchesRealRecycleBinScan(unrelated));
    }

    private static bool TouchesRealRecycleBinScan(string methodBody) =>
        methodBody.Contains("CleanupKind.RecycleBin", StringComparison.Ordinal)
        && methodBody.Contains(".ScanAsync(", StringComparison.Ordinal)
        && !methodBody.Contains("RecycleBinDriveRootsOverride", StringComparison.Ordinal);

    /// <summary>Every public method's name and full source text, block- or expression-bodied alike;
    /// brace-counted rather than matched end to end by regex, because a nested lambda or object
    /// initializer inside the body would otherwise close a purely regex-based match early.</summary>
    private static IEnumerable<(string Name, string Body)> TestMethods(string source)
    {
        var signature = new Regex(
            @"public\s+(?:async\s+)?(?:Task|void)\s+(?<name>\w+)\s*\([^)]*\)\s*(?<open>=>|\{)",
            RegexOptions.Singleline);

        foreach (Match match in signature.Matches(source))
        {
            var name = match.Groups["name"].Value;
            var bodyStart = match.Index;
            var afterOpen = match.Index + match.Length;

            if (match.Groups["open"].Value == "=>")
            {
                var semicolon = source.IndexOf(';', afterOpen);
                if (semicolon < 0)
                    continue;

                yield return (name, source[bodyStart..(semicolon + 1)]);
                continue;
            }

            var depth = 1;
            var i = afterOpen;
            while (i < source.Length && depth > 0)
            {
                if (source[i] == '{')
                    depth++;
                else if (source[i] == '}')
                    depth--;
                i++;
            }

            yield return (name, source[bodyStart..i]);
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return directory!.FullName;
    }
}
