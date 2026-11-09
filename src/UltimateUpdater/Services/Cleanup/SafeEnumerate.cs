using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The one way cleanup discovery lists a folder. The listing is an iterator with its own try/catch around
/// opening it and around every <c>MoveNext</c>: a plain <c>return Directory.EnumerateX(...)</c> inside a
/// try only guards the call, and an access or I/O error that surfaces while the lazy sequence is being
/// walked (a folder removed or locked mid-scan) would escape into the caller and abort the whole scan. What
/// was listed before the error is kept; a folder that cannot be listed yields nothing.
/// </summary>
internal static class SafeEnumerate
{
    /// <summary>The files directly inside <paramref name="directory"/>. A file cannot redirect a walk, so
    /// no reparse-point filter applies (cloud placeholders are reparse-point files and are ordinary here).</summary>
    internal static IEnumerable<string> Files(string directory) =>
        Listing(() => Directory.EnumerateFiles(directory), onError: null);

    /// <summary>Same listing, but <paramref name="onError"/> runs when the folder cannot be opened or the
    /// walk breaks part-way, for a caller that must not trust a partial result.</summary>
    internal static IEnumerable<string> Files(string directory, Action onError) =>
        Listing(() => Directory.EnumerateFiles(directory), onError);

    /// <summary>The sub-folders directly inside <paramref name="directory"/>, never a junction or symlink
    /// (any reparse point): descending into one could leave the tree the caller was asked about.</summary>
    internal static IEnumerable<string> Directories(string directory) =>
        Listing(() => Directory.EnumerateDirectories(directory, "*", NoReparsePoints), onError: null);

    /// <summary>The sub-folder listing with the same failure callback as <see cref="Files(string, Action)"/>.</summary>
    internal static IEnumerable<string> Directories(string directory, Action onError) =>
        Listing(() => Directory.EnumerateDirectories(directory, "*", NoReparsePoints), onError);

    /// <summary>The listing itself leaves out every reparse point, so no per-entry attribute call is needed.
    /// Everything else matches the plain listing: hidden and system folders are included, and an inaccessible
    /// folder raises instead of being skipped, which the callers' failure callback depends on.</summary>
    private static readonly EnumerationOptions NoReparsePoints = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
    };

    private static IEnumerable<string> Listing(Func<IEnumerable<string>> open, Action? onError)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = open().GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unlistable folder: nothing to yield.
            onError?.Invoke();
        }

        if (enumerator is null)
            yield break;

        using (enumerator)
        {
            while (true)
            {
                var hasNext = false;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The listing broke part-way (folder deleted, access changed): keep what was read.
                    onError?.Invoke();
                }

                if (!hasNext)
                    yield break;

                yield return enumerator.Current;
            }
        }
    }
}
