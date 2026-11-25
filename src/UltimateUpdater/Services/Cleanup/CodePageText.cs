using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The one single-byte code page decode the cleanup readers share (OEM console output, ANSI .inf files):
/// the two-pass <c>MultiByteToWideChar</c> call, falling back to a caller-chosen text when the system cannot
/// map the bytes. Never throws.
/// </summary>
internal static class CodePageText
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int MultiByteToWideChar(
        uint codePage, uint flags, byte[] bytes, int byteCount, char[]? chars, int charCount);

    /// <summary>The <paramref name="bytes"/> as text in <paramref name="codePage"/>, or the result of
    /// <paramref name="fallback"/> when the system cannot convert them.</summary>
    internal static string Decode(byte[] bytes, uint codePage, Func<string> fallback)
    {
        var charCount = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
        if (charCount <= 0)
            return fallback();

        var chars = new char[charCount];
        var written = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, chars, charCount);
        return written <= 0 ? fallback() : new string(chars, 0, written);
    }
}
