using System.Diagnostics;
using System.Text;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// winget prints its download bar only to a console, so its install runs go through a hidden pseudo console.
/// The text that comes back is the console's screen text with terminal sequences in it; these tests cover
/// the removal of those sequences, the segments the progress parser then sees, and a real console launch.
/// </summary>
public class PseudoConsoleTests
{
    private const string E = "\u001b";

    // Captured from winget 1.29 on a German Windows (a download of a small MSI), trimmed to the
    // interesting part: spinner repaints, the OSC progress report, the bar with its colours, the result lines.
    private static readonly string RealGermanCapture =
        E + "[?25l" + E + "]9;4;3;0" + E + "\\" + E + "[38;2;0;120;212m" + E + "[H   - " + E + "[m" + E + "[H" + E + "[K" + E + "[200C"
        + E + "]9;4;0;0" + E + "\\" + E + "[H" + E + "[?25hGefunden " + E + "[96m7-Zip " + E + "[m[" + E + "[96m7zip.7zip" + E + "[m] Version 26.06\r\n"
        + "Microsoft ist nicht verantwortlich und erteilt keine Lizenzen für Pakete von Drittanbietern.\r\n"
        + "Download läuft " + E + "[94mhttps://example.org/7z2606-x64.msi" + E + "[?25l" + E + "]9;4;3;0" + E + "\\"
        + E + "[m" + E + "[38;2;0;120;212m\r\n   - " + E + "[m" + E + "[38;2;0;120;212m\r   \\ " + E + "[m\r" + E + "[K" + E + "[200C"
        + E + "]9;4;0;0" + E + "\\" + E + "]9;4;1;47" + E + "\\\r  " + E + "[38;2;0;120;212m" + E + "[48;2;0;40;70m"
        + new string('█', 14) + E + "[m" + new string('▒', 16) + E + "[m  1.91 MB / 4.00 MB" + E + "]9;4;0;0" + E + "\\"
        + "\r  " + E + "[38;2;0;120;212m" + E + "[48;2;0;40;70m" + new string('█', 30) + E + "[m  4.00 MB / 4.00 MB"
        + E + "]9;4;0;0" + E + "\\\r\nDer Installer-Hash wurde erfolgreich überprüft\r\n" + E + "[?25h" + E + "[?9001l" + E + "[?1004l";

