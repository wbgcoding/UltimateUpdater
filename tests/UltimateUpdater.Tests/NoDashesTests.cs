namespace UltimateUpdater.Tests;

/// <summary>
/// An em dash or en dash in shipped text reads as machine-generated prose. Every one of them was rewritten
/// by hand into whatever punctuation the sentence actually needed, and this guard keeps a future edit
/// from quietly reintroducing one.
/// </summary>
public class NoDashesTests
{
    private const char EmDash = '\u2014';
    private const char EnDash = '\u2013';

    /// <summary>Recursed for their source-controlled extensions; build output never lives here.</summary>
    private static readonly (string Root, string[] Extensions)[] ScannedDirectories =
    [
        ("src", [".cs", ".xaml", ".resx"]),
        ("installer", [".iss", ".txt"]),
        ("tests", [".cs"]),
    ];

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".vs", "node_modules", "TestResults"];

    /// <summary>
    /// Root-level shipped documents. The internal, untracked working notes are never shipped and
    /// explicitly not gated here: failing the build over a dash in a private planning file is a
    /// trap, not a safety net.
    /// </summary>
    private static readonly string[] ScannedRootFiles = ["README.md", "CHANGELOG.md", "NOTICE-brand-icons.md", "LICENSE"];

    [Fact]
    public void No_shipped_text_file_contains_an_em_dash_or_en_dash()
    {
        var root = RepoRoot();
        var findings = new List<string>();

        foreach (var file in ScannedFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (!HasDash(lines[i]))
                    continue;

                findings.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(findings.Count == 0, $"em dash or en dash found:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    /// <summary>A detector that never fires would keep this guard green for ever regardless of content.</summary>
    [Fact]
    public void The_scan_flags_a_known_dash()
    {
        var emDashSample = "this reads fine" + EmDash + "until it does not";
        var enDashSample = "2020" + EnDash + "2024";
        var cleanSample = "this reads fine, plain hyphen - and all";

        Assert.True(HasDash(emDashSample));
        Assert.True(HasDash(enDashSample));
        Assert.False(HasDash(cleanSample));
    }

    private static bool HasDash(string line) => line.IndexOf(EmDash) >= 0 || line.IndexOf(EnDash) >= 0;

    private static IEnumerable<string> ScannedFiles(string root)
    {
        foreach (var name in ScannedRootFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                yield return path;
        }

        foreach (var (scannedRoot, extensions) in ScannedDirectories)
        {
            var directory = Path.Combine(root, scannedRoot);
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                    continue;

                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;

                yield return file;
            }
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
