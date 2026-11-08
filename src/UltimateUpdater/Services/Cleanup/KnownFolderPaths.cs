using System.IO;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>Folders that <see cref="Environment.SpecialFolder"/> has no entry for, asked of the shell by id.</summary>
internal static class KnownFolderPaths
{
    private static readonly Guid LocalAppDataLowId = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    /// <summary>
    /// The signed-in user's <c>AppData\LocalLow</c>, or null when the shell cannot say. Never derived from the
    /// Local folder by string surgery: a redirected profile keeps the two apart.
    /// </summary>
    internal static string? LocalAppDataLow()
    {
        var id = LocalAppDataLowId;
        var result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        try
        {
            if (result != 0 || pointer == IntPtr.Zero)
                return null;

            var path = Marshal.PtrToStringUni(pointer);
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? null : path;
        }
        finally
        {
            if (pointer != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
}
