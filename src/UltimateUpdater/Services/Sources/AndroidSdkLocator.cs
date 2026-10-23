using System.IO;
using System.Xml.Linq;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Finds the Android SDK, and the `sdkmanager` that can drive it.
/// The two are NOT the same question, which is what this class exists for: a machine can have
/// Android Studio and a separate set of command-line tools under a custom path, which is also what ANDROID_HOME
/// points at, while the SDK the IDE actually uses lives on a different drive entirely, at
/// <c>D:\AndroidStudio\SDK</c>. Following the environment variable alone therefore found a real sdkmanager
/// and pointed it at the wrong, nearly empty SDK, so the row reported that everything was up to date.
/// The order below is by AUTHORITY, not convenience: Android Studio writes the SDK it is using into its own
/// configuration, and that is the one the developer means. Only when the IDE has nothing to say do the
/// environment variables and the default location get a turn.
/// </summary>
public static class AndroidSdkLocator
{
    /// <summary>
    /// Directories that mark a folder as an SDK root rather than as some folder that happens to be called
    /// "Android". Any ONE of them is enough: a fresh install may hold nothing but the command-line tools.
    /// </summary>
    private static readonly string[] SdkMarkers =
        ["platform-tools", "platforms", "build-tools", "cmdline-tools", "licenses", "tools"];

