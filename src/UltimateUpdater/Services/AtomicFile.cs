using System.IO;
using System.Text;

namespace UltimateUpdater.Services;

/// <summary>
/// The one way this app replaces a state file: written to a temp file of its own beside the target, flushed
/// to disk, then moved over the target in a single step, so a reader (or a crash) sees the old file or the
/// complete new one, never half of either. The temp name is unique per call, so two writers never share one.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Throws IOException/UnauthorizedAccessException when the file cannot be replaced; the temp file
    /// is removed and the previous target is left untouched.</summary>
    internal static void Write(string path, Action<Stream> write)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            // File.Replace would throw when the target does not exist yet; Move with overwrite covers both.
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            FileDeletion.TryDelete(temporaryPath);
            throw;
        }
    }

    internal static void WriteAllText(string path, string text) =>
        Write(path, stream =>
        {
            var bytes = Utf8NoBom.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        });

    /// <summary>
    /// The file's text, or null when it is missing or larger than <paramref name="maxBytes"/>: a cache that
    /// grew past its cap is read as "no cache" instead of being loaded whole. The cap is enforced on the bytes
    /// actually read, not on a length that can change between the check and the read. Other I/O errors throw.
    /// </summary>
    internal static string? TryReadAllText(string path, long maxBytes)
    {
        try
        {
            // FileShare.Delete too: a concurrent save replaces the file by renaming over it, which a reader
            // holding it without delete sharing made fail with a sharing violation.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
