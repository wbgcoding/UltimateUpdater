using System.IO;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Everything on this machine that says "this program is still here": the names, publishers and folders of
/// installed programs and Store apps, running processes and services, scheduled tasks, start-up entries,
/// shortcuts and the programs' own executable names. A folder is only ever called a leftover when NONE of it
/// explains the folder, so every source is read generously and every match is lenient: more evidence can only
/// mean fewer candidates, never more. The evidence is plain data so a test can build any machine by hand.
/// </summary>
internal sealed class LeftoverEvidence
{
    /// <summary>Shortest name for which "one contains the other" counts as a match; shorter names only match
    /// exactly, because "ms" or "app" sits inside half of everything.</summary>
    internal const int ContainsMinimum = 4;

    private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _words = new(StringComparer.Ordinal);
    private readonly List<string> _paths = [];

    internal int TokenCount => _tokens.Count;

    internal int PathCount => _paths.Count;

    /// <summary>A product, publisher, service, task or file name. The whole name counts, and so does every
    /// word of it ("TeamSpeak Systems GmbH" claims "teamspeak").</summary>
    internal void AddName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var whole = LeftoverNames.Normalize(text);
        if (whole.Length > 0)
            _tokens.Add(whole);

        foreach (var word in text.Split([' ', '\t', ',', '.', '-', '_', '(', ')', '/', '\\', ':', '{', '}', '[', ']'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = LeftoverNames.Normalize(word);
            if (normalized.Length >= ContainsMinimum)
                _words.Add(normalized);
        }
    }

    /// <summary>A folder or file path, optionally followed by arguments. A file stands for its folder and its
    /// own name; a folder claims itself, its name, and, through the folder test of <see cref="Explain"/>,
    /// every folder above and below it. A service in "C:\Program Files\Contoso\Notes\notes.exe" so claims
    /// "notes", the program folder and the "Contoso" folder that holds it.</summary>
    internal void AddPath(string? raw)
    {
        var text = raw?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(text))
            return;

        // "C:\x\app.exe --flag" or "\"C:\x\app.exe\" /s": the executable is the part up to its extension.
        var end = FindExecutableEnd(text);
        if (end > 0)
            text = text[..end];

        // A relative path ("system32\drivers\x.sys", "\SystemRoot\...") would resolve against the current
        // folder: it only gives its file name, never a folder.
        if (text.Length < 3 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] != '\\')
        {
            AddName(Path.GetFileNameWithoutExtension(text.Replace('/', '\\')));
            return;
        }

        var full = LeftoverRoots.Full(text);
        if (full is null)
            return;

        var leaf = Path.GetFileName(full);
        if (leaf.Contains('.', StringComparison.Ordinal))
        {
            AddName(Path.GetFileNameWithoutExtension(leaf));
            if (Path.GetDirectoryName(full) is { Length: > 0 } folder)
                AddFolder(folder);
        }
        else
        {
            AddFolder(full);
        }
    }

    private void AddFolder(string folder)
    {
        var full = LeftoverRoots.Full(folder);
        if (full is null || string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            return;

        _paths.Add(full);
        AddName(Path.GetFileName(full));
    }

    private static int FindExecutableEnd(string text)
    {
        foreach (var extension in new[] { ".exe", ".dll", ".lnk", ".bat", ".cmd", ".msi", ".sys" })
        {
            var at = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
                return at + extension.Length;
        }

        return -1;
    }

    /// <summary>Takes over what the registry read of the Uninstall keys and the inventory already gathered.</summary>
    internal void AddClaims(LeftoverClaims claims)
    {
        foreach (var name in claims.Names)
            _tokens.Add(name);

        foreach (var publisher in claims.Publishers)
            _tokens.Add(publisher);

        foreach (var path in claims.Paths)
        {
            AddFolder(path);
            AddName(Path.GetFileName(path));
        }
    }

    internal IReadOnlyList<string> Paths => _paths;

    /// <summary>
    /// Which piece of evidence explains this folder, or null when nothing does. The name test is lenient on
    /// purpose (equal, or one contains the other from <see cref="ContainsMinimum"/> characters, or equal to a
    /// word of a product name); the folder test is: the folder is, holds or lies inside a folder some program
    /// or service owns.
    /// </summary>
    internal string? Explain(string folderFull, LeftoverRoots roots)
    {
        var name = LeftoverNames.Normalize(Path.GetFileName(folderFull));
        if (name.Length == 0)
            return "unnamed";

        if (_tokens.Contains(name) || _words.Contains(name))
            return "name " + name;

        if (name.Length >= ContainsMinimum)
        {
            foreach (var token in _tokens)
            {
                if (token.Length >= ContainsMinimum && (token.Contains(name, StringComparison.Ordinal) || name.Contains(token, StringComparison.Ordinal)))
                    return "similar name " + token;
            }

            foreach (var word in _words)
            {
                // A word of a product name only matches a folder that starts or ends with it, never a bare
                // fragment in the middle, but long words are specific enough to match anywhere.
                if (name.Contains(word, StringComparison.Ordinal) && (word.Length >= 6 || name.StartsWith(word, StringComparison.Ordinal)))
                    return "word " + word;
            }
        }

        foreach (var path in _paths)
        {
            if (roots.InstallRoots.Concat(roots.ProtectedFolders).Any(root => LeftoverRoots.SamePath(path, root)))
                continue;

            if (string.Equals(path, folderFull, StringComparison.OrdinalIgnoreCase)
                || LeftoverRoots.IsBelow(path, folderFull)
                || LeftoverRoots.IsBelow(folderFull, path))
            {
                return "folder " + path;
            }
        }

        return null;
    }
}
