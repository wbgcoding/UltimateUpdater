using System.Text.RegularExpressions;

namespace UltimateUpdater.Tests;

/// <summary>
/// Every user-visible string must go through localization, a literal
/// <c>Text="Some words"</c> ships in whatever language the developer typed it in, forever, in every
/// locale. A scan over every .xaml found exactly the intentional exceptions below (a
/// separator, an arrow, the eleven language names in the language picker itself, which
/// must show their OWN name, not a translation of it); this test fails the moment a new hardwired
/// string appears anywhere, forcing a deliberate decision (translate it, or add it here with a reason)
/// instead of a silent slip.
/// </summary>
public class HardwiredTextTests
{
    /// <summary>Attributes that render literal text to the user. Not scanned: Style/converter/resource
    /// names, x:Name, numeric or enum values, those live under different attribute names entirely.
    /// The value must start with something other than whitespace (an empty or blank literal is not
    /// visible text), it is deliberately NOT restricted to a letter: a digit- or punctuation-led
    /// literal (<c>Text="3 Updates"</c>, <c>Text="!Warning"</c>) is hardwired text too and used to slip
    /// past this scan.</summary>
    private static readonly Regex LiteralTextAttribute =
        new(@"\b(Text|Content|Header|ToolTip|PlaceholderText)=""([^\s""{][^""{]*)""");

    /// <summary>Known-fine, reviewed 22.08., each entry is "file:the exact matched attribute+value".
    /// Adding to this list is a deliberate, reviewed exception, not a default.</summary>
    private static readonly string[] ApprovedExceptions =
    [
        // The language picker must show each language's OWN name, never a translation of it, an
        // English UI showing "Deutsch" translated to "German" would defeat the point of the control.
        "SettingsWindow.xaml: Content=\"English\"",
        "SettingsWindow.xaml: Content=\"Deutsch\"",
        // The nine additional languages, same reason as the two entries above.
        "SettingsWindow.xaml: Content=\"Español\"",
        "SettingsWindow.xaml: Content=\"Italiano\"",
        "SettingsWindow.xaml: Content=\"Português\"",
        "SettingsWindow.xaml: Content=\"Polski\"",
        "SettingsWindow.xaml: Content=\"中文（简体）\"",
        "SettingsWindow.xaml: Content=\"日本語\"",
        "SettingsWindow.xaml: Content=\"Nederlands\"",
        "SettingsWindow.xaml: Content=\"Türkçe\"",
        "SettingsWindow.xaml: Content=\"Română\"",
        // Punctuation and symbols, not language-dependent words, a version-arrow glyph and
        // a breadcrumb separator read identically in every locale. Only visible now that the scan's
        // starting-character restriction (a letter) was lifted; these were always here.
        "MainWindow.xaml: Text=\"›\"",
        "MainWindow.xaml: Text=\"→ \"",
        "MainWindow.xaml: Text=\"→\"",
        // HistoryWindow's version lane gained the same fixed arrow cell the main table's own
        // version lane already has (see the entry directly above), for the same reason.
        "HistoryWindow.xaml: Text=\"→\"",
        // The cleanup dialog's own loading placeholder, the same plain ellipsis its per-row
        // measuring state already shows elsewhere in this window (built in code, so this scan never saw
        // it there), punctuation, not a language-dependent word.
        "CleanupWindow.xaml: Text=\"…\"",
    ];

    [Fact]
    public void No_hardwired_visible_text_appears_without_a_reviewed_exception()
    {
        var viewsDirectory = FindViewsDirectory();
        var approved = ApprovedExceptions.ToHashSet(StringComparer.Ordinal);
        var unexpected = new List<string>();

        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);
            var xaml = File.ReadAllText(file);

