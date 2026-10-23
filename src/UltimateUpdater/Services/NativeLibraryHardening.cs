using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services;

/// <summary>
/// Narrows where this process loads native libraries from. Runs from a module initializer, so it
/// reaches the normal app and the elevated worker alike before WPF or any static member loads a DLL.
/// </summary>
internal static class NativeLibraryHardening
{
    // APPLICATION_DIR | SYSTEM32 | USER_DIRS. The exe's own folder is exactly as trusted as the exe itself,
    // and an installed copy keeps WPF's natives there. What this drops is the current directory and %PATH%,
    // the places a phantom DLL is actually planted.
    private const uint SearchDefaultDirs = 0x1000;

    /// <summary>Win32 error of the call, 0 when it worked. Logged once the logging service exists.</summary>
    internal static int DefaultDirectoriesError { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryHardening).Assembly, ResolveFromSystemDirectory);
        }
        catch (InvalidOperationException)
        {
            // A resolver is already registered for this assembly; the flags below still apply.
        }

        DefaultDirectoriesError = SetDefaultDllDirectories(SearchDefaultDirs) ? 0 : Marshal.GetLastWin32Error();
    }

    /// <summary>
    /// The System32 path for one of this app's own imports, or null for a name that is not a bare DLL name.
    /// The runtime would otherwise probe the app folder before the search-path flags apply, so a copy of
    /// wintrust.dll planted beside a portable exe would win over the real one.
    /// </summary>
    internal static string? SystemPathFor(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName)
            || libraryName.IndexOfAny(['\\', '/', ':']) >= 0)
            return null;

        var fileName = libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        return Path.Combine(Environment.SystemDirectory, fileName);
    }

    /// <summary>
    /// A bare name that maps to System32 either loads from there or fails. Returning "not resolved" instead
    /// would hand the name back to the runtime's own probing, which looks in the app folder first: exactly
    /// the planted copy this resolver exists to keep out. A name with a path part is not ours to remap.
    /// </summary>
    internal static IntPtr ResolveFromSystemDirectory(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var path = SystemPathFor(libraryName);
        if (path is null)
            return IntPtr.Zero;

        return NativeLibrary.TryLoad(path, out var handle)
            ? handle
            : throw new DllNotFoundException($"'{libraryName}' was not found in the system directory.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
