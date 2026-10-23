using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>A started child as <see cref="ProcessRunner"/> drives it: its two output readers, a wait, an
/// exit code and a tree kill, whichever way it was started.</summary>
internal interface IChildProcess : IDisposable
{
    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    int ExitCode { get; }

    bool HasExited { get; }

    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Ends the child and everything it started. Never throws: it may already be gone.</summary>
    void Kill();
}

/// <summary>The ordinary launch, through <see cref="Process"/>.</summary>
internal sealed class ManagedChildProcess(Process process) : IChildProcess
{
    public StreamReader StandardOutput => process.StandardOutput;

    public StreamReader StandardError => process.StandardError;

    public int ExitCode => process.ExitCode;

    public bool HasExited => process.HasExited;

    public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process may have already exited between the check and the call, a benign race.
        }
        catch (Win32Exception)
        {
            // Same benign race, the other shape it can throw as.
        }
    }

    public void Dispose() => process.Dispose();
}

/// <summary>
/// Starts an image suspended, proves which image the system actually mapped, and only then lets it run.
/// The winget app-execution alias is a reparse point any process of this user can rewrite, even while a
/// verified handle to it is held open, so launching it by name leaves a window between the check and the
/// launch. Here the alias is launched with CREATE_SUSPENDED, the image the kernel resolved is read back
/// (<c>QueryFullProcessImageNameW</c>) and compared with the verified, pinned target; a mismatch ends the
/// process before a single instruction of it ran.
/// </summary>
internal sealed class SuspendedChildProcess : IChildProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x00020002;
    private const uint HandleFlagInherit = 0x1;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint WaitObject0 = 0;
    private const int ResumeFailed = -1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;

    private readonly SafeWaitHandle _process;
    private readonly SafeFileHandle? _job;
    private readonly AnonymousPipeServerStream _stdoutPipe;
    private readonly AnonymousPipeServerStream _stderrPipe;

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    private SuspendedChildProcess(
        SafeWaitHandle process, SafeFileHandle? job, AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr,
        Encoding stdoutEncoding, Encoding stderrEncoding)
    {
        _process = process;
        _job = job;
        _stdoutPipe = stdout;
        _stderrPipe = stderr;
        StandardOutput = new StreamReader(stdout, stdoutEncoding);
        StandardError = new StreamReader(stderr, stderrEncoding);
    }

    public int ExitCode =>
        GetExitCodeProcess(_process, out var code)
            ? unchecked((int)code)
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    public bool HasExited => WaitForSingleObject(_process, 0) == WaitObject0;

    /// <summary>The limit flags the child's job carries, 0 when there is no job.</summary>
    internal uint JobLimitFlags =>
        _job is { IsInvalid: false, IsClosed: false }
        && QueryInformationJobObject(_job, JobObjectExtendedLimitInformationClass, out var limits,
            (uint)Marshal.SizeOf<JobExtendedLimitInformation>(), IntPtr.Zero)
            ? limits.Basic.LimitFlags
            : 0;

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A wait handle over the process handle, which it must not close: the process handle is ours.
        using var waitHandle = new BorrowedWaitHandle(_process.DangerousGetHandle());
        var registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle, static (state, _) => ((TaskCompletionSource)state!).TrySetResult(), exited,
            Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await exited.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            registration.Unregister(null);
        }
    }

    public void Kill()
    {
        // Failure means the process or job is already gone, which is the state wanted.
        if (_job is { IsInvalid: false, IsClosed: false })
            _ = TerminateJobObject(_job, 1);
        else
            _ = TerminateProcess(_process, 1);
    }

    public void Dispose()
    {
        StandardOutput.Dispose();
        StandardError.Dispose();
        _stdoutPipe.Dispose();
        _stderrPipe.Dispose();
        _job?.Dispose();
        _process.Dispose();
    }

    /// <summary>
    /// Starts <paramref name="info"/>.FileName suspended, requires the image the system mapped to be
    /// <paramref name="expectedImagePath"/>, and resumes it. Throws <see cref="UntrustedToolException"/>
    /// (with the process already ended) on any other image, <see cref="Win32Exception"/> when the start
    /// itself fails.
    /// </summary>
    internal static SuspendedChildProcess Start(
        ProcessStartInfo info, string expectedImagePath, (string Family, string Aumid)? expectedPackage)
    {
        // Held from the creation of the inheritable pipe ends until our copies of them are closed: a
        // launch through Process.Start in that span would inherit the write ends.
        lock (ProcessRunner.ChildStartLock)
            return StartLocked(info, expectedImagePath, expectedPackage);
    }

    /// <summary>Test seam, invoked inside the start lock right after the pipes exist.</summary>
    internal static readonly AsyncLocal<Action?> AfterPipesCreatedForTest = new();

    private static SuspendedChildProcess StartLocked(
        ProcessStartInfo info, string expectedImagePath, (string Family, string Aumid)? expectedPackage)
    {
        var applicationPath = info.FileName;
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, applicationPath);
        foreach (var argument in info.ArgumentList)
            AppendArgument(commandLine, argument);

        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        SafeFileHandle? nul = null;
        SafeWaitHandle? process = null;
        SafeFileHandle? thread = null;
        SafeFileHandle? job = null;
        var environment = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        var handleArray = IntPtr.Zero;
        var attributeListInitialised = false;
        var started = false;

        try
        {
            AfterPipesCreatedForTest.Value?.Invoke();

            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            nul = CreateFileW("NUL", GenericRead, ShareReadWrite, ref security, OpenExisting, 0, IntPtr.Zero);
            if (nul.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var inheritedHandles = new[]
            {
                nul.DangerousGetHandle(),
                stdout.ClientSafePipeHandle.DangerousGetHandle(),
                stderr.ClientSafePipeHandle.DangerousGetHandle(),
            };

            // Only these three handles are inherited. Without the list the child would inherit every
            // inheritable handle of this process, including the pipes of a launch running at the same time.
            var attributeListSize = IntPtr.Zero;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize); // fails by design: asks the size
            if (attributeListSize == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            attributeListInitialised = true;

            handleArray = Marshal.AllocHGlobal(inheritedHandles.Length * IntPtr.Size);
            Marshal.Copy(inheritedHandles, 0, handleArray, inheritedHandles.Length);
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeHandleList, handleArray,
                    (IntPtr)(inheritedHandles.Length * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StdInput = inheritedHandles[0],
                    StdOutput = inheritedHandles[1],
                    StdError = inheritedHandles[2],
                },
                AttributeList = attributeList,
            };

            environment = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(info.Environment));

            if (!CreateProcessW(
                    applicationPath, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                    environment, string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory,
                    ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            process = new SafeWaitHandle(created.Process, ownsHandle: true);
            thread = new SafeFileHandle(created.Thread, ownsHandle: true);

            // The system's own answer to "what image did you map" and "which package identity did you get",
            // both read before the process ran anything. The identity is a token attribute set when the
            // process is created, so the suspended process already carries it.
            var mapped = QueryImagePath(process);
            var identityRead = TryReadPackageIdentity(process, out var family, out var aumid);
            if (!identityRead || !MayResume(mapped, expectedImagePath, family, aumid, expectedPackage))
            {
                _ = TerminateProcess(process, 1);
                throw new UntrustedToolException(
                    applicationPath, "the image or package identity the system started is not the verified executable");
            }

            // Put into a job so a timeout or Stop can end everything it started, as the tree kill of the
            // ordinary route does. No kill-on-close: what a finished install left running stays running.
            // A process already in a job that forbids nesting just keeps the single-process kill.
            job = CreateJobObjectW(IntPtr.Zero, null);

            // Installers that start their payload outside the job (a scheduled task, a service) must still
            // work; without BREAKAWAY_OK that start fails. A refused call keeps the job as it is: the tree
            // kill matters more. Cost: a child that breaks away is out of reach of Stop and the timeout.
            if (!job.IsInvalid)
            {
                var limits = new JobExtendedLimitInformation { Basic = { LimitFlags = JobObjectLimitBreakawayOk } };
                _ = SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobExtendedLimitInformation>());
            }

            if (!job.IsInvalid && !AssignProcessToJobObject(job, process))
            {
                job.Dispose();
                job = null;
            }

            if (ResumeThread(thread) == unchecked((uint)ResumeFailed))
            {
                var error = Marshal.GetLastWin32Error();
                _ = TerminateProcess(process, 1);
                throw new Win32Exception(error);
            }

            // Our copies of the write ends must go, or the readers never see the end of the output.
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();

            var child = new SuspendedChildProcess(
                process, job, stdout, stderr,
                info.StandardOutputEncoding ?? Encoding.UTF8, info.StandardErrorEncoding ?? Encoding.UTF8);
            started = true;
            return child;
        }
        finally
        {
            if (!started)
            {
                stdout.Dispose();
                stderr.Dispose();
                job?.Dispose();
                process?.Dispose();
            }

            thread?.Dispose();
            nul?.Dispose();
            if (attributeListInitialised)
                DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero)
                Marshal.FreeHGlobal(attributeList);
            if (handleArray != IntPtr.Zero)
                Marshal.FreeHGlobal(handleArray);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
        }
    }

    /// <summary>
    /// The resume decision. The right image alone is not enough: an alias rewritten to the genuine target
    /// but launched under another package family or application id would run elevated with an identity the
    /// attacker chose. <paramref name="expected"/> null means the child must carry no package identity at
    /// all. Anything unreadable arrives as null and so fails against a package, and an unexpected identity
    /// fails against null.
    /// </summary>
    internal static bool MayResume(
        string? mappedImage, string expectedImage, string? family, string? aumid, (string Family, string Aumid)? expected)
    {
        if (mappedImage is null || !string.Equals(mappedImage, expectedImage, StringComparison.OrdinalIgnoreCase))
            return false;

        if (expected is not { } package)
            return family is null && aumid is null;

        return family is not null && aumid is not null
            && string.Equals(family, package.Family, StringComparison.OrdinalIgnoreCase)
            && string.Equals(aumid, package.Aumid, StringComparison.OrdinalIgnoreCase);
    }

    private const int ErrorSuccess = 0;
    private const int AppModelErrorNoPackage = 15700;
    private const int AppModelErrorNoApplication = 15703;
    /// <summary>
    /// Package family and application id of the process, both read by the system from the process token.
    /// No package (or no application) is a legitimate null; any other failure is false so the caller
    /// refuses the process.
    /// </summary>
    private static bool TryReadPackageIdentity(SafeWaitHandle process, out string? family, out string? aumid)
    {
        family = null;
        aumid = null;
        return TryReadName(process, GetPackageFamilyName, out family)
            && TryReadName(process, GetApplicationUserModelId, out aumid);
    }

    private delegate int ProcessNameReader(SafeWaitHandle process, ref uint length, StringBuilder buffer);

    private static bool TryReadName(SafeWaitHandle process, ProcessNameReader read, out string? value)
    {
        value = null;
        var buffer = new StringBuilder(256);
        var length = (uint)buffer.Capacity;
        switch (read(process, ref length, buffer))
        {
            case ErrorSuccess:
                value = buffer.ToString();
                return true;
            case AppModelErrorNoPackage:
            case AppModelErrorNoApplication:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The Windows (drive letter) path of the process image, or null when it cannot be read.</summary>
    private static string? QueryImagePath(SafeWaitHandle process)
    {
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    /// <summary>The final path of an open file as a plain drive path, for comparing with
    /// <see cref="QueryImagePath"/>; null when it cannot be read.</summary>
    internal static string? PlainFinalPath(SafeFileHandle file)
    {
        var finalPath = Cleanup.VerifiedDelete.FinalPathOf(file);
        if (finalPath is null)
            return null;

        return finalPath.StartsWith(@"\\?\", StringComparison.Ordinal) && !finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? finalPath[4..]
            : null;
    }

    /// <summary>The block CreateProcess wants: NAME=value, NUL separated, sorted, closed by an extra NUL.</summary>
    private static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var block = new StringBuilder();
        foreach (var (key, value) in environment.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(key).Append('=').Append(value).Append('\0');

        return block.Append('\0').ToString();
    }

    /// <summary>One argument quoted the way CommandLineToArgvW and the C runtime read it back.</summary>
    internal static void AppendArgument(StringBuilder commandLine, string argument)
    {
        if (commandLine.Length != 0)
            commandLine.Append(' ');

        if (argument.Length != 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            commandLine.Append(argument);
            return;
        }

        commandLine.Append('"');
        var index = 0;
        while (index < argument.Length)
        {
            var c = argument[index++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (index < argument.Length && argument[index] == '\\')
                {
                    index++;
                    backslashes++;
                }

                if (index == argument.Length)
                {
                    commandLine.Append('\\', backslashes * 2);
                }
                else if (argument[index] == '"')
                {
                    commandLine.Append('\\', backslashes * 2 + 1).Append('"');
                    index++;
                }
                else
                {
                    commandLine.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                commandLine.Append('\\').Append('"');
            }
            else
            {
                commandLine.Append(c);
            }
        }

        commandLine.Append('"');
    }

    /// <summary>A wait handle over a process handle that someone else owns: disposing it closes nothing.</summary>
    private sealed class BorrowedWaitHandle : WaitHandle
    {
        public BorrowedWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobIoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation Basic;
        public JobIoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, ref SecurityAttributes securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeWaitHandle process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(SafeWaitHandle process, ref uint length, StringBuilder name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(SafeWaitHandle process, ref uint length, StringBuilder name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int informationClass, ref JobExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int informationClass, out JobExtendedLimitInformation information, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeWaitHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);
}
