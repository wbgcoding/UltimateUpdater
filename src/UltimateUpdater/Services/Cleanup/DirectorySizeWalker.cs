using System.IO;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The one bounded way cleanup discovery adds up the size of a folder. Iterative on purpose: a recursive
/// walk over a tree that loops back on itself (a junction cycle of empty folders counts no files, so a
/// file cap alone never stops it) would overflow the stack. A directory that is a reparse point is never
/// entered, so the walk cannot be steered outside the tree it was asked about, and a folder the account
/// cannot read, or one that changes while it is listed, just contributes nothing.
/// </summary>
internal static class DirectorySizeWalker
{
    /// <summary>Hard ceiling for callers that want "deep" without a tighter bound of their own.</summary>
    internal const int DefaultMaxDepth = 32;

    /// <summary>
    /// Sums file sizes below <paramref name="root"/>. <paramref name="maxDepth"/> counts sub-folder levels
    /// below the root (0 = the root's own files only); <paramref name="maxFiles"/> ends the walk once that
    /// many files were looked at, so a size shown next to a checkbox stays a hint, not an audited total.
    /// </summary>
    internal static long Measure(
        string root, int maxDepth, int maxFiles, CancellationToken cancellationToken = default)
    {
        // The root is never entered when it is itself a link: the sub-folder rule below only covers what is
        // found INSIDE a folder, and a root swapped for a junction would otherwise measure its target.
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        long total = 0;
        var scanned = 0;
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0 && scanned < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();

            foreach (var file in SafeEnumerate.Files(directory))
            {
                if (scanned++ >= maxFiles)
                    return total;

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort measurement; a locked or vanished file simply contributes nothing.
                }
            }

            if (depth >= maxDepth)
                continue;

            foreach (var sub in SafeEnumerate.Directories(directory))
                pending.Push((sub, depth + 1));
        }

        return total;
    }
}
