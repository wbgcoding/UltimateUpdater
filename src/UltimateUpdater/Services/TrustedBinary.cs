using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// Authenticode check for an executable the ELEVATED worker is about to launch.
/// Why this exists: tools are resolved through %PATH%, and on a normal machine winget is reachable
/// only through %LOCALAPPDATA%\Microsoft\WindowsApps: a directory the user can write without admin.
/// Any process running as this user could drop its own winget.exe there and would then be executed
/// with the administrator token the user granted to THIS app, on the back of our UAC prompt.
/// Both signature forms have to be handled, and the catalog one is not optional: measured on Windows
/// 11, winget.exe, powershell.exe and notepad.exe are ALL catalog-signed and carry no embedded
/// signature at all. Verifying only the embedded form would reject every tool the worker runs and
/// break installing outright: so the file's hash is looked up in the system catalogs first, exactly
/// as Windows does it, and only then is the embedded signature tried.
/// </summary>
internal static class TrustedBinary
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint SaferFlag = 0x100;
    // Never block an install on a CRL fetch: revocation is not checked, so no network call is made.
    private const uint CacheOnlyUrlRetrieval = 0x1000;

    private const int MaxPath = 260;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnionInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CatalogInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr catAdmin, IntPtr subsystem, string hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr catAdmin, SafeFileHandle file, ref uint hashSize, byte[]? hash, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr catAdmin, byte[] hash, uint hashSize, uint flags, IntPtr previousCatInfo);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr catInfo, ref CatalogInfo info, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catInfo, uint flags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr catAdmin, uint flags);

    /// <summary>
    /// True for a file Windows itself would consider signed, by catalog or embedded signature.
    /// <paramref name="pinned"/> is the handle the caller is holding open across the launch: given one,
    /// BOTH forms read their bytes through it instead of reopening the path, so the answer is about the
    /// file that will actually run and not about whatever the path happens to name a moment later. Passing
    /// null answers for the path, fine for a question that is not about launching anything.
    /// </summary>
    public static bool IsSigned(string path, FileStream? pinned = null)
    {
        if (string.IsNullOrWhiteSpace(path) || (pinned is null && !File.Exists(path)))
            return false;

        try
        {
            return IsCatalogSigned(path, pinned) || IsEmbeddedSigned(path, pinned);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            // No way to tell a real tool from a planted one: refuse, do not guess.
            return false;
        }
    }

    private static bool IsCatalogSigned(string path, FileStream? pinned)
    {
        if (!CryptCATAdminAcquireContext2(out var catAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0))
            return false;

        var catInfoHandle = IntPtr.Zero;
        SafeFileHandle? opened = null;
        try
        {
            // The hash (the whole basis of a catalog verdict) is calculated from the pinned handle when
            // there is one; only without one is the path opened again here.
            var file = pinned is null ? opened = File.OpenHandle(path) : Rewound(pinned);

            uint hashSize = 0;
            CryptCATAdminCalcHashFromFileHandle2(catAdmin, file, ref hashSize, null, 0);
            if (hashSize == 0)
                return false;

            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle2(catAdmin, file, ref hashSize, hash, 0))
                return false;

            catInfoHandle = CryptCATAdminEnumCatalogFromHash(catAdmin, hash, hashSize, 0, IntPtr.Zero);
            if (catInfoHandle == IntPtr.Zero)
                return false;   // not in any catalog: the caller falls back to the embedded signature

            var catalog = new CatalogInfo { cbStruct = (uint)Marshal.SizeOf<CatalogInfo>(), wszCatalogFile = string.Empty };
            if (!CryptCATCatalogInfoFromContext(catInfoHandle, ref catalog, 0))
                return false;

            var hashHandle = GCHandle.Alloc(hash, GCHandleType.Pinned);
            try
            {
                var catalogInfo = new WinTrustCatalogInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                    pcwszCatalogFilePath = catalog.wszCatalogFile,
                    // the member tag is the file hash as an uppercase hex string
                    pcwszMemberTag = Convert.ToHexString(hash),
                    pcwszMemberFilePath = path,
                    hMemberFile = file.DangerousGetHandle(),
                    pbCalculatedFileHash = hashHandle.AddrOfPinnedObject(),
                    cbCalculatedFileHash = hashSize,
                    hCatAdmin = catAdmin,
                };

                return Verify(ChoiceCatalog, catalogInfo);
            }
            finally
            {
                hashHandle.Free();
            }
        }
        finally
        {
            if (catInfoHandle != IntPtr.Zero)
                CryptCATAdminReleaseCatalogContext(catAdmin, catInfoHandle, 0);

            CryptCATAdminReleaseContext(catAdmin, 0);
            opened?.Dispose();
        }
    }

    private static bool IsEmbeddedSigned(string path, FileStream? pinned)
    {
        // hFile is what wintrust reads the image from when it is given one, so the embedded signature is
        // checked on the same pinned bytes the catalog attempt just hashed.
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = path,
            hFile = pinned is null ? IntPtr.Zero : Rewound(pinned).DangerousGetHandle(),
            pgKnownSubject = IntPtr.Zero,
        };

        var verified = Verify(ChoiceFile, fileInfo);
        GC.KeepAlive(pinned);
        return verified;
    }

    /// <summary>The pinned handle is shared between the two attempts, and each of them walks the whole
    /// file: so it starts every one of them at the beginning rather than wherever the last left it.</summary>
    private static SafeFileHandle Rewound(FileStream pinned)
    {
        pinned.Seek(0, SeekOrigin.Begin);
        return pinned.SafeFileHandle;
    }

    private static bool Verify<T>(uint unionChoice, T unionInfo) where T : struct
    {
        var unionPtr = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(unionInfo, unionPtr, fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = unionChoice,
                pUnionInfo = unionPtr,
                dwStateAction = StateActionVerify,
                dwProvFlags = SaferFlag | CacheOnlyUrlRetrieval,
            };

            var action = GenericVerifyV2;
            var verified = WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;

            // The verify call allocates state that has to be handed back, or the provider leaks it.
            data.dwStateAction = StateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            return verified;
        }
        finally
        {
            Marshal.DestroyStructure<T>(unionPtr);
            Marshal.FreeHGlobal(unionPtr);
        }
    }
}
