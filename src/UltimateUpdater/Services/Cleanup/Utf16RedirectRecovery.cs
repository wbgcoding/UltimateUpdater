namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Sfc's progress line (and, on the same code path, DISM's) came back stuck at 0 % once its output
/// was redirected. <see cref="ProcessRunner"/> decodes every child's stdout as explicit UTF-8, but a native
/// console tool that falls back to <c>WriteConsoleW</c>'s raw form once it notices its output handle is not
/// a real console writes UTF-16LE instead. Every ASCII byte of a UTF-16LE stream is still a valid standalone
/// UTF-8 byte on its own (0x00-0x7F never needs a continuation byte), so nothing throws and no
/// replacement character appears; decoding "V" "e" "r" "i" "f" "y" as UTF-8 from UTF-16LE bytes instead
/// produces "V\0e\0r\0i\0f\0y\0", a NUL after every real character, which is exactly why
/// <see cref="ProgressParser"/> never matched a percentage in it. Recognising and undoing that ONE shape,
/// rather than switching how <see cref="ProcessRunner"/> decodes every tool's output, keeps every other
/// caller's behaviour untouched.
/// </summary>
public static class Utf16RedirectRecovery
{
    /// <summary>
    /// True once at least a third of a segment's characters are NUL. An ASCII UTF-16LE stream misdecoded as
    /// UTF-8 interleaves a NUL after every real character (roughly half the string); an ordinary UTF-8
    /// tool's own text essentially never contains one at all, so this cannot mistake real output for the
    /// misdecoded shape.
    /// </summary>
    public static bool LooksLikeMisdecodedUtf16(ReadOnlySpan<char> segment)
    {
        if (segment.Length < 2)
            return false;

        var nulCount = 0;
        foreach (var c in segment)
        {
            if (c == '\0')
                nulCount++;
        }

        return nulCount * 3 >= segment.Length;
    }

    /// <summary>Dropping every NUL is exactly what recovers the real text, once
    /// <see cref="LooksLikeMisdecodedUtf16"/> says a segment needs it.</summary>
    public static string Recover(string segment) => segment.Replace("\0", string.Empty);
}
