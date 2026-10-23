using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static UltimateUpdater.Services.Cleanup.CleanupNative;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Deletes a file or an empty directory through a handle that was opened and checked first, never by a
/// path that is resolved a second time. A path-based delete re-walks every ancestor, so a folder below the
/// locked cleanup root that is swapped for a junction after the listing would send an elevated delete
/// outside the root. Here the object is opened without following a final link, its final path must lie
/// inside the root's own final path, and the delete goes through that same handle.
/// </summary>
internal static class VerifiedDelete
{
    internal enum Outcome
    {
        Deleted,

        /// <summary>Nothing at the path any more: neither deleted by us nor a failure.</summary>
        Vanished,

        /// <summary>Found, but outside the root or a link; left alone.</summary>
        Refused,

        /// <summary>Only from <see cref="CheckFile"/>: the file is inside the root and may be handed on.</summary>
        Cleared,
    }

    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint FileWriteAttributes = 0x100;
    private const uint ShareAll = 0x7; // read | write | delete: the same sharing File.Delete asks for
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint AttributeReadOnly = 0x1;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeNormal = 0x80;
    private const uint AttributeReparsePoint = 0x400;

    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileDispositionInfoClass = 4;
    private const int FileDispositionInfoExClass = 21;

    private const uint DispositionDelete = 0x1;
    private const uint DispositionPosixSemantics = 0x2;
    // Without it a read-only file fails the delete, and the handle has no write access to clear the flag.
    private const uint DispositionIgnoreReadOnly = 0x10;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorNotSupported = 50;
    private const int ErrorInvalidParameter = 87;

    /// <summary>The Ex disposition call is unavailable on this volume or build (FAT and exFAT, old
    /// Windows): only then is the older call tried. Any other failure stays a failure.</summary>
    internal static bool ShouldFallBack(int win32Error) =>
        win32Error is ErrorInvalidParameter or ErrorNotSupported or ErrorInvalidFunction;

