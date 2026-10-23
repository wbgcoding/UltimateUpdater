using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// Ship-clean rule: a comment explains the CURRENT behavior, never the process that produced it. No
/// plan-point number, no plan-document filename, no "round N" reference, no tool names and no personal
/// names. Same shape as <see cref="SecretScanTests"/>: scan every tracked source file for a fixed set of
/// SHAPES, not for a hand-maintained list of past incidents.
/// </summary>
public class ProcessTraceScanTests
{
    private static readonly string[] ScannedExtensions = [".cs", ".xaml"];

    // Hex escapes so the matched words themselves never appear in the repository.
    private static readonly (string Name, Regex Pattern)[] Detectors =
    [
        ("work item reference", new Regex(@"\b\x57ORK\.md\b|\b\x57ORK item\b", RegexOptions.IgnoreCase)),
        ("plan document filename", new Regex(@"\bPLAN-\d{4}-\d{2}-\d{2}\b")),
        ("plan-point number", new Regex(@"\bPunkt \d+(\.\d+)?\b")),
        ("round reference", new Regex(@"\bRunde \d+\b", RegexOptions.IgnoreCase)),
        ("tool name", new Regex(@"\b(\x43laude|\x43odex|\x41nthropic|\x43hatGPT|\x43opilot|\x47emini)\b")),
        ("personal name", new Regex(@"\b\x42en\b")),
    ];

