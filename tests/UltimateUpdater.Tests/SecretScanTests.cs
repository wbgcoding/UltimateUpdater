using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// A credential committed once is public for ever, and this repository is meant to become public.
/// The scan looks for secret SHAPES, never for the words "token"/"key"/"password": the source legitimately
/// talks about DPAPI, and names variables <c>token</c>; none of it embeds a credential.
/// </summary>
public class SecretScanTests
{
    /// <summary>
    /// Only what is checked in and shipped, build output carries copies of everything else. The repository
    /// root is scanned too, without recursing: <c>build.bat</c> lives there.
    /// </summary>
    private static readonly string[] ScannedRoots = ["src", "tools", "installer"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".vs", "node_modules", "TestResults"];

    /// <summary>Images and compiled artefacts are not text; reading them would only produce noise.</summary>
    private static readonly string[] SkippedExtensions =
        [".png", ".ico", ".jpg", ".jpeg", ".gif", ".dll", ".exe", ".pdb", ".zip", ".resources"];

    /// <summary>Key material has no place in a source tree whatever it contains, so the name alone is the finding.</summary>
    private static readonly string[] KeyMaterialExtensions = [".pfx", ".p12", ".snk", ".pem", ".key", ".jks", ".keystore"];

    private static readonly (string Name, Regex Pattern)[] Detectors =
    [
        ("GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b")),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b")),
        ("Slack token", new Regex(@"\bxox[baprs]-[A-Za-z0-9\-]{10,}")),
        ("PEM private key", new Regex(@"-----BEGIN[A-Z ]*PRIVATE KEY-----")),
        // The value class excludes the characters every templating form uses ({}, <>, $, %), so an
        // interpolated or formatted header cannot match; PlaceholderValues covers the plain-word rest.
        ("Authorization: Bearer literal", new Regex(@"Authorization\s*:\s*Bearer\s+(?<value>[A-Za-z0-9._\-]{20,})")),
    ];

    private static readonly string[] PlaceholderValues =
        ["example", "placeholder", "your", "token", "secret", "changeme", "redacted", "xxxx", "0000", "aaaa"];

    [Fact]
    public void No_checked_in_file_contains_anything_shaped_like_a_secret()
    {
        var root = RepoRoot();
        var findings = new List<string>();

        foreach (var file in ScannedFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
                foreach (var name in Findings(lines[i]))
                    findings.Add($"{relative}:{i + 1}: {name}");
        }

        // The location and the kind are enough to act on; echoing the match would copy a live credential
        // into every build log that ever runs this test.
        Assert.True(findings.Count == 0, $"secret-shaped values found:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Fact]
    public void No_key_material_file_is_checked_in()
    {
        var root = RepoRoot();

        var keyFiles = ScannedFiles(root, skipBinaries: false)
            .Where(f => KeyMaterialExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(keyFiles.Count == 0, $"key material in the source tree:{Environment.NewLine}{string.Join(Environment.NewLine, keyFiles)}");
    }

    /// <summary>
    /// A detector that matches nothing would keep the scan above green for ever. The samples are assembled
    /// at run time so no file in this repository ever holds a string a push-protection scanner would flag.
    /// </summary>
    [Fact]
    public void Every_detector_still_recognises_its_own_secret()
    {
        Assert.Equal(["GitHub token"], Findings("var t = \"gh" + "p_" + new string('A', 36) + "\";"));
        Assert.Equal(["Google API key"], Findings("key=AI" + "za" + new string('B', 35)));
        Assert.Equal(["Slack token"], Findings("xo" + "xb-" + new string('C', 12)));
        Assert.Equal(["PEM private key"], Findings("-----BE" + "GIN RSA PRIVATE KEY-----"));
        Assert.Equal(["Authorization: Bearer literal"], Findings("Authorization: Bearer " + new string('D', 40)));
    }

    [Theory]
    [InlineData("var token = await GetTokenAsync();")]
    [InlineData("ProcessRunner.RunAsync(\"gh\", [\"auth\", \"token\"], ...)")]
    [InlineData("/// Protected with DPAPI, DataProtectionScope.CurrentUser, never a plaintext key.")]
    [InlineData("request.Headers.Authorization = new AuthenticationHeaderValue(\"Bearer\", token);")]
    [InlineData("Authorization: Bearer {token}")]
    [InlineData("Authorization: Bearer <your-token-here>")]
    [InlineData("Authorization: Bearer YOUR_TOKEN_PLACEHOLDER_VALUE")]
    [InlineData("private const string ApiKey = nameof(ApiKey);")]
    public void Ordinary_source_lines_are_not_secrets(string line) => Assert.Empty(Findings(line));

    /// <summary>
    /// A path out of someone's profile carries an account name, and this repository is meant to become
    /// public. Tracked files are exactly the set that would be published, so git defines the scope rather
    /// than a hand-maintained copy of <c>.gitignore</c>: internal notes are untracked and stay out of it.
    /// Fixtures need such paths, so the account segment is compared against the placeholder names the
    /// tests already use, anything else is a real profile and fails.
    /// </summary>
    [Fact]
    public void No_published_file_carries_a_real_user_profile_path()
    {
        var root = RepoRoot();
        var findings = new List<string>();

        foreach (var relative in TrackedTextFiles(root))
        {
            var lines = File.ReadAllLines(Path.Combine(root, relative));

            for (var i = 0; i < lines.Length; i++)
                foreach (var account in ProfilePaths(lines[i]))
                    findings.Add($"{relative}:{i + 1}: profile path for '{account}'");
        }

        Assert.True(findings.Count == 0,
            $"tracked files carry a real user profile path:{Environment.NewLine}{string.Join(Environment.NewLine, findings)}");
    }

    [Theory]
    [InlineData(@"var p = @""C:\Users\someone\AppData\Local"";")]
    [InlineData(@"D:\Users\TestUser2\Documents")]
    [InlineData(@"C:\Users\Public\Desktop")]
    [InlineData("rem (decided 2026-07-23: build.bat must ALWAYS fill dist\\)")]
    public void Placeholder_profile_paths_are_allowed(string line) => Assert.Empty(ProfilePaths(line));

    [Fact]
    public void A_real_looking_profile_path_is_still_caught()
    {
        Assert.Equal(["mmustermann"], ProfilePaths(@"var p = @""C:\Users\mmustermann\Desktop\notes.txt"";"));
        Assert.Equal(["mmustermann"], ProfilePaths(@"var p = ""C:\\Users\\mmustermann\\Desktop"";"));
    }

    // \\{1,2}: a regular C# string literal writes every backslash doubled, which a single-backslash
    // pattern never matched.
    private static readonly Regex ProfilePath = new(@"[A-Za-z]:\\{1,2}Users\\{1,2}(?<account>[A-Za-z0-9._\-]+)");

    /// <summary>Account names that name nobody, Windows' own, plus the ones the fixtures invent.</summary>
    private static readonly string[] PlaceholderAccounts =
        ["public", "default", "all users", "user", "username", "someone", "somebody", "me", "other",
         "test", "testuser", "testuser2", "benutzer", "x", "y", "z"];

    private static IEnumerable<string> ProfilePaths(string line)
    {
        foreach (Match match in ProfilePath.Matches(line))
        {
            var account = match.Groups["account"].Value;
            if (!PlaceholderAccounts.Contains(account, StringComparer.OrdinalIgnoreCase))
                yield return account;
        }
    }

    /// <summary>Everything git tracks, minus what is not text, the published set, straight from git.</summary>
    private static IEnumerable<string> TrackedTextFiles(string root)
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
            .Where(file => !SkippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Where(file => File.Exists(Path.Combine(root, file)))
            // This file has to spell the forbidden words out to detect them, and it is the one place
            // where they are the subject rather than a leak.
            .Where(file => !file.EndsWith(nameof(SecretScanTests) + ".cs", StringComparison.Ordinal))
            .ToList();

        // A silent empty listing would make this test pass for ever without reading a single line.
        Assert.True(tracked.Count > 50, $"only {tracked.Count} tracked text files found; the listing is not trustworthy");
        return tracked;
    }

    private static IEnumerable<string> Findings(string line)
    {
        foreach (var (name, pattern) in Detectors)
        {
            var match = pattern.Match(line);
            if (!match.Success)
                continue;

            var value = match.Groups["value"];
            if (value.Success && PlaceholderValues.Any(p => value.Value.Contains(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            yield return name;
        }
    }

    private static IEnumerable<string> ScannedFiles(string root, bool skipBinaries = true)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (!skipBinaries || !SkippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                yield return file;
        }

        foreach (var scanned in ScannedRoots)
        {
            var directory = Path.Combine(root, scanned);
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => SkippedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                    continue;

                if (skipBinaries && SkippedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
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
