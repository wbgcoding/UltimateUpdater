namespace UltimateUpdater.Tests;

/// <summary>
/// The XAML sources of the app's windows for tests that read markup as text. A big window is split into
/// controls or resource dictionaries in a sibling folder of Views (Views/Main for MainWindow, Views/Cleanup
/// for CleanupWindow); a test that asserts something about "the window's markup" must still see all of it,
/// so these helpers read the window file together with its split parts, and report a part under the name
/// of the window it was split from.
/// </summary>
internal static class ViewSources
{
    private static readonly Dictionary<string, string> WindowOfPartFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Main"] = "MainWindow.xaml",
        ["Cleanup"] = "CleanupWindow.xaml",
    };

    /// <summary>Every .xaml under <paramref name="viewsDirectory"/>; a split part carries its window's file name.</summary>
    public static IEnumerable<(string Name, string Path)> AllXaml(string viewsDirectory)
    {
        foreach (var file in Directory.EnumerateFiles(viewsDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file)!);
            var name = !string.Equals(System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(file)!),
                           System.IO.Path.GetFullPath(viewsDirectory), StringComparison.OrdinalIgnoreCase)
                       && WindowOfPartFolder.TryGetValue(folder, out var window)
                ? window
                : System.IO.Path.GetFileName(file);
            yield return (name, file);
        }
    }

    /// <summary>The window's own file followed by its split parts, one path each.</summary>
    public static IEnumerable<string> WindowFiles(string viewsDirectory, string windowFile)
    {
        yield return System.IO.Path.Combine(viewsDirectory, windowFile);
        foreach (var (name, path) in AllXaml(viewsDirectory))
        {
            if (string.Equals(name, windowFile, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(System.IO.Path.GetFileName(path), windowFile, StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    /// <summary>The window's markup as one text: its own file plus every split part.</summary>
    public static string ReadWindow(string viewsDirectory, string windowFile) =>
        string.Join(Environment.NewLine, WindowFiles(viewsDirectory, windowFile).Select(File.ReadAllText));
}
