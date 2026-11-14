using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UltimateUpdater.Services;

/// <summary>
/// A hidden pseudo console (ConPTY) a child is attached to instead of plain pipes. winget prints its download
/// bar (size and percent) only when its output is a console; to a redirected pipe it prints the line
/// "Downloading url" and nothing else, so a run through pipes can never show real progress. The host owns the
/// two pipes of the console and the console itself; <see cref="Output"/> is the child's screen text with every
/// escape sequence already removed (<see cref="VtTextFilter"/>), ready for the segment reader.
/// </summary>
internal sealed class PseudoConsoleHost : IDisposable
{
    /// <summary>Wide enough that winget's bar and its size text never wrap onto a second line.</summary>
    internal const short Columns = 200;

    internal const short Rows = 50;

    private readonly AnonymousPipeServerStream _input;
    private readonly AnonymousPipeServerStream _output;
    private IntPtr _console;
    private int _closeStarted;

    /// <summary>The child's output as plain text: UTF-8 bytes, escape sequences removed, redraws as '\r'.</summary>
    public Stream Output { get; }

    /// <summary>The console handle for <c>PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE</c>.</summary>
    public IntPtr Handle => _console;

    private PseudoConsoleHost(AnonymousPipeServerStream input, AnonymousPipeServerStream output, IntPtr console)
    {
        _input = input;
        _output = output;
        _console = console;
        Output = new VtFilterStream(output);
    }

    /// <summary>Creates the console. Throws <see cref="Win32Exception"/> (or the platform's missing-entry
    /// exceptions) where pseudo consoles are not available; the caller falls back to pipes.</summary>
    public static PseudoConsoleHost Create()
    {
        // Pipes the console writes to and reads from: ours are the server ends, the console gets the client
        // ends and duplicates them, so the client copies are closed again right after the call.
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        try
        {
            var hr = CreatePseudoConsole(
                new Coord { X = Columns, Y = Rows }, input.ClientSafePipeHandle, output.ClientSafePipeHandle, 0, out var console);
            if (hr != 0)
                throw new Win32Exception(hr, "CreatePseudoConsole failed");

            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            return new PseudoConsoleHost(input, output, console);
        }
        catch
        {
            input.Dispose();
            output.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes the console on a pool thread and returns at once. Closing flushes what the child still printed
    /// and only then ends the output pipe, so the reader sees its last lines and then the end of the stream;
    /// on older Windows the close itself waits until that output has been read, which is why it must never
    /// run on the thread that reads it. A process still attached to the console is ended by the close.
    /// </summary>
    public void CloseInBackground()
    {
        if (Interlocked.Exchange(ref _closeStarted, 1) != 0)
            return;

        var console = _console;
        _ = Task.Run(() =>
        {
            try
            {
                ClosePseudoConsole(console);
            }
            catch
            {
                // best effort: the console is going away with the process either way
            }
        });
    }

    public void Dispose()
    {
        CloseInBackground();
        Output.Dispose();
        _input.Dispose();
        _output.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(
        Coord size, SafePipeHandle input, SafePipeHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
}
