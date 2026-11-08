using System.IO;

namespace UltimateUpdater.Tests;

/// <summary>Deletes a test tree that may hold junctions: each link is removed on its own first, so the
/// recursive delete never has to walk into one.</summary>
internal static class TempTreeCleanup
{
    public static void Delete(string root)
    {
        if (!Directory.Exists(root))
            return;

        RemoveLinks(new DirectoryInfo(root));
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    private static void RemoveLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.LinkTarget is not null || (child.Attributes & FileAttributes.ReparsePoint) != 0)
                child.Delete();
            else
                RemoveLinks(child);
        }
    }
}