            foreach (var entry in FindUnapproved(fileName, xaml, approved))
                unexpected.Add(entry);
        }

        Assert.True(unexpected.Count == 0,
            $"hardwired text found without a reviewed exception:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}");
    }

    /// <summary>Proves the regex actually catches a digit- or punctuation-led literal, the exact shape
    /// the old pattern (starting character restricted to a letter) let through unnoticed, run against a
    /// synthetic fixture, never against a real shipped .xaml (that would BE the hardwired-text bug this
    /// scan exists to prevent, not a test of it).</summary>
    [Theory]
    [InlineData("<TextBlock Text=\"3 Updates\"/>")]
    [InlineData("<TextBlock Text=\"!Warning\"/>")]
    public void A_digit_or_punctuation_led_literal_is_caught(string fixtureXaml)
    {
        var unexpected = FindUnapproved("Fixture.xaml", fixtureXaml, new HashSet<string>()).ToList();

        Assert.NotEmpty(unexpected);
    }

    private static IEnumerable<string> FindUnapproved(string fileName, string xaml, ISet<string> approved)
    {
        foreach (Match match in LiteralTextAttribute.Matches(xaml))
        {
            var entry = $"{fileName}: {match.Groups[1].Value}=\"{match.Groups[2].Value}\"";
            if (!approved.Contains(entry))
                yield return entry;
        }
    }

    [Theory]
    [InlineData("English")]
    [InlineData("Deutsch")]
    [InlineData("Español")]
    [InlineData("Italiano")]
    [InlineData("Português")]
    [InlineData("Polski")]
    [InlineData("中文（简体）")]
    [InlineData("日本語")]
    [InlineData("Nederlands")]
    [InlineData("Türkçe")]
    [InlineData("Română")]
    public void Every_approved_exception_still_matches_something_real(string expectedWord)
    {
        // Guards the exception list itself from going stale: if SettingsWindow.xaml ever stops
        // containing this literal (renamed, removed, converted to a binding), the exception is dead
        // weight that would silently hide a REAL new hardwired string added elsewhere in its place.
        var viewsDirectory = FindViewsDirectory();
        var xaml = File.ReadAllText(Path.Combine(viewsDirectory, "SettingsWindow.xaml"));
        Assert.Contains($"Content=\"{expectedWord}\"", xaml, StringComparison.Ordinal);
    }

    private static string FindViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Views directory from the test output path.");
    }

    // ===== The same hardwired-text problem, but in .cs code-behind instead of .xaml markup.
    // Log lines are deliberately NOT scanned, they stay English on purpose and never reach the UI. =====

    /// <summary>A string-literal argument to one of <see cref="Dialogs"/>' user-facing methods (every
    /// parameter on every one of them is shown to the user, there is no "internal" parameter to skip),
    /// or to the tray's balloon notification (<c>TrayIconService.ShowBalloon</c> and the underlying
    /// <c>NotifyIcon.ShowBalloonTip</c>). A caller that passes a local variable or a <c>Loc[...]</c>
    /// lookup instead never matches this at all, only a literal string typed directly into the call is a
    /// hardwired-text bug.</summary>
    private static readonly Regex DialogCallLiteral =
        new(@"\b(?:Dialogs\.(?:Info|Warn|Error|Success|Confirm|ConfirmThreeWay)|ShowBalloon(?:Tip)?)\s*\(([^;]*?)\)\s*;", RegexOptions.Singleline);

    /// <summary>A literal assigned straight to a bound text-carrying property, anything named
    /// <c>*Text</c>/<c>*Message</c> (this project's own naming convention for such properties, e.g.
    /// <c>StatusText</c>, <c>ErrorMessage</c>) or <c>ToolTip</c>.</summary>
    private static readonly Regex PropertyAssignmentLiteral =
        new(@"\b\w*(?:Text|Message|ToolTip)\s*=\s*""([^""{][^""]*)""");

    /// <summary>Reviewed exceptions for the .cs scan, same idea as <see cref="ApprovedExceptions"/> for
    /// .xaml: a deliberate, reviewed non-translatable literal, never a default.</summary>
    private static readonly string[] ApprovedCSharpExceptions =
    [
        // A duration placeholder ("0 minutes, 0 seconds"), not language-dependent words - the same class
        // of exception as the XAML side's punctuation glyphs.
        "MainViewModel.Install.cs: installElapsedText = \"0:00\"",
    ];

    /// <summary>A resource-key indexer lookup, <c>Loc["Key"]</c>/<c>loc["Key"]</c> or the fully spelled
    /// out <c>LocalizationService.Instance["Key"]</c>, stripped out before the literal scan runs (see
    /// <see cref="FindUnapprovedCSharp"/>), not just skipped in place: its quotes would otherwise pair up
    /// with unrelated quotes elsewhere in an interpolated string (<c>$"...{loc["Key"]}..."</c> has a real
    /// closing quote AFTER the hole that a naive left-to-right quote-pairing scan pairs with the WRONG
    /// opening quote once the inner pair is consumed first).</summary>
    private static readonly Regex LocKeyLookup = new(@"[\w.]+\[\s*""[^""]*""\s*\]");

    /// <summary>A plain string literal, once every <see cref="LocKeyLookup"/> has already been stripped.</summary>
    private static readonly Regex StringLiteral = new(@"""([^""{][^""]*)""");

    [Fact]
    public void No_hardwired_visible_text_appears_in_cs_dialog_calls_or_status_assignments()
    {
        var srcDirectory = FindSrcDirectory();
        var approved = ApprovedCSharpExceptions.ToHashSet(StringComparer.Ordinal);
        var unexpected = new List<string>();

        foreach (var file in Directory.EnumerateFiles(srcDirectory, "*.cs", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = Path.GetFileName(file);
            var code = File.ReadAllText(file);

            foreach (var entry in FindUnapprovedCSharp(fileName, code, approved))
                unexpected.Add(entry);
        }

        Assert.True(unexpected.Count == 0,
            $"hardwired text found in .cs without a reviewed exception:{Environment.NewLine}{string.Join(Environment.NewLine, unexpected)}");
    }

    /// <summary>Same red-before-green proof as <see cref="A_digit_or_punctuation_led_literal_is_caught"/>
    /// above, for the .cs scan, run against synthetic snippets only, never real shipped code.</summary>
    [Theory]
    [InlineData("Dialogs.Warn(\"Hardwired headline\");")]
    [InlineData("Dialogs.Confirm(\"Hardwired headline\", body);")]
    [InlineData("StatusText = \"Hardwired status\";")]
    [InlineData("button.ToolTip = \"Hardwired tooltip\";")]
    [InlineData("_tray.ShowBalloon(\"Hardwired title\", message);")]
    [InlineData("ErrorMessage = \"Hardwired error\";")]
    public void A_hardwired_cs_literal_is_caught(string fixtureCode)
    {
        var unexpected = FindUnapprovedCSharp("Fixture.cs", fixtureCode).ToList();

        Assert.NotEmpty(unexpected);
    }

    /// <summary>The scan must NOT flag a variable, a Loc[...] lookup, or a log call, only a literal typed
    /// straight into a Dialogs.*/ShowBalloon call or a *Text/*Message/ToolTip assignment is the bug this
    /// test guards.</summary>
    [Theory]
    [InlineData("Dialogs.Warn(headline, body);")]
    [InlineData("Dialogs.Info(Loc[\"SomeKey\"]);")]
    [InlineData("_logging.Warn(\"Retry failed, giving up\");")]
    [InlineData("StatusText = statusKey;")]
    [InlineData("_tray.ShowBalloon(title, message);")]
    public void A_non_literal_or_log_call_is_not_flagged(string fixtureCode)
    {
        var unexpected = FindUnapprovedCSharp("Fixture.cs", fixtureCode).ToList();

        Assert.Empty(unexpected);
    }

    private static IEnumerable<string> FindUnapprovedCSharp(string fileName, string code, ISet<string>? approved = null)
    {
        approved ??= new HashSet<string>();

        foreach (Match call in DialogCallLiteral.Matches(code))
        {
            var args = LocKeyLookup.Replace(call.Groups[1].Value, "LOCKEY");
            foreach (Match literal in StringLiteral.Matches(args))
            {
                var entry = $"{fileName}: Dialogs.*/ShowBalloon(...) literal \"{literal.Groups[1].Value}\"";
                if (!approved.Contains(entry))
                    yield return entry;
            }
        }

        foreach (Match assignment in PropertyAssignmentLiteral.Matches(code))
        {
            var entry = $"{fileName}: {assignment.Value}";
            if (!approved.Contains(entry))
                yield return entry;
        }
    }

    /// <summary>Two interpolation holes joined by "; " is what the dash removal left behind in window
    /// titles and status lines ("Git; Changelog", "NVIDIA; checking for viruses"); a label and its status
    /// are joined by a colon, as the error detail window already does.</summary>
    [Fact]
    public void No_visible_label_is_joined_to_its_text_by_a_semicolon()
    {
        var pattern = new Regex(@"\$""\{[^}""]+\}; \{");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(FindSrcDirectory(), "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0, "label joined by a semicolon: " + string.Join(", ", offenders));
    }

    private static string FindSrcDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the src/UltimateUpdater directory from the test output path.");
    }
}
