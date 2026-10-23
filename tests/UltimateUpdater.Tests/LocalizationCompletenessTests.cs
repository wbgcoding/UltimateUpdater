using System.Xml.Linq;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Guards the i18n rule that both locales carry the same keys. A string added to one file and
/// forgotten in the other would otherwise only surface as English text in a German UI.
/// </summary>
public class LocalizationCompletenessTests
{
    private static readonly string ResourcesDirectory = FindResourcesDirectory();

    /// <summary>
    /// Every <c>Strings.*.resx</c> next to the neutral file (German, and every further locale added later)
    /// must carry the same key set as the neutral one, checked in BOTH directions, so
    /// a tenth language is covered automatically without touching this file again: <see cref="LocaleFiles"/>
    /// discovers the files on disk instead of naming them one by one.
    /// </summary>
    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void Locale_defines_every_key_the_neutral_locale_does(string fileName)
    {
        var neutral = ReadKeys("Strings.resx");
        var locale = ReadKeys(fileName);

        var missing = neutral.Except(locale).Order().ToList();

        Assert.True(missing.Count == 0, $"{fileName} is missing translations: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The other direction of <see cref="Locale_defines_every_key_the_neutral_locale_does"/>: a key removed
    /// from <c>Strings.resx</c> cannot quietly go on living in a locale file without anything catching it.
    /// </summary>
    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void Locale_defines_no_keys_the_neutral_locale_lacks(string fileName)
    {
        var neutral = ReadKeys("Strings.resx");
        var locale = ReadKeys(fileName);

        var extra = locale.Except(neutral).Order().ToList();

        Assert.True(extra.Count == 0, $"{fileName} keys without a neutral counterpart: {string.Join(", ", extra)}");
    }

    [Fact]
    public void No_value_is_left_empty()
    {
        foreach (var file in new[] { "Strings.resx" }.Concat(LocaleFileNames()))
        {
            var empty = ReadEntries(file)
                .Where(e => string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Key)
                .ToList();

            Assert.True(empty.Count == 0, $"{file} has empty values: {string.Join(", ", empty)}");
        }
    }

    /// <summary>Placeholders like {0} must match, or a formatted string throws at runtime in one locale
    /// only. Compares the actual SET of placeholder tokens, not just how many distinct ones each string
    /// has, two strings can both use exactly two distinct placeholders while using DIFFERENT indices
    /// (English "{0}...{1}", German "{1}...{2}"), which a bare count comparison cannot tell apart, and
    /// "{2}" with only two format arguments supplied is exactly the runtime FormatException this test
    /// exists to prevent. Runs over every discovered locale file, not just German, so a tenth language
    /// is covered automatically.</summary>
    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void Format_placeholders_match_between_locales(string fileName)
    {
        var neutral = ReadEntries("Strings.resx");
        var locale = ReadEntries(fileName);

        var mismatched = new List<string>();

        foreach (var (key, value) in neutral)
        {
            if (!locale.TryGetValue(key, out var translated))
                continue;

            if (!PlaceholderSet(value).SetEquals(PlaceholderSet(translated)))
                mismatched.Add(key);
        }

        Assert.True(mismatched.Count == 0, $"{fileName} placeholder mismatch: {string.Join(", ", mismatched)}");
    }

    /// <summary>Where the English text uses the ellipsis character ("Analysing…"), a translation must not
    /// fall back to three ASCII dots, which render wider and read differently from every other locale.</summary>
    [Theory]
    [MemberData(nameof(LocaleFiles))]
    public void A_translation_keeps_the_ellipsis_character(string fileName)
    {
        var neutral = ReadEntries("Strings.resx");
        var locale = ReadEntries(fileName);

        var dotted = neutral
            .Where(e => e.Value.Contains('…') && locale.TryGetValue(e.Key, out var value) && value.Contains("..."))
            .Select(e => e.Key)
            .Order()
            .ToList();

        Assert.True(dotted.Count == 0, $"{fileName} uses \"...\" instead of \"…\": {string.Join(", ", dotted)}");
    }

    /// <summary>Proves the set comparison catches an index mismatch that an equal-COUNT comparison would
    /// miss: both fixtures use exactly two distinct placeholders, just different ones.</summary>
    [Fact]
    public void An_index_mismatch_with_the_same_placeholder_count_is_still_caught()
    {
        var english = "{0} of {1}";
        var german = "{1} von {2}";

        Assert.Equal(CountPlaceholders(english), CountPlaceholders(german));
        Assert.False(PlaceholderSet(english).SetEquals(PlaceholderSet(german)));
    }

    [Fact]
    public void A_format_specifier_mismatch_is_caught()
    {
        Assert.False(PlaceholderSet("{0:N0} files").SetEquals(PlaceholderSet("{0} Dateien")));
        Assert.True(PlaceholderSet("{0:N0} files").SetEquals(PlaceholderSet("{0:N0} Dateien")));
    }

    /// <summary>
    /// A key that does not exist renders as the key name itself, no exception, no build error, just
    /// "SettingsAutostrt" sitting in the UI. Every key the app asks for is checked against the resx.
    /// </summary>
    [Fact]
    public void Every_key_the_app_asks_for_exists()
    {
        var defined = ReadKeys("Strings.resx");
        var sourceRoot = Path.GetDirectoryName(ResourcesDirectory)!;

        var used = new HashSet<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".xaml"))
                continue;

            var text = File.ReadAllText(file);

            // XAML: {Binding Loc[Key]}, C#: Loc["Key"] and LocalizationService.Instance["Key"]
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"Loc\[""?([A-Za-z0-9_]+)""?\]"))
            {
                used.Add(match.Groups[1].Value);
            }

            // A local alias (var loc = LocalizationService.Instance; loc["Key"]). Quoted only: an unquoted
            // loc[...] in C# is an ordinary index expression, not a key.
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"\bloc\[""([A-Za-z0-9_]+)""\]"))
            {
                used.Add(match.Groups[1].Value);
            }

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, @"LocalizationService\.Instance\[""([A-Za-z0-9_]+)""\]"))
            {
                used.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);

        var undefined = used.Except(defined).Order().ToList();

        Assert.True(undefined.Count == 0, $"Keys used but never defined: {string.Join(", ", undefined)}");
    }

    /// <summary>
    /// Every catalogue target's TitleKey/DescriptionKey/ConsequenceKey is read through
    /// LocalizationService.Instance[Target.XxxKey], a DYNAMIC indexer the broad scan above can never see
    /// (it only matches literal Loc["Key"]/Instance["Key"] text), so a typo'd catalogue key name would
    /// otherwise never be caught by any test, it would just quietly show the raw key as UI text.
    /// </summary>
    [Fact]
    public void Every_cleanup_targets_localization_keys_exist_in_both_languages()
    {
        var en = ReadKeys("Strings.resx");
        var de = ReadKeys("Strings.de.resx");

        var missing = new List<string>();
        foreach (var target in CleanupCatalog.Build())
        {
            foreach (var key in new[] { target.TitleKey, target.DescriptionKey, target.ConsequenceKey })
            {
                if (!en.Contains(key))
                    missing.Add($"{target.Id}: '{key}' missing from Strings.resx");
                if (!de.Contains(key))
                    missing.Add($"{target.Id}: '{key}' missing from Strings.de.resx");
            }
        }

        Assert.True(missing.Count == 0, string.Join("; ", missing));
    }

    /// <summary>
    /// The counterpart to <see cref="Every_key_the_app_asks_for_exists"/>: a key defined in the resx but
    /// never read anywhere is dead weight the compiler cannot see either (three
    /// such keys, `CleanupLeftoverInstalls[Desc]`/`CleanupLeftoverNoneFound`, were found and removed).
    /// Scans every .cs/.xaml file for the key's NAME appearing anywhere as a quoted literal, not only
    /// inside a <c>Loc[...]</c>/<c>Instance[...]</c> indexer, because several call sites route through a
    /// variable (<c>InstallErrorExplainer</c>'s tables, <c>ProviderStatusViewModel</c>'s stored key,
    /// <c>SourceNames</c>, ...) whose own literal lives at a DIFFERENT point in the source than the one
    /// that reads it. The one genuinely dynamic key shape in the whole codebase,
    /// <c>UpdateRowViewModel.CategoryDisplay</c> builds <c>"Category" + an UpdateCategory value</c>,
    /// never spelling the result out as a literal anywhere, is computed from the enum, not guessed at;
    /// that is the named exception list this test's own plan called for.
    /// </summary>
    [Fact]
    public void No_key_is_defined_without_a_user()
    {
        var defined = ReadKeys("Strings.resx");
        var sourceRoot = Path.GetDirectoryName(ResourcesDirectory)!;

        var used = new HashSet<string>();
        var haystack = new System.Text.StringBuilder();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            if (Path.GetExtension(file) is not (".cs" or ".xaml"))
                continue;

            var content = File.ReadAllText(file);
            haystack.Append(content).Append('\n');

            // XAML's {Binding Loc[Key]} carries no quotes around the key at all, only C#'s Loc["Key"] does.
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(content, @"Loc\[""?([A-Za-z0-9_]+)""?\]"))
                used.Add(match.Groups[1].Value);

            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(content, @"LocalizationService\.Instance\[""([A-Za-z0-9_]+)""\]"))
                used.Add(match.Groups[1].Value);
        }

        var text = haystack.ToString();
        foreach (var category in Enum.GetValues<UpdateCategory>())
            used.Add($"Category{category}");

        var unused = FindUnusedKeys(defined, used, text).Order().ToList();

        Assert.True(unused.Count == 0, $"Keys defined but never used: {string.Join(", ", unused)}");
    }

    /// <summary>Isolates the "is this key referenced anywhere" logic <see
    /// cref="No_key_is_defined_without_a_user"/> runs against the real resx/source tree, so it can be
    /// proven against a synthetic fixture instead, never against real shipped code, same discipline as
    /// <c>HardwiredTextTests</c>. Several real call sites route through a variable (InstallErrorExplainer's
    /// tables, ProviderStatusViewModel's stored key, SourceNames, ...) whose own literal lives at a
    /// DIFFERENT point in the source than the one that reads it, caught here by matching the key's NAME
    /// as a quoted literal anywhere in the combined source text, not only right after Loc[/Instance[.</summary>
    private static IEnumerable<string> FindUnusedKeys(IEnumerable<string> defined, HashSet<string> usedViaIndexer, string sourceText)
    {
        var used = new HashSet<string>(usedViaIndexer);

        foreach (var key in defined)
        {
            if (sourceText.Contains($"\"{key}\"", StringComparison.Ordinal))
                used.Add(key);
        }

        return defined.Except(used);
    }

    /// <summary>Red-before-green proof that <see cref="FindUnusedKeys"/> actually catches an orphaned key
    ///, a resource defined but referenced nowhere, and does not flag one that IS referenced, whether
    /// directly through the indexer or only as a bare quoted literal elsewhere in the source (the shape
    /// several real call sites use).</summary>
    [Fact]
    public void An_orphaned_key_is_caught_while_a_referenced_one_is_not()
    {
        var defined = new[] { "RealKeyDirect", "RealKeyViaVariable", "OrphanedBaitKey" };
        var usedViaIndexer = new HashSet<string> { "RealKeyDirect" };
        var sourceText = "var explainerTable = \"RealKeyViaVariable\";";

        var unused = FindUnusedKeys(defined, usedViaIndexer, sourceText).ToList();

        Assert.Equal(["OrphanedBaitKey"], unused);
    }

    /// <summary>No catalogue target may exist without a risk level. RiskLevel is a required constructor
    /// parameter, so the compiler already guarantees this, this test is the documented, discoverable
    /// proof of that guarantee.</summary>
    [Fact]
    public void Every_cleanup_target_has_a_defined_risk_level() =>
        Assert.All(CleanupCatalog.Build(), target => Assert.True(Enum.IsDefined(target.RiskLevel)));

    private static int CountPlaceholders(string value) => PlaceholderSet(value).Count;

    /// <summary>Whole format items, alignment and format string included: "{0:N0}" in English against
    /// "{0}" in a translation formats the number differently in that one language.</summary>
    private static HashSet<string> PlaceholderSet(string value) =>
        [.. System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+(?:,\s*-?\d+)?(?::[^{}]*)?\}").Select(m => m.Value)];

    private static HashSet<string> ReadKeys(string fileName) => [.. ReadEntries(fileName).Keys];

    private static Dictionary<string, string> ReadEntries(string fileName)
    {
        var document = XDocument.Load(Path.Combine(ResourcesDirectory, fileName));

        return document.Root!
            .Elements("data")
            .Where(e => e.Attribute("name") is not null)
            .ToDictionary(
                e => e.Attribute("name")!.Value,
                e => e.Element("value")?.Value ?? string.Empty);
    }

    /// <summary>Every <c>Strings.*.resx</c> next to the neutral file, discovered on disk instead of named
    /// one by one, so a locale file added later is picked up by the theories above without editing this
    /// class again.</summary>
    public static IEnumerable<object[]> LocaleFiles() =>
        LocaleFileNames().Select(fileName => new object[] { fileName });

    private static IEnumerable<string> LocaleFileNames() =>
        Directory.EnumerateFiles(ResourcesDirectory, "Strings.*.resx")
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null && fileName != "Strings.resx")
            .Select(fileName => fileName!)
            .OrderBy(fileName => fileName, StringComparer.Ordinal);

    private static string FindResourcesDirectory()
    {
        // walk up from the test binaries to the repository root
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Resources");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Resources directory from the test output path.");
    }
}
