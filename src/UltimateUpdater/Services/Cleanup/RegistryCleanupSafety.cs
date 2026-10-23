using System.IO;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Judges whether one registry value is a confidently-broken file reference: the same "never guess"
/// standard the rest of this project holds for everything else (rustup's date, Lenovo's CLI). Pure: no
/// registry access here, so every rule is unit-testable against a plain string and a fake existence check
/// rather than the machine's real registry.
/// </summary>
public static partial class RegistryCleanupSafety
{
    /// <summary>
    /// An unquoted value with a space can only be split at a boundary that also carries a known
    /// executable extension. Otherwise "path vs. argument" stays genuinely ambiguous and is never guessed.
    /// </summary>
    private static readonly string[] ExecutableExtensions = [".exe", ".dll", ".com", ".bat", ".cmd", ".msc", ".cpl"];

    /// <summary>
    /// True only when the value confidently names a file reference AND every candidate path it could mean
    /// is gone. An unparseable value (a command line with arguments we cannot safely split, a non-file
    /// reference, an empty value) is never "broken": the whole point is to delete only what we are SURE
    /// points at nothing, not to guess and risk taking a real reference with it.
    /// </summary>
    public static bool IsBrokenFileReference(string? rawValue, Func<string, bool> fileExists)
    {
        var path = ExtractFilePath(rawValue, fileExists);
        return path is not null && !fileExists(path);
    }

    /// <summary>
    /// Pulls a single, confident file path out of a raw registry value, or null when the value is not
    /// unambiguously one path. Handles three shapes: a quoted path (anything after the closing quote is an
    /// argument and is dropped, never inspected), a bare path with an optional ",&lt;icon index&gt;" suffix,
    /// and an UNQUOTED value that contains a space (e.g. <c>C:\Program Files\Foo\foo.exe --minimized</c>),
    /// classically ambiguous since Windows itself cannot tell "path with a space" from "path plus argument"
    /// without probing the filesystem. For that last shape every prefix that ends on a token boundary
    /// (end of string, or the next character is a space) AND carries a known executable extension is a
    /// candidate; the first one <paramref name="fileExists"/> confirms wins (existence anywhere means this
    /// is a real, live reference), and only when NONE of them exist is the longest candidate returned so the
    /// caller can judge the whole value broken. Proving existence can therefore only ever keep an entry;
    /// only proving the absence of every candidate can mark it broken.
    /// </summary>
    public static string? ExtractFilePath(string? rawValue, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var value = rawValue.Trim();

        if (value.StartsWith('"'))
        {
            var closing = value.IndexOf('"', 1);
            if (closing <= 1)
                return null; // an unterminated quote is not a value we understand

            var quoted = value[1..closing];
            return quoted.Length > 0 && IsConfidentlyQualified(quoted) ? quoted : null;
        }

        if (!value.Contains(' '))
        {
            var stripped = UninstallEntryPaths.StripIconIndex(value);

            // StripIconIndex only strips a ",<index>" suffix when something real precedes the comma. A
            // bare ",0" (no path at all) comes back unchanged, still starting with the comma. That is not
            // a path either, so it is rejected here rather than treated as one.
            if (stripped.Length == 0 || stripped.StartsWith(','))
                return null;

            return IsConfidentlyQualified(stripped) ? stripped : null;
        }

        var candidates = new List<string>();
        for (var i = 0; i < value.Length; i++)
        {
            var atTokenBoundary = i == value.Length - 1 || value[i + 1] == ' ';
            if (!atTokenBoundary)
                continue;

            var candidate = value[..(i + 1)];
            if (ExecutableExtensions.Any(ext => candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                && IsConfidentlyQualified(candidate))
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return null; // no plausible executable boundary anywhere in the value - stay silent

        // Candidates were built shortest-first (left to right), so the first one that exists is also the
        // FIRST in reading order, and the last one is the longest - exactly the two the contract asks for.
        return candidates.FirstOrDefault(fileExists) ?? candidates[^1];
    }

    [GeneratedRegex(
        @"^""?msiexec(\.exe)?""?\s+/[xi]\s*(\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\})",
        RegexOptions.IgnoreCase)]
    private static partial Regex MsiExecCommandRegex();

    /// <summary>
    /// Pulls the product code GUID out of an MSI uninstall command (<c>MsiExec.exe /X{GUID}</c> or
    /// <c>/I{GUID}</c>), or null when the command is not that shape. The caller then judges the entry by
    /// whether that product code is still registered, not by file existence: an MSI-based uninstall
    /// command is a genuine reference to a product record, not a file path.
    /// </summary>
    public static string? ExtractMsiProductCode(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString))
            return null;

        var match = MsiExecCommandRegex().Match(uninstallString.Trim());
        return match.Success ? match.Groups[2].Value : null;
    }

