using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Cleanup;
using static UltimateUpdater.Services.Cleanup.CleanupNative;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// Moves one file or folder into or out of a quarantine by handle, never by path. A path move after a path check
/// leaves a window in which the checked parent can be swapped for a junction, and the elevated worker would then
/// move something it never inspected. Here the source is opened without following a link and proven to be at the
/// path it was asked for, the destination folder is locked so it cannot be swapped, and the rename is issued on the
/// source handle while the locked folder is held: what was proven is what moves, to where it was proven.
/// </summary>
internal static class QuarantineMove
{
    private const uint Delete = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint AttributeDirectory = 0x10;
    private const uint AttributeReparsePoint = 0x400;
    private const int FileRenameInfo = 3;
    private const int FileRenameInfoEx = 22;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorInvalidLevel = 124;
    private const int ErrorNotSupported = 50;

    /// <summary>Test seam, invoked with the full source path after the caller's own checks and before anything is
    /// opened (the window in which an attacker would swap a folder on the way for a link).</summary>
    internal static readonly AsyncLocal<Action<string>?> BeforeQuarantineMoveForTest = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile, int fileInformationClass, IntPtr information, uint bufferSize);

    /// <summary>
    /// Renames <paramref name="source"/> to <paramref name="destination"/> (a name that is free, in a folder that
    /// exists) on the same volume. False, with the reason in <paramref name="failure"/> and nothing moved, when the
    /// source is a link, is not of the expected kind, is not at that path any more, when the destination folder
    /// leads through a link or the rename is refused (another drive, a locked file, a name taken).
    /// </summary>
    internal static bool TryRename(string source, string destination, bool isDirectory, out string failure)
    {
        failure = string.Empty;
        try
        {
            var fullSource = Path.GetFullPath(source);
            var fullDestination = Path.GetFullPath(destination);
            var destinationParent = Path.GetDirectoryName(fullDestination);
            var leaf = Path.GetFileName(fullDestination);
            if (destinationParent is null || leaf.Length == 0)
            {
                failure = "the destination has no folder";
                return false;
            }

            BeforeQuarantineMoveForTest.Value?.Invoke(fullSource);

            // Entries may still be added to the destination folder (it is where the item goes); the folder itself
            // cannot be renamed, deleted or re-pointed while this handle is open.
            using var parentLock = CleanupSafety.TryLockForUse(destinationParent, allowEntriesToBeAdded: true);
            if (parentLock is null)
            {
                failure = $"'{destinationParent}' leads through a link or cannot be locked";
                return false;
            }

            using var sourceHandle = CreateFileW(
                fullSource, Delete | ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (sourceHandle.IsInvalid)
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (!GetFileInformationByHandle(sourceHandle, out var info)
                || (info.FileAttributes & AttributeReparsePoint) != 0
                || ((info.FileAttributes & AttributeDirectory) != 0) != isDirectory)
            {
                failure = "the item is a link or not of the expected kind";
                return false;
            }

            // A handle that came through a link on the way reports the link's target as its path.
            if (!CleanupSafety.HandleIsAtPath(sourceHandle, fullSource))
            {
                failure = "the item is not at the path it was found at any more";
                return false;
            }

            return Rename(sourceHandle, fullDestination, out failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Win32Exception)
        {
            failure = ex.Message;
            return false;
        }
    }

    private static bool Rename(SafeFileHandle source, string destination, out string failure)
    {
        failure = string.Empty;

        // FILE_RENAME_INFO: flags (4 bytes, padded), RootDirectory (a pointer, always null here: the Win32 call
        // rejects a root handle), FileNameLength in bytes (4), FileName. No replace flag is set, so a name that is
        // taken fails instead of being overwritten. The destination folder is locked by the caller, so the path
        // cannot lead anywhere else while this runs.
        var lengthOffset = 2 * IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = destination.Length * sizeof(char);
        var size = nameOffset + nameBytes + sizeof(char);

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++)
                Marshal.WriteByte(buffer, i, 0);

            Marshal.WriteInt32(buffer, lengthOffset, nameBytes);
            Marshal.Copy(destination.ToCharArray(), 0, buffer + nameOffset, destination.Length);

            if (SetFileInformationByHandle(source, FileRenameInfoEx, buffer, (uint)size))
                return true;

            var error = Marshal.GetLastWin32Error();
            if (error is ErrorInvalidParameter or ErrorInvalidLevel or ErrorNotSupported)
            {
                if (SetFileInformationByHandle(source, FileRenameInfo, buffer, (uint)size))
                    return true;

                error = Marshal.GetLastWin32Error();
            }

            failure = new Win32Exception(error).Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
