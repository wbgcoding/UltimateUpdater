using System.IO;
using UltimateUpdater.Services.Platform;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The places a leftover scan looks in, resolved once and handed in so a test can point every one of them at a
/// temp tree. Every path comes from <see cref="Environment.GetFolderPath"/>, never a literal machine path.
/// </summary>
internal sealed record LeftoverRoots(
    string? AppData,
    string? LocalAppData,
    string? ProgramData,
    IReadOnlyList<string> ProgramFiles,
    IReadOnlyList<string> UserShortcutFolders,
    IReadOnlyList<string> MachineShortcutFolders,
    IReadOnlyList<string> ProtectedFolders)
{
    /// <summary>Folders whose direct children a user-lane scan reads: Roaming, Local and Local\Programs.</summary>
    internal IReadOnlyList<string> UserFolderRoots =>
        Existing([AppData, LocalAppData, LocalAppData is null ? null : Path.Combine(LocalAppData, "Programs")]);

    /// <summary>Folders whose direct children a machine-lane scan reads: ProgramData and both Program Files.</summary>
    internal IReadOnlyList<string> MachineFolderRoots => Existing([ProgramData, .. ProgramFiles]);

    /// <summary>The roots an install folder may sit directly or one level below (publisher\program).</summary>
    internal IReadOnlyList<string> InstallRoots =>
        [.. UserFolderRoots, .. MachineFolderRoots];

    internal LeftoverLane LaneOfFolder(string fullPath) =>
        MachineFolderRoots.Any(root => IsBelow(fullPath, root)) ? LeftoverLane.Machine : LeftoverLane.User;

    internal static LeftoverRoots Current()
    {
        static string? Folder(Environment.SpecialFolder folder) =>
            Environment.GetFolderPath(folder) is { Length: > 0 } path ? path : null;

        var appData = Folder(Environment.SpecialFolder.ApplicationData);
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var programData = Folder(Environment.SpecialFolder.CommonApplicationData);

        string[] user = [.. new[]
        {
            appData is null ? null : Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs"),
            Folder(Environment.SpecialFolder.DesktopDirectory),
        }.OfType<string>()];

        string[] machine = [.. new[]
        {
            programData is null ? null : Path.Combine(programData, "Microsoft", "Windows", "Start Menu", "Programs"),
            Folder(Environment.SpecialFolder.CommonDesktopDirectory),
        }.OfType<string>()];

        string[] protectedFolders = [.. new[]
        {
            Folder(Environment.SpecialFolder.MyDocuments), Folder(Environment.SpecialFolder.MyPictures),
            Folder(Environment.SpecialFolder.MyMusic), Folder(Environment.SpecialFolder.MyVideos),
            Folder(Environment.SpecialFolder.Desktop), Folder(Environment.SpecialFolder.DesktopDirectory),
            Folder(Environment.SpecialFolder.UserProfile), Folder(Environment.SpecialFolder.Windows),
            Folder(Environment.SpecialFolder.System), Folder(Environment.SpecialFolder.ProgramFiles),
            Folder(Environment.SpecialFolder.ProgramFilesX86), appData, local, programData,
            Folder(Environment.SpecialFolder.CommonDesktopDirectory),
        }.OfType<string>()];

        return new LeftoverRoots(appData, local, programData, ProgramFilesPaths.Roots(), user, machine, protectedFolders);
    }

    private static IReadOnlyList<string> Existing(IEnumerable<string?> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when <paramref name="path"/> lies strictly below <paramref name="root"/> (segment-wise).</summary>
    internal static bool IsBelow(string path, string root)
    {
        var a = Full(path);
        var b = Full(root);
        return a is not null && b is not null
            && a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string left, string right) =>
        Full(left) is { } a && Full(right) is { } b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The path as a trimmed full path, or null when it is not a usable path.</summary>
    internal static string? Full(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>How many folder levels <paramref name="path"/> lies below <paramref name="root"/>
    /// (1 = a direct child); 0 when it is not below it.</summary>
    internal static int DepthBelow(string path, string root)
    {
        if (!IsBelow(path, root))
            return 0;

        return Full(path)![(Full(root)!.Length + 1)..].Split(Path.DirectorySeparatorChar).Length;
    }
}
