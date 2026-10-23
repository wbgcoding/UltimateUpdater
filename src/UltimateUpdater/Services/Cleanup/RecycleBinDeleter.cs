using System.IO;
using System.Runtime.InteropServices;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Deletes a whole folder via the shell's own recycle-bin operation; never `Directory.Delete`. There is
/// no backup mechanism for an arbitrary folder the way the registry cleaner
/// has `reg export`; the recycle bin IS that safety net here, and it is the one thing that makes a wrong
/// leftover judgment recoverable rather than permanent.
/// </summary>
public static class RecycleBinDeleter
{
    // IFileOperation rather than SHFileOperation: with "allow undo" and no confirmation the old call
    // deleted an item for good when it could not be recycled (too large for the bin, bin switched off on
    // that drive) and still reported success. FOFX_RECYCLEONDELETE asks for recycling but does not
    // guarantee it, so a progress sink also refuses every delete the shell does not mean to recycle.
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofSilent = 0x0004;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;

    private const int EFail = unchecked((int)0x80004005);
    private const int EAbort = unchecked((int)0x80004004);

    /// <summary>TSF_DELETE_RECYCLE_IF_POSSIBLE: set by the shell on a delete it is going to send to the bin.</summary>
    private const uint TsfDeleteRecycleIfPossible = 0x80;

