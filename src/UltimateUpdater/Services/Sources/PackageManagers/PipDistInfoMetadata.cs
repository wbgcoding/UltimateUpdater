using System.IO;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// A last-resort, network-free publisher lookup for a pip package PyPI has nothing to say about (an
/// unlisted or private-index package, or a listed one whose JSON carries neither an author nor a
/// maintainer): pip's own installer writes a plain-text METADATA file into the package's own *.dist-info
/// folder, in the same RFC-5322-ish header format PyPI's JSON API itself is generated from. Read only as
/// a fallback (see <see cref="PipSource.EnrichAsync"/>), so a package that IS on PyPI keeps reading the
/// SAME fetch its date/size already came from rather than doing this extra disk work for every pip row.
/// </summary>
internal static partial class PipDistInfoMetadata
{
    /// <summary>A METADATA file's headers end at the first blank line, the long description (often the
    /// whole README) follows; this is comfortably more than every real package's header block needs.</summary>
    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Author, else Author-email's name portion, else Maintainer/Maintainer-email, else the
    /// owner of a github.com/gitlab.com Project-URL or Home-page: the exact priority
    /// <see cref="PackageMetadataParsers"/> reads PyPI's own JSON in, so a package looks the same
    /// whichever way its publisher was found.</summary>
    public static string? ReadPublisher(string? sitePackages, string packageName)
    {
        if (string.IsNullOrEmpty(sitePackages))
            return null;

        try
        {
            var directory = FindDistInfoDirectory(sitePackages, packageName);
            if (directory is null)
                return null;

            var metadataPath = Path.Combine(directory, "METADATA");
            if (!File.Exists(metadataPath))
                return null;

            return ParsePublisher(ReadHeaderBlock(metadataPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Pure parse over an already-read header block; kept separate from the disk I/O above so
    /// it is directly unit-testable, the same split <see cref="PackageMetadataParsers"/> uses for the
    /// network responses.</summary>
    internal static string? ParsePublisher(string headerBlock)
    {
        var headers = ParseHeaders(headerBlock);

        return NameOrEmailSender(headers, "Author", "Author-email")
            ?? NameOrEmailSender(headers, "Maintainer", "Maintainer-email")
            ?? ProjectUrlOwner(headers);
    }

    private static string ReadHeaderBlock(string metadataPath)
    {
        using var stream = File.OpenRead(metadataPath);
        using var reader = new StreamReader(stream);
        var buffer = new char[MaxHeaderBytes];
        var read = reader.Read(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    /// <summary>Every "Field: value" line up to the first blank line (where the headers end and the
    /// long description begins); a field that repeats (Project-URL, Classifier) keeps every value in
    /// order. Continuation (indented) lines are not folded back into the previous value: none of the
    /// fields this reads ever wrap onto a second line in practice, and skipping them rather than
    /// mis-parsing them as new fields is the safer failure mode.</summary>
    private static Dictionary<string, List<string>> ParseHeaders(string text)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
                break;

            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
                continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!headers.TryGetValue(name, out var values))
                headers[name] = values = [];
            values.Add(value);
        }

        return headers;
    }

    private static string? NameOrEmailSender(
        IReadOnlyDictionary<string, List<string>> headers, string nameField, string emailField)
    {
        if (headers.TryGetValue(nameField, out var names) && names is [{ Length: > 0 } name, ..])
            return name;

        if (!headers.TryGetValue(emailField, out var emails) || emails.Count == 0)
            return null;

        var combined = emails[0];
        var angle = combined.IndexOf('<');
        if (angle < 0)
            return null; // a bare address with no name ahead of it

        var candidate = combined[..angle].Trim(' ', '"');
        return candidate.Length > 0 ? candidate : null;
    }

    /// <summary>Core metadata's Project-URL is "Label, https://…" (label, comma, URL); Home-page is a
    /// bare URL.</summary>
    private static string? ProjectUrlOwner(IReadOnlyDictionary<string, List<string>> headers)
    {
        if (headers.TryGetValue("Project-URL", out var entries))
        {
            foreach (var entry in entries)
            {
                var comma = entry.IndexOf(',');
                var url = comma >= 0 ? entry[(comma + 1)..].Trim() : entry.Trim();
                if (PackageMetadataParsers.RepoOwner(url) is { } owner)
                    return owner;
            }
        }

        return headers.TryGetValue("Home-page", out var home) && home.Count > 0
            ? PackageMetadataParsers.RepoOwner(home[0])
            : null;
    }

    private static string? FindDistInfoDirectory(string sitePackages, string packageName)
    {
        if (!Directory.Exists(sitePackages))
            return null;

        var target = Normalize(packageName);

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(sitePackages, "*.dist-info"))
            {
                // The folder's own ".dist-info" suffix carries a hyphen too ("dist-info"), so it must
                // come off BEFORE the last remaining hyphen is taken as the name/version split, or that
                // split lands inside "dist-info" instead of between the name and the version.
                var folder = Path.GetFileName(directory);
                var withoutSuffix = folder[..^".dist-info".Length];
                if (SplitsIntoNameAndVersion(withoutSuffix, target))
                    return directory;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>
    /// True when some hyphen splits the folder name into a prefix that normalises to the wanted package and a
    /// version. The split cannot simply be the last hyphen: a version may carry one ("pkg-2.0-1") and an old
    /// installer left the name's own hyphens unescaped ("my-pkg-1.0"). A version starts with a digit, which
    /// keeps "foo" from claiming the folder of "foo-bar".
    /// </summary>
    private static bool SplitsIntoNameAndVersion(string folderWithoutSuffix, string target)
    {
        for (var dash = folderWithoutSuffix.IndexOf('-'); dash > 0; dash = folderWithoutSuffix.IndexOf('-', dash + 1))
        {
            if (dash + 1 < folderWithoutSuffix.Length && char.IsAsciiDigit(folderWithoutSuffix[dash + 1])
                && Normalize(folderWithoutSuffix[..dash]) == target)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>PEP 503 distribution-name normalisation: runs of "-", "_" and "." all compare equal,
    /// case-insensitively, so "typing_extensions" and "typing-extensions" name the same dist-info
    /// folder.</summary>
    private static string Normalize(string name) => NormalizePattern().Replace(name, "_").ToLowerInvariant();

    [GeneratedRegex("[-_.]+")]
    private static partial Regex NormalizePattern();
}