    /// <summary>The root's own final path (as a handle reports it) in the form
    /// <see cref="IsInsideRoot"/> compares against, or null when it cannot be read.</summary>
    internal static string? FinalPathOf(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
                return null;

            if (length < buffer.Capacity)
                return buffer.ToString();

            buffer.Capacity = (int)length + 1;
        }
    }

    /// <summary>True when <paramref name="finalPath"/> lies strictly below <paramref name="rootFinalPath"/>.</summary>
    internal static bool IsInsideRoot(string rootFinalPath, string finalPath)
    {
        var root = rootFinalPath.TrimEnd('\\') + "\\";
        return finalPath.Length > root.Length && finalPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Volume serial and file index of the object behind a handle: the identity two different
    /// spellings of one directory (8.3 name, other casing) share.</summary>
    internal static bool TryGetIdentity(SafeFileHandle handle, out (uint VolumeSerial, ulong FileIndex) identity)
    {
        identity = default;
        if (!GetFileInformationByHandle(handle, out var info))
            return false;

        identity = (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
        return true;
    }

    /// <summary>Only the check, for the recycle-bin route that deletes through the shell afterwards:
    /// Cleared, Vanished or Refused.</summary>
    internal static Outcome CheckFile(string rootFinalPath, string file)
    {
        using var handle = OpenChecked(rootFinalPath, file, expectDirectory: false, writeAttributes: false, out var outcome);
        return handle is null ? outcome : Outcome.Cleared;
    }

    /// <summary>Deletes one file. Throws IOException or UnauthorizedAccessException when the file is in use
    /// or not ours to delete, exactly where the path-based delete did.</summary>
    internal static Outcome TryDeleteFile(string rootFinalPath, string file, out long size)
    {
        return DeleteCore(rootFinalPath, file, expectDirectory: false, out size, writeAttributes: false);
    }

    /// <summary>Deletes one directory that must be empty; a directory with content fails like a
    /// non-recursive <see cref="Directory.Delete(string, bool)"/>.</summary>
    internal static Outcome TryDeleteEmptyDirectory(string rootFinalPath, string directory) =>
        DeleteCore(rootFinalPath, directory, expectDirectory: true, out _, writeAttributes: false);

    private static Outcome DeleteCore(
        string rootFinalPath, string path, bool expectDirectory, out long size, bool writeAttributes)
    {
        size = 0;
        using var handle = OpenChecked(rootFinalPath, path, expectDirectory, writeAttributes, out var outcome);
        if (handle is null)
            return outcome;

        if (!expectDirectory && GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo standard, (uint)Marshal.SizeOf<FileStandardInfo>()))
            // Removing one of several names of a file frees nothing: the data stays reachable by the others.
            size = standard.NumberOfLinks > 1 ? 0 : standard.EndOfFile;

        if (!writeAttributes)
        {
            var exFlags = new FileDispositionInfoEx
            {
                Flags = DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadOnly,
            };

            if (SetFileInformationByHandle(handle, FileDispositionInfoExClass, ref exFlags, (uint)Marshal.SizeOf<FileDispositionInfoEx>()))
                return Outcome.Deleted;

            var error = Marshal.GetLastWin32Error();
            if (!ShouldFallBack(error))
                throw ExceptionFor(error, path);

            // Reopen with write access: the old call needs it to clear the read-only attribute first. The
            // reopen runs the same checks again, so nothing verified earlier is trusted.
            handle.Dispose();
            return DeleteCore(rootFinalPath, path, expectDirectory, out size, writeAttributes: true);
        }

        if (GetFileInformationByHandle(handle, out var info) && (info.FileAttributes & AttributeReadOnly) != 0)
        {
            var basic = new FileBasicInfo { FileAttributes = AttributeNormal };
            if (!SetFileInformationByHandle(handle, FileBasicInfoClass, ref basic, (uint)Marshal.SizeOf<FileBasicInfo>()))
                throw ExceptionFor(Marshal.GetLastWin32Error(), path);
        }

        var legacy = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(handle, FileDispositionInfoClass, ref legacy, (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw ExceptionFor(Marshal.GetLastWin32Error(), path);

        return Outcome.Deleted;
    }

    /// <summary>Opens the object without following a final link and checks it. Null means
    /// <paramref name="outcome"/> says why (vanished or refused); a handle means it is cleared to delete.</summary>
    private static SafeFileHandle? OpenChecked(
        string rootFinalPath, string path, bool expectDirectory, bool writeAttributes, out Outcome outcome)
    {
        string extended;
        try
        {
            extended = ToExtendedPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            outcome = Outcome.Refused;
            return null;
        }

        var access = Delete | FileReadAttributes | (writeAttributes ? FileWriteAttributes : 0);
        var handle = CreateFileW(extended, access, ShareAll, IntPtr.Zero, OpenExisting,
            FlagOpenReparsePoint | FlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                outcome = Outcome.Vanished;
                return null;
            }

            throw ExceptionFor(error, path);
        }

        var finalPath = FinalPathOf(handle);
        if (finalPath is null
            || !IsInsideRoot(rootFinalPath, finalPath)
            || !GetFileInformationByHandle(handle, out var info)
            || (info.FileAttributes & AttributeReparsePoint) != 0
            || ((info.FileAttributes & AttributeDirectory) != 0) != expectDirectory)
        {
            handle.Dispose();
            outcome = Outcome.Refused;
            return null;
        }

        outcome = Outcome.Deleted;
        return handle; // outcome is only read when the handle is null
    }

    private static string ToExtendedPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return full;

        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static Exception ExceptionFor(int error, string path)
    {
        var message = $"{new Win32Exception(error).Message} ('{path}')";
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message)
            : new IOException(message, unchecked((int)(0x80070000 | (uint)(error & 0xFFFF))));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
        public uint Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
        public ushort Padding;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int informationClass, out FileStandardInfo info, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile, int informationClass, ref FileDispositionInfoEx info, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile, int informationClass, ref FileDispositionInfo info, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile, int informationClass, ref FileBasicInfo info, uint bufferSize);
}
