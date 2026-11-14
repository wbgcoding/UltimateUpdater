using System.IO;

namespace UltimateUpdater.Services;

/// <summary>
/// Turns what a pseudo console writes (the screen text of the child with terminal control sequences mixed
/// in) into plain text the segment reader understands: every escape sequence is dropped, and the cursor
/// moves that start a line over (home, absolute column or row, up, down) become a carriage return, so a
/// redrawn progress bar still arrives as one segment per repaint. Works on UTF-8 bytes, which is safe because
/// every byte of an escape sequence is ASCII and no byte of a multi-byte character ever is. State carries
/// across calls, so a sequence split between two reads is still removed whole.
/// </summary>
internal sealed class VtTextFilter
{
    private const byte Escape = 0x1B;
    private const byte Bell = 0x07;

    private enum State
    {
        Text,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        StringBody,
        StringEscape,
    }

    private State _state = State.Text;

    /// <summary>
    /// Filters <paramref name="data"/> in place and returns how many bytes are left. The result is never
    /// longer than the input, which is what makes the in-place pass possible.
    /// </summary>
    public int Filter(Span<byte> data)
    {
        var written = 0;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            switch (_state)
            {
                case State.Text:
                    if (b == Escape)
                        _state = State.Escape;
                    else if (b >= 0x20 || b is (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\b')
                        data[written++] = b;

                    break;

                case State.Escape:
                    _state = b switch
                    {
                        (byte)'[' => State.Csi,
                        (byte)']' => State.Osc,
                        (byte)'P' or (byte)'X' or (byte)'^' or (byte)'_' => State.StringBody,
                        >= 0x20 and <= 0x2F => State.EscapeIntermediate,
                        Escape => State.Escape,
                        _ => State.Text,
                    };
                    break;

                case State.EscapeIntermediate:
                    if (b is < 0x20 or > 0x2F)
                        _state = State.Text;

                    break;

                case State.Csi:
                    if (b == Escape)
                    {
                        _state = State.Escape;
                    }
                    else if (b is >= 0x40 and <= 0x7E)
                    {
                        _state = State.Text;
                        if (StartsLineOver(b))
                            data[written++] = (byte)'\r';
                    }

                    break;

                case State.Osc:
                    if (b == Bell)
                        _state = State.Text;
                    else if (b == Escape)
                        _state = State.OscEscape;

                    break;

                case State.OscEscape:
                    // ESC \ ends the string; anything else after the ESC starts a new sequence.
                    _state = b == (byte)'\\' ? State.Text : b == (byte)'[' ? State.Csi : State.Osc;
                    break;

                case State.StringBody:
                    if (b == Escape)
                        _state = State.StringEscape;

                    break;

                case State.StringEscape:
                    _state = b == (byte)'\\' ? State.Text : State.StringBody;
                    break;
            }
        }

        return written;
    }

    /// <summary>Cursor home / position (H, f), column (G), row (d), line up and down (A, B, E, F): the moves
    /// a repainted line starts with. Forward and erase sequences carry no line change and are just dropped.</summary>
    private static bool StartsLineOver(byte finalByte) =>
        finalByte is (byte)'H' or (byte)'f' or (byte)'G' or (byte)'d' or (byte)'A' or (byte)'B' or (byte)'E' or (byte)'F';
}

/// <summary>A read-only stream that hands out the bytes of another one after <see cref="VtTextFilter"/>.</summary>
internal sealed class VtFilterStream(Stream inner) : Stream
{
    private readonly VtTextFilter _filter = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            var read = inner.Read(buffer);
            if (read == 0)
                return 0;

            // A chunk that was nothing but escape sequences filters to nothing; 0 would read as the end of
            // the stream, so keep reading until text arrives or the pipe really ends.
            var kept = _filter.Filter(buffer[..read]);
            if (kept > 0)
                return kept;
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return 0;

            var kept = _filter.Filter(buffer.Span[..read]);
            if (kept > 0)
                return kept;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }
}