    private static string Filter(string raw, int chunk)
    {
        var filter = new VtTextFilter();
        var bytes = Encoding.UTF8.GetBytes(raw);
        var result = new List<byte>();
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var part = bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)).ToArray();
            result.AddRange(part.AsSpan(0, filter.Filter(part)).ToArray());
        }

        return Encoding.UTF8.GetString([.. result]);
    }

    [Fact]
    public void Escape_sequences_are_removed_and_the_text_is_kept()
    {
        var text = Filter(RealGermanCapture, int.MaxValue);

        Assert.DoesNotContain('\u001b', text);
        Assert.Contains("Gefunden 7-Zip [7zip.7zip] Version 26.06", text);
        Assert.Contains("Download läuft https://example.org/7z2606-x64.msi", text);
        Assert.Contains("1.91 MB / 4.00 MB", text);
        Assert.Contains("Der Installer-Hash wurde erfolgreich überprüft", text);
        Assert.DoesNotContain("9;4;", text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void A_sequence_split_between_reads_is_removed_all_the_same(int chunk) =>
        Assert.Equal(Filter(RealGermanCapture, int.MaxValue), Filter(RealGermanCapture, chunk));

    [Fact]
    public void An_unterminated_string_sequence_hides_nothing_before_it_and_a_later_terminator_ends_it()
    {
        Assert.Equal("a", Filter("a" + E + "]0;title", int.MaxValue));
        Assert.Equal("ab", Filter("a" + E + "]0;title\u0007b", int.MaxValue));
        Assert.Equal("ab", Filter("a" + E + "P1$r" + E + "\\b", int.MaxValue));
        Assert.Equal("ab", Filter("a" + E + "(Bb", int.MaxValue));
    }

    [Fact]
    public void A_chunk_made_only_of_sequences_does_not_read_as_the_end_of_the_stream()
    {
        using var inner = new MemoryStream(Encoding.UTF8.GetBytes(E + "[?25l" + E + "[m"));
        using var stream = new VtFilterStream(inner);

        Assert.Equal(0, stream.Read(new byte[16], 0, 16));

        using var pipe = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
        using var writer = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.Out, pipe.GetClientHandleAsString());
        using var filtered = new VtFilterStream(pipe);
        writer.Write(Encoding.UTF8.GetBytes(E + "[?25l"));
        writer.Write(Encoding.UTF8.GetBytes("hi"));
        writer.Flush();

        var buffer = new byte[16];
        Assert.Equal(2, filtered.Read(buffer, 0, buffer.Length));
        Assert.Equal("hi", Encoding.UTF8.GetString(buffer, 0, 2));
    }

    private static async Task<List<string>> SegmentsOf(string raw)
    {
        using var inner = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        using var reader = new StreamReader(new VtFilterStream(inner), Encoding.UTF8);
        var segments = new List<string>();
        await ProcessRunner.PumpAsync(reader, new StringBuilder(), segments.Add, CancellationToken.None);
        return segments;
    }

    [Fact]
    public async Task Each_repaint_of_the_bar_arrives_as_its_own_segment_the_parser_reads()
    {
        var segments = await SegmentsOf(RealGermanCapture);

        var readings = segments
            .Select(s => ProgressParser.ToInstallProgress(s, "7-Zip"))
            .Where(p => p is not null)
            .Select(p => (p!.BytesReceived, p.BytesTotal, p.PercentComplete))
            .ToList();

        // the closing "installer hash verified" line reads as the install phase and carries no figure
        Assert.Equal(3, readings.Count);
        Assert.Equal(0, readings[2].PercentComplete);
        Assert.Equal((long)Math.Round(1.91 * 1024 * 1024), readings[0].BytesReceived);
        Assert.Equal(4L * 1024 * 1024, readings[0].BytesTotal);
        Assert.InRange(readings[0].PercentComplete, 47, 48);
        Assert.Equal(100, readings[1].PercentComplete);
        Assert.Contains(segments, s => s.Contains("überprüft", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_english_bar_and_the_percent_form_are_read_too()
    {
        var raw = "\r  " + E + "[38;2;0;120;212m" + new string('█', 10) + E + "[m" + new string('▒', 20)
            + E + "[m  12.3 MB / 80.0 MB" + E + "]9;4;1;15" + E + "\\"
            + "\r  " + E + "[38;2;0;120;212m" + new string('█', 15) + E + "[m" + new string('▒', 15) + E + "[m  36%\r\n";

        var readings = (await SegmentsOf(raw))
            .Select(s => ProgressParser.Parse(s))
            .OfType<ProgressSample>()
            .ToList();

        Assert.Equal(2, readings.Count);
        Assert.Equal(80L * 1024 * 1024, readings[0].BytesTotal);
        Assert.InRange(readings[0].Percent!.Value, 15, 16);
        Assert.Equal(36, readings[1].Percent);
    }

    [Fact]
    public async Task A_cursor_move_to_a_new_position_ends_the_segment_but_moving_forward_does_not()
    {
        var segments = await SegmentsOf("12 MB" + E + "[2;1H" + "13 MB" + E + "[5C" + " / 80 MB");

        Assert.Equal(["12 MB", "13 MB / 80 MB"], segments);
    }

    // A full path: a bare name does not resolve for the suspended launch, which would quietly fall back to pipes.
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>Records the runner's notes; a note about a missing pseudo console means the run used pipes
    /// and proved nothing about the console route.</summary>
    private sealed class NoteScope : IDisposable
    {
        private readonly List<string> _notes = [];

        public NoteScope() => ProcessRunner.Diagnostics = _notes.Add;

        public void AssertConsoleWasUsed() => Assert.True(_notes.Count == 0, string.Join(" | ", _notes));

        public void Dispose() => ProcessRunner.Diagnostics = null;
    }

    [Fact]
    public async Task A_console_command_runs_through_the_pseudo_console_with_its_output_and_exit_code()
    {
        using var notes = new NoteScope();
        var segments = new List<string>();

        var result = await ProcessRunner.RunStreamingAsync(
            Cmd, ["/c", "echo hello from the console & exit 7"], TimeSpan.FromSeconds(60), segments.Add,
            CancellationToken.None, pseudoConsole: true);

        notes.AssertConsoleWasUsed();
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("hello from the console", result.StandardOutput);
        Assert.DoesNotContain('\u001b', result.StandardOutput);
        Assert.Contains(segments, s => s.Contains("hello from the console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Umlauts_survive_the_console()
    {
        using var notes = new NoteScope();

        var result = await ProcessRunner.RunStreamingAsync(
            Cmd, ["/c", "echo Prüfung läuft"], TimeSpan.FromSeconds(60), _ => { },
            CancellationToken.None, pseudoConsole: true);

        notes.AssertConsoleWasUsed();
        Assert.Contains("Prüfung läuft", result.StandardOutput);
    }

    [Fact]
    public async Task A_cancel_ends_a_console_run_and_its_children_promptly()
    {
        using var notes = new NoteScope();
        using var cts = new CancellationTokenSource();
        var before = PingIds();
        var clock = Stopwatch.StartNew();

        var run = ProcessRunner.RunStreamingAsync(
            Cmd, ["/c", "ping -n 60 127.0.0.1"], TimeSpan.FromMinutes(5), _ => { }, cts.Token, pseudoConsole: true);

        await Task.Delay(1500);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        notes.AssertConsoleWasUsed();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"the cancel took {clock.Elapsed}");
        // the ping the shell started must be gone as well (only pings that appeared during this run count)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<int> alive;
        do
        {
            alive = NewPingIds(before);
            if (alive.Count == 0) break;
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        Assert.True(alive.Count == 0, "ping still running after the cancel: " + string.Join(", ", alive));
    }

    private static HashSet<int> PingIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName("PING")) { ids.Add(p.Id); p.Dispose(); }
        return ids;
    }

    private static List<int> NewPingIds(HashSet<int> before)
    {
        var alive = new List<int>();
        foreach (var p in Process.GetProcessesByName("PING"))
        {
            using (p)
            {
                try { if (!before.Contains(p.Id) && !p.HasExited) alive.Add(p.Id); } catch { }
            }
        }
        return alive;
    }

    [Fact]
    public async Task A_silent_console_run_is_stopped_by_the_idle_timeout()
    {
        using var notes = new NoteScope();

        await Assert.ThrowsAsync<ProcessIdleTimeoutException>(() => ProcessRunner.RunStreamingAsync(
            Cmd, ["/c", "ping -n 60 127.0.0.1 > nul"], TimeSpan.FromMinutes(5), _ => { }, CancellationToken.None,
            idleTimeout: TimeSpan.FromSeconds(3), pseudoConsole: true));

        notes.AssertConsoleWasUsed();
    }
}