    private static readonly Guid FileOperationClsid = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid ShellItemIid = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(uint sigdnName, out IntPtr ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    // Only the flags, delete and perform members are called, but a COM vtable is positional: every member
    // before them has to be declared, in the interface's own order.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise([MarshalAs(UnmanagedType.Interface)] IFileOperationProgressSink pfops, out uint pdwCookie);

        void Unadvise(uint dwCookie);

        void SetOperationFlags(uint dwOperationFlags);

        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

        void SetProgressDialog(IntPtr popd);

        void SetProperties(IntPtr pproparray);

        void SetOwnerWindow(IntPtr hwndOwner);

        void ApplyPropertiesToItem(IShellItem psiItem);

        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);

        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);

        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);

        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);

        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);

        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);

        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);

        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);

        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IntPtr pfopsItem);

        void PerformOperations();

        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    // Every member of the interface, in the order shobjidl_core.h declares them: a COM vtable is positional,
    // so a missing or moved member would make the shell call the wrong method. All return an HRESULT directly.
    [ComImport, ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();

        [PreserveSig] int FinishOperations(int hrResult);

        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrRename, IShellItem psiNewlyCreated);

        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrMove, IShellItem psiNewlyCreated);

        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, int hrCopy, IShellItem psiNewlyCreated);

        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);

        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated);

        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem);

        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);

        [PreserveSig] int ResetTimer();

        [PreserveSig] int PauseTimer();

        [PreserveSig] int ResumeTimer();
    }

    /// <summary>
    /// Refuses a delete (E_ABORT) unless the shell says it will go to the recycle bin; the item then stays
    /// where it is. Everything else is a plain S_OK.
    /// </summary>
    [ComVisible(true)]
    private sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        public int StartOperations() => 0;

        public int FinishOperations(int hrResult) => 0;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName) => 0;

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string pszNewName, int hrRename, IShellItem psiNewlyCreated) => 0;

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrMove, IShellItem psiNewlyCreated) => 0;

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName) => 0;

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string pszNewName, int hrCopy, IShellItem psiNewlyCreated) => 0;

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            LastPreDeleteFlagsForTest = dwFlags;
            return AllowDelete(dwFlags) ? 0 : EAbort;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem psiNewlyCreated) => 0;

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName) => 0;

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string pszNewName, string pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem psiNewItem) => 0;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }

    /// <summary>The decision of the progress sink: a delete may go ahead only when the shell has marked it as
    /// one that recycles. Anything else would remove the item for good.</summary>
    internal static bool AllowDelete(uint deleteFlags) => (deleteFlags & TsfDeleteRecycleIfPossible) != 0;

    /// <summary>The flags the sink last saw on a delete; lets a live test prove the sink is really called.</summary>
    internal static uint? LastPreDeleteFlagsForTest { get; set; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    /// <summary>
    /// The one place that turns the shell's answer into yes or no. Anything but a clean success is a
    /// refusal: a failed or aborted operation, or an item that is still where it was, must never read as
    /// "moved to the recycle bin".
    /// </summary>
    internal static bool Succeeded(int hresult, bool anyOperationsAborted, bool itemStillExists) =>
        hresult >= 0 && !anyOperationsAborted && !itemStillExists;

    /// <summary>True when the item was moved to the recycle bin. False when it could not be: it then stays
    /// on disk, never deleted for good. Works for a single file (the "recycle bin instead of deleting"
    /// option) or a whole folder alike.
    /// <paramref name="path"/> must already have passed every judgment call; this performs no safety
    /// checks of its own, same "impure edge, pure logic decides elsewhere first" split as every other
    /// deleter in this app.</summary>
    public static bool DeleteToRecycleBin(string path, LoggingService logging, CancellationToken cancellationToken = default) =>
        DeleteToRecycleBin(path, logging, RunOperation, OperationTimeout, cancellationToken);

    /// <summary>How long one shell move may take. A hung shell extension or a stalled network drive would
    /// otherwise hold the whole cleanup, and its cancel button, for good.</summary>
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The same move with the shell call and the time limit injectable, so the wait can be proven
    /// without a shell that really hangs.</summary>
    internal static bool DeleteToRecycleBin(
        string path, LoggingService logging, Func<string, LoggingService, (int HResult, bool Aborted)> operation,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !(Directory.Exists(path) || File.Exists(path)))
            return false;

        // IFileOperation wants a single-threaded apartment; a dedicated thread keeps that true whatever
        // thread the caller (a pool thread, the elevated worker) happens to be on.
        var result = (HResult: EFail, Aborted: true);
        var thread = new Thread(() =>
        {
            // A dedicated thread has no caller to hand an exception to: one escaping here would end the
            // process, so whatever RunOperation did not anticipate becomes a failed move instead.
            try
            {
                result = operation(path, logging);
            }
            catch (Exception ex)
            {
                logging.Warn($"Cleanup: recycle-bin delete failed unexpectedly: {ex.Message}");
                result = (ex.HResult < 0 ? ex.HResult : EFail, true);
            }
        })
        {
            IsBackground = true,
            Name = "RecycleBinDelete",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // A shell call cannot be interrupted from outside, so a stuck one is abandoned: the thread is a
        // background thread and ends with the process, and the item counts as not moved.
        var deadline = DateTime.UtcNow + timeout;
        while (!thread.Join(TimeSpan.FromMilliseconds(100)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                logging.Warn($"Cleanup: recycle-bin delete of '{path}' was cancelled; the shell call is abandoned.");
                return false;
            }

            if (DateTime.UtcNow >= deadline)
            {
                logging.Warn($"Cleanup: the shell did not answer for '{path}' within {timeout.TotalSeconds:0} s; giving up on it.");
                return false;
            }
        }

        var stillThere = Directory.Exists(path) || File.Exists(path);
        if (!Succeeded(result.HResult, result.Aborted, stillThere))
        {
            logging.Warn($"Cleanup: could not move '{path}' to the recycle bin (0x{result.HResult:X8}); it was left in place.");
            return false;
        }

        return true;
    }

    private static (int HResult, bool Aborted) RunOperation(string path, LoggingService logging)
    {
        object? operation = null;
        uint cookie = 0;
        try
        {
            var type = Type.GetTypeFromCLSID(FileOperationClsid);
            if (type is null)
                return (EFail, true);

            operation = Activator.CreateInstance(type);
            var fileOperation = (IFileOperation)operation!;
            fileOperation.SetOperationFlags(FofNoConfirmation | FofSilent | FofNoErrorUi | FofxRecycleOnDelete);
            fileOperation.Advise(new RecycleOnlySink(), out cookie);

            var iid = ShellItemIid;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            try
            {
                fileOperation.DeleteItem(item, IntPtr.Zero);
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }

            fileOperation.PerformOperations();
            fileOperation.GetAnyOperationsAborted(out var aborted);
            return (0, aborted);
        }
        catch (COMException ex)
        {
            return (ex.HResult, true);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidCastException
                                       or InvalidOperationException or PlatformNotSupportedException)
        {
            logging.Warn($"Cleanup: recycle-bin delete unavailable: {ex.Message}");
            return (EFail, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or OperationCanceledException)
        {
            // The runtime maps common HRESULTs to these instead of COMException: 0x80070002 (the file vanished
            // between the caller's check and this call) becomes FileNotFoundException, E_ACCESSDENIED an
            // UnauthorizedAccessException, ERROR_CANCELLED an OperationCanceledException. Uncaught, any of them
            // ended this dedicated thread, and an unhandled exception on any thread ends the whole process.
            return (ex.HResult < 0 ? ex.HResult : EFail, true);
        }
        finally
        {
            if (operation is not null && Marshal.IsComObject(operation))
            {
                // Also after an aborted or failed PerformOperations, which throws before the normal path ends.
                if (cookie != 0)
                {
                    try
                    {
                        ((IFileOperation)operation).Unadvise(cookie);
                    }
                    catch (Exception ex) when (ex is COMException or InvalidCastException)
                    {
                        // the operation is released right below; nothing left to undo
                    }
                }

                Marshal.ReleaseComObject(operation);
            }
        }
    }
}