    /// <summary>The SDK roots worth trying, best first, de-duplicated and known to exist.</summary>
    public static IReadOnlyList<string> Roots()
    {
        var found = new List<string>();

        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Replace('/', '\\')));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!IsSdkRoot(full) || found.Contains(full, StringComparer.OrdinalIgnoreCase))
                continue;

            found.Add(full);
        }

        return found;
    }

    /// <summary>
    /// A directory that actually looks like an SDK. Checked before a path is used, because both the
    /// environment variables and the IDE's configuration can name a folder that has since been moved.
    /// </summary>
    public static bool IsSdkRoot(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        return SdkMarkers.Any(marker => Directory.Exists(Path.Combine(directory, marker)));
    }

    private static IEnumerable<string?> Candidates()
    {
        // 1. What Android Studio itself is using. This is the authoritative answer.
        foreach (var path in StudioSdkPaths())
            yield return path;

        // 2. The documented environment variables.
        yield return Environment.GetEnvironmentVariable("ANDROID_HOME");
        yield return Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");

        // 3. The default location Studio uses when nobody chose one.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");

        // 4. The extra rule for a Studio-style split installation: the IDE's own installer
        // offers to put the SDK in an "SDK" folder beside or below the chosen directory, which is exactly
        // the D:\AndroidStudio\SDK layout. So every directory named above is also probed one level down.
        // 5. The SAME directory can also BE the SDK root directly, as when the install moved to
        // D:\AndroidStudio with no separate "SDK" subfolder underneath, IsSdkRoot's marker check keeps
        // this safe: a plain Android Studio program folder without SDK markers is simply rejected.
        foreach (var parent in SplitInstallParents())
        {
            yield return parent;
            yield return Path.Combine(parent, "SDK");
            yield return Path.Combine(parent, "Sdk");
            yield return Path.Combine(parent, "sdk");
        }
    }

    private static IEnumerable<string> SplitInstallParents()
    {
        var parents = new List<string?>
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android"),
        };

        // Where the IDE itself was installed, its SDK often sits next to it on the same drive.
        foreach (var studio in StudioInstallDirectories())
        {
            parents.Add(studio);
            parents.Add(Path.GetDirectoryName(studio));
        }

        return parents.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!);
    }

    /// <summary>
    /// The SDK paths Android Studio has recorded, newest configuration first. Studio keeps one settings
    /// directory per release (AndroidStudio2024.1, AndroidStudio2026.1.1, …) and each holds a jdk.table.xml
    /// naming the SDK behind every Android platform entry.
    /// </summary>
    private static IEnumerable<string> StudioSdkPaths()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var google = Path.Combine(roaming, "Google");
        if (!Directory.Exists(google))
            yield break;

        List<string> configurations;
        try
        {
            configurations = OrderStudioConfigurations(Directory.GetDirectories(google, "AndroidStudio*")).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var configuration in configurations)
        {
            var table = Path.Combine(configuration, "options", "jdk.table.xml");

            string xml;
            try
            {
                if (!File.Exists(table))
                    continue;

                xml = File.ReadAllText(table);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in ParseStudioSdkPaths(xml))
                yield return path;
        }
    }

    /// <summary>
    /// Studio settings folders newest release first. The folder name is compared as a version, not as text:
    /// "AndroidStudio4.2" must not outrank "AndroidStudio2024.1". At the same version a stable folder
    /// comes before the "AndroidStudioPreview" one; a name without a readable version comes last.
    /// </summary>
    internal static IReadOnlyList<string> OrderStudioConfigurations(IEnumerable<string> directories) =>
        directories
            .Select(directory => (Directory: directory, Key: StudioConfigurationKey(Path.GetFileName(directory))))
            .OrderByDescending(entry => entry.Key.Version is not null)
            .ThenByDescending(entry => entry.Key.Version)
            .ThenBy(entry => entry.Key.IsPreview)
            .ThenByDescending(entry => entry.Directory, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Directory)
            .ToList();

    private static (Version? Version, bool IsPreview) StudioConfigurationKey(string name)
    {
        const string prefix = "AndroidStudio";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return (null, false);

        var rest = name[prefix.Length..];
        var preview = rest.StartsWith("Preview", StringComparison.OrdinalIgnoreCase);
        if (preview)
            rest = rest["Preview".Length..];

        var numeric = new string(rest.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        if (Version.TryParse(numeric, out var version))
            return (version, preview);

        return (int.TryParse(numeric, out var major) ? new Version(major, 0) : null, preview);
    }

    /// <summary>
    /// The home paths of every "Android SDK" entry in a jdk.table.xml, in document order and without
    /// repeats. Pure so it can be tested against a real file's shape; malformed XML yields nothing rather
    /// than throwing, like every other parser in this project.
    /// </summary>
    public static IReadOnlyList<string> ParseStudioSdkPaths(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return [];

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        var paths = new List<string>();

        foreach (var jdk in document.Descendants("jdk"))
        {
            var type = jdk.Elements("type").Attributes("value").FirstOrDefault()?.Value;
            if (!string.Equals(type, "Android SDK", StringComparison.OrdinalIgnoreCase))
                continue;

            var home = jdk.Elements("homePath").Attributes("value").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(home))
                continue;

            // Studio writes forward slashes even on Windows.
            var normalised = home.Replace('/', '\\').TrimEnd('\\');
            if (!paths.Contains(normalised, StringComparer.OrdinalIgnoreCase))
                paths.Add(normalised);
        }

        return paths;
    }

    /// <summary>Where Android Studio is installed, from its own registry key and the usual two paths.</summary>
    public static IEnumerable<string> StudioInstallDirectories()
    {
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            if (RegistryPath(view) is { Length: > 0 } path)
                yield return path;
        }

        yield return Path.Combine(ProgramFilesPaths.ProgramFiles, "Android", "Android Studio");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Android Studio");
    }

    private static string? RegistryPath(Microsoft.Win32.RegistryView view)
    {
        try
        {
            using var machine = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Android Studio");
            return key?.GetValue("Path") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The `sdkmanager` to run, and the SDK it should be pointed at. The two are looked up separately on
    /// purpose: the tool may sit in one SDK tree while the SDK worth updating is another one, and passing
    /// --sdk_root explicitly is what keeps the command working on the right tree.
    /// </summary>
    public static (string? Executable, string? SdkRoot) FindSdkManager()
    {
        var roots = Roots();
        if (roots.Count == 0)
            return (null, null);

        var sdkRoot = roots[0];

        // The tool is looked for in the chosen root first, then anywhere else an SDK was found.
        foreach (var root in roots)
        {
            if (SdkManagerIn(root) is { } executable)
                return (executable, sdkRoot);
        }

        return (null, sdkRoot);
    }

    /// <summary>
    /// The `sdkmanager` to run, inside one SDK root.
    /// A VERSIONED cmdline-tools folder is preferred over "latest", and that ordering is the whole point:
    /// `sdkmanager --update` updates the `cmdline-tools;latest` package too, and it cannot overwrite the copy
    /// it is itself running from, it unzips the new release and then fails at the very end. That is exactly
    /// what a real run showed: 100% unzipped, then exit code 1. Started from a pinned version
    /// folder, the files being replaced are not the ones in use and the update completes.
    /// "latest" is still used when it is the only thing there; then a cmdline-tools self-update is expected
    /// to fail and everything else in the same run still succeeds.
    /// </summary>
    internal static string? SdkManagerIn(string root)
    {
        var cmdlineTools = Path.Combine(root, "cmdline-tools");

        // Newest version FIRST, compared as numbers: sorted as text, "9.0" beats "21.0".
        var candidates = Directories(cmdlineTools)
            .Where(directory => !string.Equals(
                Path.GetFileName(directory), "latest", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(VersionOfFolder)
            .ThenByDescending(directory => directory, StringComparer.OrdinalIgnoreCase)
            .Select(directory => Path.Combine(directory, "bin", "sdkmanager.bat"))
            .ToList();

        // A developer who installed only "latest" has nothing else to run.
        candidates.Add(Path.Combine(cmdlineTools, "latest", "bin", "sdkmanager.bat"));

        // the pre-cmdline-tools layout, still present on older SDK installations
        candidates.Add(Path.Combine(root, "tools", "bin", "sdkmanager.bat"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The `android.exe` CLI that sits in the same `bin` folder as the chosen `sdkmanager.bat`, or null.
    /// The newest command-line tools ship it and `sdkmanager.bat` only forwards to it, so talking to it
    /// directly skips the Java dependency and gives a per-package update. Only the folder of the CHOSEN
    /// sdkmanager counts: the CLI of an older tools release is not a candidate.
    /// </summary>
    internal static string? AndroidCliNextTo(string? sdkManagerPath)
    {
        if (string.IsNullOrEmpty(sdkManagerPath) || Path.GetDirectoryName(sdkManagerPath) is not { } bin)
            return null;

        var cli = Path.Combine(bin, "android.exe");
        return File.Exists(cli) ? cli : null;
    }

    /// <summary>
    /// A cmdline-tools folder name as a comparable version. Names like "16.0-alpha01" carry a suffix, so only
    /// the leading numeric part is read; anything unparseable sorts last rather than throwing.
    /// </summary>
    private static Version VersionOfFolder(string directory)
    {
        var name = Path.GetFileName(directory);
        var numeric = new string(name.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');

        return Version.TryParse(numeric, out var version)
            ? version
            : int.TryParse(numeric, out var major) ? new Version(major, 0) : new Version(0, 0);
    }

    private static IReadOnlyList<string> Directories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
