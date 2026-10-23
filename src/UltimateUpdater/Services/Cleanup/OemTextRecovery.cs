using System.Runtime.InteropServices;
using System.Text;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// A real DISM run on the lab VM (German display language) did NOT write UTF-16LE once
/// redirected, the shape <see cref="Utf16RedirectRecovery"/> already covers (an earlier finding,
/// only ever checked against a fabricated fixture); it wrote the machine's own single-byte OEM code page
/// (CP850 on that VM), one byte per accented label letter (ä, ö, ü, ß). <see cref="ProcessRunner"/>
/// otherwise decodes every external tool's stdout as UTF-8, and handed those OEM bytes, .NET's UTF-8 decoder
/// replaces every one of them with U+FFFD: the original byte is gone for good at that point, so recovering
/// it AFTER that decode (the way <see cref="Utf16RedirectRecovery"/> does for the UTF-16LE shape) is not
/// possible here. This class instead works on the UNDECODED bytes, which
/// <see cref="CleanupService.AnalyzeComponentStoreAsync"/> preserves losslessly by asking
/// <see cref="ProcessRunner"/> to decode this one call with <see cref="Encoding.Latin1"/> (a 1:1
/// byte-to-char mapping that never substitutes or throws) instead of its usual UTF-8, scoped to this
/// one caller only.
/// </summary>
internal static class OemTextRecovery
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint flags, byte[] bytes, int byteCount, char[]? chars, int charCount);

    /// <summary>
    /// True once a Latin1-preserved capture holds at least one byte outside plain ASCII. Plain English
    /// DISM output (or any run that happens to contain no accented letter) never needs this at all: ASCII
    /// bytes are identical text under Latin1, UTF-8 and every OEM code page alike, so treating them as
    /// "possibly OEM" would risk mangling a perfectly good line for nothing.
    /// </summary>
    public static bool NeedsRecovery(string latin1PreservedText) =>
        latin1PreservedText.Any(c => c > 0x7F);

    /// <summary>
    /// Reinterprets a Latin1-preserved string as the raw bytes it stands in for and decodes them with the
    /// machine's own OEM code page (<c>GetOEMCP</c>): the code page a native console tool falls back to
    /// once it notices its output handle is not a real console. Never throws: an unmappable byte becomes
    /// whatever replacement the OS itself substitutes, the same as a real console would show; the
    /// unrecognisable text then simply fails every label match downstream instead of the app crashing.
    /// </summary>
    public static string Recover(string latin1PreservedText)
    {
        var bytes = Encoding.Latin1.GetBytes(latin1PreservedText);
        var codePage = GetOEMCP();

        var charCount = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
        if (charCount <= 0)
            return latin1PreservedText;

        var chars = new char[charCount];
        var written = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, chars, charCount);
        return written <= 0 ? latin1PreservedText : new string(chars, 0, written);
    }
}
