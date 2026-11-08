using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace UltimateUpdater.Services;

/// <summary>
/// The shortcuts in the Start menu, read once. A shortcut names the program and the icon the user
/// actually sees for it, which is the one thing a registry entry without DisplayIcon cannot tell us.
/// </summary>
internal sealed class StartMenuShortcuts
{
    /// <summary>Shortcuts that sit beside an app but open something else.</summary>
    private static readonly string[] NotTheApplication =
        ["uninstall", "remove", "readme", "releasenotes", "documentation", "website", "manual", "license", "help", "changelog"];

    internal sealed record Shortcut(string Name, string? TargetPath, string? IconPath, int IconIndex);

    private readonly IReadOnlyList<string> _folders;
    private readonly Lazy<IReadOnlyList<Shortcut>> _shortcuts;

    public StartMenuShortcuts(IReadOnlyList<string> folders)
    {
        _folders = folders;
        _shortcuts = new Lazy<IReadOnlyList<Shortcut>>(Load);
    }

    public static IReadOnlyList<string> DefaultFolders() =>
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs"),
    ];

    /// <summary>Exact name first, then the same prefix and substring rules the registry match uses.</summary>
    public Shortcut? Find(string displayName)
    {
        var wanted = IconCacheService.CleanName(displayName);
        if (wanted.Length == 0)
            return null;

        var shortcuts = _shortcuts.Value;

        var exact = shortcuts.FirstOrDefault(s => string.Equals(s.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        return Best(shortcuts, wanted, prefixOnly: true) ?? Best(shortcuts, wanted, prefixOnly: false);
    }

    private static Shortcut? Best(IReadOnlyList<Shortcut> shortcuts, string wanted, bool prefixOnly) =>
        shortcuts.Where(s => IconCacheService.IsPartialMatch(s.Name, wanted, prefixOnly))
            .OrderBy(s => s.Name.Length)
            .FirstOrDefault();

    private IReadOnlyList<Shortcut> Load()
    {
        var found = new List<Shortcut>();

        // IShellLinkW wants an apartment thread; the pool's threads are not one.
        var thread = new Thread(() => ReadAll(found)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return found;
    }

    private void ReadAll(List<Shortcut> found)
    {
        foreach (var folder in _folders)
        {
            IEnumerable<string> files;
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                // One folder this user may not list, or a link into somewhere else, must not cost the shortcuts
                // of every other folder under the same root.
                files = Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var squashed = TextNormalization.AlphanumericLower(name);
                if (NotTheApplication.Any(squashed.Contains))
                    continue;

                if (Read(file) is { } link)
                    found.Add(new Shortcut(name, link.Target, link.IconPath, link.IconIndex));
            }
        }
    }

    /// <summary>Target and icon of one shortcut, or null when it cannot be read (a shortcut to a Store app has no path at all).</summary>
    internal static (string? Target, string? IconPath, int IconIndex)? Read(string linkPath)
    {
        object? created = null;
        try
        {
            created = new ShellLink();
            var link = (IShellLinkW)created;
            ((IPersistFile)created).Load(linkPath, 0);

            var target = new StringBuilder(1024);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);

            var icon = new StringBuilder(1024);
            link.GetIconLocation(icon, icon.Capacity, out var index);

            var targetPath = Expand(target.ToString());
            var iconPath = Expand(icon.ToString());
            if (targetPath is null && iconPath is null)
                return null;

            return (targetPath, iconPath, index);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (created is not null)
                Marshal.ReleaseComObject(created);
        }
    }

    private static string? Expand(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Environment.ExpandEnvironmentVariables(value.Trim());
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