    [Fact]
    public void No_tracked_source_file_carries_a_process_trace()
    {
        var root = RepoRoot();
        var findings = new List<string>();

        foreach (var relative in TrackedSourceFiles(root))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));

            for (var i = 0; i < lines.Length; i++)
                foreach (var name in Findings(lines[i]))
                    findings.Add($"{relative}:{i + 1}: {name}");
        }

        Assert.True(findings.Count == 0,
            $"process traces found:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    // A comment citing a plan's own item number ages badly: the number means nothing once the plan
    // that assigned it is gone, and it invites future comments to keep citing plan bookkeeping instead
    // of explaining the current behavior. Deliberately comment-only (see CommentSpans below): plenty of
    // real product and test text legitimately contains "item" next to a number (a resx string, a batch
    // count assertion) and none of that is this rule's business.
    private static readonly Regex PlanItemNumberPattern = new(@"\bitems?\s+\d{2,3}\b", RegexOptions.IgnoreCase);

    [Fact]
    public void No_comment_cites_a_plans_own_item_number()
    {
        var root = RepoRoot();
        var findings = new List<string>();

        foreach (var relative in TrackedSourceFiles(root))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));
            var isXaml = Path.GetExtension(relative).Equals(".xaml", StringComparison.OrdinalIgnoreCase);

            foreach (var (comment, offset) in CommentSpans(text, isXaml))
                if (PlanItemNumberPattern.IsMatch(comment))
                    findings.Add($"{relative}:{LineNumberAt(text, offset)}: plan item number in a comment");
        }

        Assert.True(findings.Count == 0,
            $"plan item numbers found in comments:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Theory]
    [InlineData("// A normal comment naming no plan item at all.", false)]
    [InlineData("private const string Bendy = \"Item 3 of 5\";", false)]
    [InlineData("Assert.Equal(\"batch done 3 items 12.4s\", ActivityLog.BatchFinished(3, t));", false)]
    public void PlanItemNumberPattern_only_flags_the_comment_portion_of_a_line(string line, bool expectFinding)
    {
        var isXaml = line.TrimStart().StartsWith("<!--", StringComparison.Ordinal);
        var found = CommentSpans(line, isXaml).Any(span => PlanItemNumberPattern.IsMatch(span.Text));

        Assert.Equal(expectFinding, found);
    }

    /// <summary>The lines that must be flagged live in a data file: written inline here they would
    /// themselves be source comments citing plan items, which the repository's commit check rejects.</summary>
    [Fact]
    public void PlanItemNumberPattern_flags_every_sample_in_the_positive_data_file()
    {
        var path = Path.Combine(RepoRoot(), "tests", "UltimateUpdater.Tests", "TestData", "plan-item-comments.txt");
        var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToList();
        Assert.Equal(3, lines.Count);
        foreach (var line in lines)
        {
            var isXaml = line.TrimStart().StartsWith("<!--", StringComparison.Ordinal);
            Assert.True(CommentSpans(line, isXaml).Any(span => PlanItemNumberPattern.IsMatch(span.Text)), line);
        }
    }

    /// <summary>Pulls out only the comment text of a source file: <c>//</c>/<c>///</c> line comments and
    /// <c>/* */</c> block comments for .cs, <c>&lt;!-- --&gt;</c> for .xaml (both can span several lines,
    /// e.g. a wrapped doc comment), so a scan built on this never sees a string literal or a plain
    /// identifier.</summary>
    private static IEnumerable<(string Text, int Offset)> CommentSpans(string text, bool isXaml)
    {
        var pattern = isXaml
            ? new Regex(@"<!--(.*?)-->", RegexOptions.Singleline)
            : new Regex(@"//+[^\r\n]*|/\*(.*?)\*/", RegexOptions.Singleline);

        foreach (Match match in pattern.Matches(text))
            yield return (match.Value, match.Index);
    }

    private static int LineNumberAt(string text, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    /// <summary>A detector that matches nothing would keep the scan above green for ever.</summary>
    [Fact]
    public void Every_detector_still_recognises_its_own_pattern()
    {
        Assert.Equal(["work item reference"], Findings("// \u0057ORK item 14A: fixed the race"));
        Assert.Equal(["plan document filename"], Findings("// see PLAN-2026-07-25.md for background"));
        Assert.Equal(["plan-point number"], Findings("// Punkt 3.2 says the code wins over the comment"));
        Assert.Equal(["round reference"], Findings("// found in Runde 4 of the review"));
        Assert.Equal(["tool name"], Findings("// reviewed by \u0043laude before merging"));
        Assert.Equal(["personal name"], Findings("// \u0042en's hard limit: at least 2.4 s"));
    }

    [Theory]
    [InlineData("// A normal comment explaining the current behavior.")]
    [InlineData("// Fixed a race: a stale pump must still release it.")]
    [InlineData("// \"gleicher Handgriff, gleicher Punkt\" (same move, same spot) as the fix above.")]
    [InlineData("// Hard limit (\"ruhig + hochwertig\", calm and high-quality): at least 2.4 s.")]
    [InlineData("private const string Bendy = \"not a name\";")]
    public void Ordinary_comments_are_not_flagged(string line) => Assert.Empty(Findings(line));

    private static IEnumerable<string> Findings(string line)
    {
        foreach (var (name, pattern) in Detectors)
            if (pattern.IsMatch(line))
                yield return name;
    }

    /// <summary>Everything git tracks under src/tests that is actual source (not markup assets, resources,
    /// or generated files), the published set, straight from git, same approach as
    /// <see cref="SecretScanTests"/>'s TrackedTextFiles.</summary>
    private static IEnumerable<string> TrackedSourceFiles(string root)
    {
        var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "ls-files")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });

        Assert.True(git is not null, "git is required to know which files this repository would publish");

        var listing = git!.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, "git ls-files failed, so the published file set is unknown");

        var tracked = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace('/', Path.DirectorySeparatorChar))
            .Where(file => ScannedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => file.StartsWith("src" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.StartsWith("tests" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(file => file.EndsWith(".g.cs", StringComparison.Ordinal) is false)
            .Where(file => File.Exists(Path.Combine(root, file)))
            // This file has to spell the forbidden shapes out to detect and prove them, and it is the one
            // place where they are the subject rather than a leak.
            .Where(file => !file.EndsWith(nameof(ProcessTraceScanTests) + ".cs", StringComparison.Ordinal))
            .ToList();

        Assert.True(tracked.Count > 50, $"only {tracked.Count} tracked source files found; the listing is not trustworthy");
        return tracked;
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