    /// <summary>
    /// Windows' "compressed GUID" representation, used as the key name under
    /// <c>...\Installer\Products\&lt;packed&gt;</c>: each of the first three GUID fields byte-order
    /// reversed, the last two fields left in byte order but with each byte's own two hex digits swapped.
    /// Pure string transform, no registry access: this is the standard Windows Installer packing
    /// algorithm, not something invented here. Null for anything that is not a well-formed GUID.
    /// </summary>
    public static string? PackProductCode(string? guid)
    {
        if (guid is null || !Guid.TryParse(guid, out var parsed))
            return null;

        var hex = parsed.ToString("N");
        string[] fields = [hex[..8], hex[8..12], hex[12..16], hex[16..20], hex[20..32]];

        return ReverseByteOrder(fields[0]) + ReverseByteOrder(fields[1]) + ReverseByteOrder(fields[2]) +
               SwapNibblesPerByte(fields[3]) + SwapNibblesPerByte(fields[4]);
    }

    /// <summary>Reverses the order of whole bytes in a hex string (each byte's own two hex digits stay
    /// together and in order). reversing the characters flips both byte order AND the nibbles within each
    /// byte, so the adjacent-pair swap afterwards undoes only the nibble flip.</summary>
    private static string ReverseByteOrder(string hex)
    {
        var chars = hex.ToCharArray();
        Array.Reverse(chars);
        SwapAdjacentPairsInPlace(chars);
        return new string(chars);
    }

    /// <summary>Swaps the two hex digits within each byte, keeping the bytes themselves in the same order.</summary>
    private static string SwapNibblesPerByte(string hex)
    {
        var chars = hex.ToCharArray();
        SwapAdjacentPairsInPlace(chars);
        return new string(chars);
    }

    private static void SwapAdjacentPairsInPlace(char[] chars)
    {
        for (var i = 0; i < chars.Length; i += 2)
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
    }

    /// <summary>
    /// A bare, unqualified name like <c>mscoree.dll</c> is a perfectly valid COM registration:
    /// Windows resolves it through the DLL search path, never through a literal path this app could check.
    /// Judging it "missing" because no such file sits in whatever the current working directory happens to
    /// be would delete a working registration. Only a FULLY qualified path (a drive letter or a UNC share,
    /// see <see cref="Path.IsPathFullyQualified(string)"/>) is confident enough to check; anything else,
    /// including a drive-relative <c>\Windows\System32\x.dll</c>, is ambiguous and never deletable.
    /// </summary>
    private static bool IsConfidentlyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The multi-field version of <see cref="IsBrokenFileReference"/>, for an entry that offers more than
    /// one place it could still be reachable from (a CLSID's in-proc AND local server, an uninstall entry's
    /// icon path AND uninstall command). True only when every non-empty value present extracts to a
    /// confident path AND every one of those paths is gone. One field that cannot be confidently parsed
    /// aborts the whole judgment rather than being silently dropped. Dropping it is what let "one
    /// ambiguous, one genuinely broken" field read as a false "everything is broken".
    /// </summary>
    public static bool TryGetAllBrokenPaths(IEnumerable<string?> rawValues, Func<string, bool> fileExists, out IReadOnlyList<string> brokenPaths)
    {
        var present = rawValues.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList();
        if (present.Count == 0)
        {
            brokenPaths = [];
            return false;
        }

        var extracted = present.Select(value => ExtractFilePath(value, fileExists)).ToList();
        if (extracted.Any(path => path is null))
        {
            brokenPaths = [];
            return false; // at least one field is real but unparseable, never guess it away
        }

        var confirmed = extracted.Select(path => path!).ToList();
        brokenPaths = confirmed;
        return confirmed.All(path => !fileExists(path));
    }
}
