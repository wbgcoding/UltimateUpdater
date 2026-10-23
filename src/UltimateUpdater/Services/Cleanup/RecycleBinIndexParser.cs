using System.IO;
using System.Text;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>What one <c>$I</c> index file says about the deleted item its <c>$R</c> twin holds.</summary>
public sealed record RecycleBinIndexEntry(long SizeBytes, DateTimeOffset DeletedAt, string OriginalPath);

/// <summary>
/// Reads Windows' own <c>$I…</c> index files: the only place the original path and the deletion date of a
/// recycled item still exist. Pure and total: every malformed, truncated or unknown input is answered with
/// <c>false</c>, never an exception, because this runs once per file over a folder whose content is entirely
/// outside our control and a single odd file must never end a scan.
/// </summary>
public static class RecycleBinIndexParser
{
    // Version, size and deletion time are a fixed 24-byte prologue in both formats; version 1 follows it
    // with a fixed 260-character path buffer, version 2 with a character count and a path of that length.
    private const int HeaderBytes = 24;
    private const int V1PathBytes = 520;
    private const int MaxPathChars = 32767;

    /// <summary>The highest FILETIME <see cref="DateTimeOffset.FromFileTime"/> accepts: a corrupted
    /// timestamp must be refused rather than thrown over.</summary>
    private const long MaxFileTime = 2650467743999999999;

    /// <summary>One petabyte. The scan totals every entry's size, so a forged size near the limit of a long
    /// would overflow that sum and end the scan; no file a recycle bin holds comes anywhere near this.</summary>
    private const long MaxSizeBytes = (1L << 50) - 1;

    public static bool TryParse(ReadOnlySpan<byte> data, out RecycleBinIndexEntry? entry)
    {
        entry = null;

        if (data.Length < HeaderBytes)
            return false;

        var version = BitConverter.ToInt64(data[..8]);
        var size = BitConverter.ToInt64(data.Slice(8, 8));
        var fileTime = BitConverter.ToInt64(data.Slice(16, 8));

        if (size < 0 || size > MaxSizeBytes || fileTime < 0 || fileTime > MaxFileTime)
            return false;

        ReadOnlySpan<byte> pathBytes;
        switch (version)
        {
            case 1:
                if (data.Length < HeaderBytes + V1PathBytes)
                    return false;
                pathBytes = data.Slice(HeaderBytes, V1PathBytes);
                break;

            case 2:
                if (data.Length < HeaderBytes + 4)
                    return false;
                var charCount = BitConverter.ToInt32(data.Slice(HeaderBytes, 4));
                if (charCount <= 0 || charCount > MaxPathChars)
                    return false;
                if (data.Length < HeaderBytes + 4 + (charCount * 2))
                    return false;
                pathBytes = data.Slice(HeaderBytes + 4, charCount * 2);
                break;

            default:
                return false;
        }

        var path = Encoding.Unicode.GetString(pathBytes);
        var terminator = path.IndexOf('\0');
        if (terminator >= 0)
            path = path[..terminator];

        // Windows always records a fully-qualified original path. Insisting on that is what tells a real
        // index file from arbitrary bytes that merely happen to start with a plausible header.
        if (path.Length == 0 || !IsPlausiblePath(path))
            return false;

        // Read as UTC on purpose: the local-time overload can push a 1601 timestamp out of range, and the
        // display layer converts to local time itself anyway.
        entry = new RecycleBinIndexEntry(size, new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime)), path);
        return true;
    }

    private static bool IsPlausiblePath(string path)
    {
        if (path.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;

        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
