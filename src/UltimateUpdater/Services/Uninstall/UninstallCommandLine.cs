using System.IO;
using System.Text;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>An uninstall command split into the program and its arguments, never a shell string.</summary>
/// <param name="Executable">The absolute path of the program.</param>
/// <param name="Arguments">The arguments, split the way CommandLineToArgvW splits them.</param>
public sealed record ParsedUninstallCommand(string Executable, IReadOnlyList<string> Arguments);

/// <summary>Why an uninstall command is not run. None means it may be.</summary>
public enum UninstallRefusal
{
    None,
    NoCommand,
    Unparseable,
    NotAnExecutable,
    ScriptHost,
    UntrustedLocation,
    MissingFile,
    InvalidId,

    /// <summary>The entry is this app's own; its own setup removes it, never this run.</summary>
    OwnEntry,
}

/// <summary>
/// Splits a registry uninstall command into program and arguments. The command comes from a key any installer
/// could have written, so the split is strict and fails closed: anything not unambiguously one program path
/// followed by arguments is refused rather than guessed.
/// </summary>
public static class UninstallCommandLine
{
    /// <summary>Longest command line taken; the registry value is untrusted and may be anything.</summary>
    private const int MaxLength = 4096;

    /// <summary>Programs that run text or other files as code. As the uninstaller program they would turn a
    /// registry value into arbitrary code, so they are never run from there (msiexec is run only from a
    /// validated product code, never from a command line).</summary>
    private static readonly HashSet<string> RefusedStems = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "powershell_ise", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        "wmic", "cmstp", "installutil", "regasm", "regsvcs", "msbuild", "forfiles", "pcalua", "bash", "wsl",
        "conhost", "msiexec", "python", "pythonw", "py", "pyw", "node", "perl", "ruby", "java", "javaw", "wt",
        "start", "explorer", "schtasks", "at", "sc", "reg", "bitsadmin", "certutil", "curl", "ftp",
    };

    /// <summary>
    /// The command as program and arguments, or null with <paramref name="refusal"/> saying why not.
    /// <paramref name="fileExists"/> settles an unquoted path with spaces, the one shape that is ambiguous.
    /// </summary>
    public static ParsedUninstallCommand? Parse(string? raw, Func<string, bool> fileExists, out UninstallRefusal refusal)
    {
        refusal = UninstallRefusal.None;

        if (string.IsNullOrWhiteSpace(raw))
        {
            refusal = UninstallRefusal.NoCommand;
            return null;
        }

        var text = raw.Trim();
        if (text.Length > MaxLength || text.Any(char.IsControl))
        {
            refusal = UninstallRefusal.Unparseable;
            return null;
        }

        string path;
        string rest;
        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            if (closing <= 1)
            {
                refusal = UninstallRefusal.Unparseable;
                return null;
            }

            path = text[1..closing];
            rest = text[(closing + 1)..];
        }
        else
        {
            var candidate = RegistryCleanupSafety.ExtractFilePath(text, fileExists);
            if (candidate is null || !text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                refusal = UninstallRefusal.Unparseable;
                return null;
            }

            path = candidate;
            rest = text[candidate.Length..];
        }

        // Nothing may be glued to the program: `app.exe,0` or `app.exe/S` is not a shape Windows runs the way it reads.
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]))
        {
            refusal = UninstallRefusal.Unparseable;
            return null;
        }

        if (!IsPlainAbsolutePath(path))
        {
            refusal = UninstallRefusal.Unparseable;
            return null;
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            refusal = UninstallRefusal.NotAnExecutable;
            return null;
        }

        if (RefusedStems.Contains(Path.GetFileNameWithoutExtension(path)))
        {
            refusal = UninstallRefusal.ScriptHost;
            return null;
        }

        var arguments = SplitArguments(rest);
        if (arguments is null)
        {
            refusal = UninstallRefusal.Unparseable;
            return null;
        }

        return new ParsedUninstallCommand(path, arguments);
    }

    /// <summary>A drive-letter path with no variable, no parent-directory segment, no device or UNC prefix and
    /// no alternate data stream.</summary>
    internal static bool IsPlainAbsolutePath(string path)
    {
        if (path.Length < 4 || path.Length > 260 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))
            return false;

        if (path.IndexOfAny(['%', '*', '?', '<', '>', '|', '"', ':'], 2) >= 0)
            return false;

        if (path.Any(char.IsControl) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var segments = path[3..].Split('\\', '/');
        return segments.All(segment => segment.Length > 0 && segment != ".." && segment != "." && segment.TrimEnd(' ', '.') == segment);
    }

    /// <summary>
    /// CommandLineToArgvW's rules: whitespace separates, a double quote groups, backslashes double up only
    /// right before a quote. Null for an unterminated quote.
    /// </summary>
    public static IReadOnlyList<string>? SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    slashes++;
                    i++;
                }

                inToken = true;
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1)
                        current.Append('"');
                    else
                        inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                inToken = true;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (inToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (inQuotes)
            return null;

        if (inToken)
            result.Add(current.ToString());

        return result;
    }
}
