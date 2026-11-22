using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Decides whether a package a package manager lists and a program the registry lists are the same installation, so the
/// uninstaller shows it once. The rule is exact on purpose: the normalized names are equal, and the normalized publishers
/// are equal when BOTH are known. A package manager rarely knows a publisher, so an unknown one on either side leaves the
/// name alone to decide; two different known publishers never match, however alike the names are.
/// </summary>
public static partial class InstalledPackageDuplicates
{
    public static bool IsDuplicate(string? nameA, string? publisherA, string? nameB, string? publisherB)
    {
        var name = Normalize(nameA);
        if (name.Length == 0 || !string.Equals(name, Normalize(nameB), StringComparison.Ordinal))
            return false;

        var first = Normalize(publisherA);
        var second = Normalize(publisherB);
        return first.Length == 0 || second.Length == 0 || string.Equals(first, second, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shared text normalization for names and publishers: compatibility-normalized (NFKC), the registered, trademark
    /// and copyright marks (and their "(R)" / "(TM)" spellings) dropped, non-breaking spaces made plain, runs of white
    /// space collapsed, compared without regard to case.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // The marks go first, as a space so "Intel(R)Wireless" stays two words: NFKC would turn the trademark sign into the letters "TM".
        var normalized = MarkSpellings().Replace(WithoutLoneSurrogates(text), " ").Normalize(NormalizationForm.FormKC).Replace('\u00A0', ' ');
        return Whitespace().Replace(normalized, " ").Trim().ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>A registry value can hold an unpaired surrogate, which Unicode normalization rejects with an exception; it becomes a space.</summary>
    private static string WithoutLoneSurrogates(string text)
    {
        char[]? fixedText = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                fixedText ??= text.ToCharArray();
                fixedText[i] = ' ';
            }
        }

        return fixedText is null ? text : new string(fixedText);
    }

    [GeneratedRegex(@"[\u00AE\u2122\u00A9]|\((?:r|tm|c)\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MarkSpellings();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Whitespace();
}
