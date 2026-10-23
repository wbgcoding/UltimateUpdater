using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace UltimateUpdater.Services;

/// <summary>Rolling daily log file under AppPaths.LogsDirectory. Never throws, a failed log write must not crash the app.</summary>
public sealed class LoggingService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// A log file is rolled into the archive once it reaches this size, independent of the day rollover,
    /// a single day that logs unusually heavily (a stuck retry loop, a very talkative tool) must never be
    /// allowed to grow into the gigabytes.
    /// </summary>
    private const long MaxLogFileBytes = 5 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Action<string>? _sink;
    private readonly string _logsDirectory;

    /// <summary>
    /// The disk writes (open/rotate/append/flush) run on one dedicated consumer drained from this bounded
    /// channel, so a caller on any thread never blocks on file I/O or a mid-write rotation/zip. Null in
    /// sink mode: the elevated worker never touches disk (see the sink constructor's remarks). Bounded so
    /// a flood cannot grow into unbounded memory; <see cref="EnqueueJob"/> drops and counts a line rather
    /// than wait on the rare occasion it is full.
    /// </summary>
    private readonly Channel<WriteJob>? _queue;

    private const int QueueCapacity = 4096;

    /// <summary>One item on <see cref="_queue"/>: either a line to write, or a barrier that
    /// <see cref="Flush"/> waits on so it only returns once every line queued before it has actually
    /// landed on disk.</summary>
    private sealed class WriteJob
    {
        public string? Line;
        public TaskCompletionSource? Barrier;
    }

    /// <summary>Guards the once-per-day sweep for a stale log file left over from before the app was last
    /// closed, checked on every write, but the actual directory scan only runs once that date changes.
    /// Only ever touched from the consumer thread.</summary>
    private DateTime _lastArchiveSweep = DateTime.MinValue;
    private DateTime _nextRotationAttempt = DateTime.MinValue;
    private static readonly TimeSpan RotationRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>The handle onto today's (or the currently rotated) log file. It stays open for one drained
    /// batch and is shared by every line in it, then closed (see <see cref="ConsumeAsync"/>); only ever
    /// touched from the consumer thread.</summary>
    private StreamWriter? _openWriter;
    private string? _openPath;
    private long _openFileBytes;

    /// <summary>
    /// <paramref name="logsDirectory"/> exists so <see cref="ReadAround"/> and the write path can be
    /// exercised against a temp directory in tests, same optional-path convention HistoryService already
    /// uses. Production code never passes it and gets the real AppPaths location.
    /// </summary>
    public LoggingService(string? logsDirectory = null)
    {
        _logsDirectory = logsDirectory ?? AppPaths.LogsDirectory;
        _queue = Channel.CreateBounded<WriteJob>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Logging for the ELEVATED worker, which writes nothing to disk. Every directory the normal app logs
    /// into (%APPDATA%, the data folder beside the exe, %TEMP%) belongs to the unelevated user, who can
    /// replace it with a reparse point aimed somewhere only administrators may write, which would turn
    /// each log line into an arbitrary create-or-append as admin. The worker keeps its lines in memory and
    /// hands them to the app over its pipe. Deliberately does NOT read <see cref="AppPaths"/>: that would
    /// run its static initializer (data directory resolution, folder creation) AS ADMIN, following a
    /// same-user-writable pointer file. <see cref="_logsDirectory"/> stays empty;
    /// <see cref="Append"/> returns through the sink before <see cref="LogFilePath"/> is ever built.
    /// </summary>
    public LoggingService(Action<string> sink)
    {
        _sink = sink;
        _logsDirectory = string.Empty;
    }

    /// <summary>Resolved per write, not once: an app left running overnight otherwise keeps appending to yesterday's file.</summary>
    private string LogFilePath => Path.Combine(_logsDirectory, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd}.log"));

    public void Info(string message) => Write('I', null, message);

    public void Warn(string message) => Write('W', null, message);

    public void Error(string message, Exception? exception = null) =>
        Write('E', null, exception is null ? message : $"{message}: {exception}");

    /// <summary>
    /// One recorded event, tagged with a short category so a whole class of them can be picked out of a
    /// day's file at a glance: <c>app</c>, <c>scan</c>, <c>inst</c>, <c>dl</c>, <c>clean</c>, <c>self</c>.
    /// Everything that matters for reading a fault back later, or for answering "when was this installed",
    /// goes through here rather than being written as prose.
    /// </summary>
    public void Event(string category, string message) => Write('I', category, message);

    /// <summary>The same, for an event that went wrong, the category still applies.</summary>
    public void EventFailed(string category, string message) => Write('W', category, message);

    /// <summary>A read-around result is capped here rather than left open-ended, a busy log around the
    /// requested time is still a fallback for a MISSING stored detail, not a substitute for one.</summary>
    private const int MaxReadAroundLines = 200;

    /// <summary>
    /// Lines from the day's log file(s) whose own timestamp falls within <paramref name="window"/> of
    /// <paramref name="when"/>, read from today's file and the previous day's when the window crosses
    /// midnight. <paramref name="category"/>, when given, keeps only lines written through
    /// <see cref="Event"/>/<see cref="EventFailed"/> with that exact category (see the class doc on the
    /// line format, space-separated, no brackets). Read-only, so it is safe to call at any time; a
    /// missing or unreadable file is simply skipped like everywhere else in this class.
    /// </summary>
    public IReadOnlyList<string> ReadAround(DateTimeOffset when, TimeSpan window, string? category = null)
    {
        // Sink mode (the elevated worker) never had a real log directory to read around in the
        // first place, _logsDirectory stays empty on purpose (see the sink constructor's own remarks); an
        // empty string is a RELATIVE path, so without this guard a future worker-reachable caller would
        // silently read around the process's current directory instead of simply getting nothing back.
        if (_sink is not null)
            return [];

        var local = when.ToLocalTime().DateTime;
        var start = local - window;
        var end = local + window;

        var result = new List<string>();
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            foreach (var line in ReadDayLines(day))
            {
                if (!TryParseTimestamp(line, day, out var lineTime) || lineTime < start || lineTime > end)
                    continue;
                if (category is { Length: > 0 } && !MatchesCategory(line, category))
                    continue;

                result.Add(line);
                if (result.Count >= MaxReadAroundLines)
                    return result;
            }
        }

        return result;
    }

    /// <summary>
    /// Every line written on <paramref name="day"/>, oldest first: the archives that day's file was rotated
    /// into (a size rollover, or the daily sweep that zips every earlier day), then the plain file itself.
    /// Without the archives, anything older than today could never be found, since the first write of a day
    /// already zips and deletes every earlier plain log.
    /// </summary>
    private IEnumerable<string> ReadDayLines(DateTime day)
    {
        var baseName = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var archiveDir = Path.Combine(_logsDirectory, ArchiveDirectoryName);

        var archives = new List<string>();
        try
        {
            if (Directory.Exists(archiveDir))
            {
                archives.AddRange(Directory.EnumerateFiles(archiveDir, baseName + ".zip"));
                archives.AddRange(Directory.EnumerateFiles(archiveDir, baseName + "_*.zip")
                    .OrderBy(path => int.TryParse(
                        Path.GetFileNameWithoutExtension(path).AsSpan(baseName.Length + 1),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var archive in archives)
        {
            foreach (var line in ReadLinesOrEmpty(() =>
                     {
                         using var zip = ZipFile.OpenRead(archive);
                         var entry = zip.Entries.FirstOrDefault();
                         if (entry is null)
                             return [];
                         using var reader = new StreamReader(entry.Open(), Utf8NoBom);
                         return reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                     }))
            {
                yield return line;
            }
        }

        var path = Path.Combine(_logsDirectory, baseName + ".log");
        if (File.Exists(path))
        {
            foreach (var line in ReadLinesOrEmpty(() => File.ReadAllLines(path, Utf8NoBom)))
                yield return line;
        }
    }

    private static string[] ReadLinesOrEmpty(Func<string[]> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>The line's leading "HH:mm:ss" against the file's own date, the file name carries the
    /// date, the line itself never repeats it (see the class doc on the line format).</summary>
    private static bool TryParseTimestamp(string line, DateTime day, out DateTime timestamp)
    {
        timestamp = default;
        if (line.Length < 8 || line[2] != ':' || line[5] != ':')
            return false;

        if (!TimeSpan.TryParseExact(line[..8], "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out var timeOfDay))
            return false;

        timestamp = day.Date + timeOfDay;
        return true;
    }

    /// <summary>"HH:mm:ss LEVEL CATEGORY message…", the category is the third space-separated token,
    /// when the line carries one at all (a plain Info/Warn/Error line has none).</summary>
    private static bool MatchesCategory(string line, string category)
    {
        var firstSpace = line.IndexOf(' ');
        if (firstSpace < 0)
            return false;

        var secondSpace = line.IndexOf(' ', firstSpace + 1);
        if (secondSpace < 0)
            return false;

        var thirdSpace = line.IndexOf(' ', secondSpace + 1);
        var categoryToken = thirdSpace < 0 ? line[(secondSpace + 1)..] : line[(secondSpace + 1)..thirdSpace];
        return categoryToken == category;
    }

    public void CleanupOldLogs(int retentionDays)
    {
        // Same reasoning as ReadAround's own guard just above, sink mode has no real log
        // directory to sweep; without this an empty _logsDirectory (a relative path) would let a future
        // worker-reachable caller delete files relative to the process's current directory instead.
        if (_sink is not null)
            return;

        // A non-positive value puts the cutoff in the future and deletes every log, including the one
        // written seconds ago. settings.json is hand-editable, so this is not hypothetical.
        if (retentionDays < 1)
            return;

        // Capped: a hand-edited huge value would make AddDays throw and housekeeping never run.
        var cutoff = DateTime.Now.AddDays(-Math.Min(retentionDays, 36500));

        DeleteOlderThan(_logsDirectory, "*.log", cutoff);
        DeleteOlderThan(Path.Combine(_logsDirectory, ArchiveDirectoryName), "*.zip", cutoff);
    }

    private static void DeleteOlderThan(string directory, string searchPattern, DateTime cutoff)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(directory, searchPattern);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // housekeeping is never worth failing a startup over, including a directory that never
            // got created yet because nothing has been archived.
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort cleanup; retried next start
            }
        }
    }

    /// <summary>
    /// The user's profile directory, and what it is replaced with. A failing install is logged with the
    /// tool's own output, and package managers print absolute paths, so the log filled up with
    /// "C:\Users\&lt;name&gt;\...", which is the user's name in a file they may well hand to someone else
    /// for help. The diagnostic value of the path is kept; the identity is not.
    /// </summary>
    private static readonly string ProfilePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    /// <summary>
    /// The same directory the way npm, pip and node print it. Those tools echo Windows paths with forward
    /// slashes, and that spelling walked straight past the replacement above with the account name in it.
    /// </summary>
    private static readonly string ProfilePathWithForwardSlashes = ProfilePath.Replace('\\', '/');

    private static readonly Regex ProfilePathPattern = new(
        $@"(?i)(?:{Regex.Escape(ProfilePath)}|{Regex.Escape(ProfilePathWithForwardSlashes)})(?![^\\/\s""'])",
        RegexOptions.Compiled);

    /// <summary>
    /// The credential shapes that turn up in a package manager's own output: a private registry in an
    /// .npmrc with its token, a git remote carrying a password, an authorization header echoed back by a
    /// tool that failed. None of it is ours, all of it lands in the log verbatim, and the log is a file
    /// the user may hand to someone else, so the shape is kept and the value is not.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] SecretPatterns =
    [
        // scheme://user:secret@host
        // (the secret may itself contain "@": it runs to the LAST "@" before the host)
        (new Regex(@"(?<=://)[^\s/:@]+:[^\s/]+(?=@)", RegexOptions.Compiled), "***:***"),
        // ?token=… &api_key=… &access_token=…
        (new Regex(@"(?i)([?&](?:api[_-]?key|access[_-]?token|auth|token|secret|password|pwd)=)[^&\s""']+",
            RegexOptions.Compiled), "$1***"),
        // Authorization: Bearer … /  "authorization": "Basic …"
        (new Regex(@"(?i)(authorization\W{0,4})(bearer|basic|token)\s+\S+", RegexOptions.Compiled), "$1$2 ***"),
        // key = value in a config line a tool printed back
        // (also the JSON form "_authToken": "…", with the quote between the key and the colon)
        (new Regex(@"(?i)\b(_authToken|_password|api[_-]?key|access[_-]?token|client[_-]?secret)\b""?\s*[:=]\s*""?[^\s""]+",
            RegexOptions.Compiled), "$1=***"),
        // Any user's profile folder in the forms the literal replace below cannot see: doubled backslashes
        // (Python's repr of a path, as in every pip "Access is denied: 'C:\\Users\\someone\\..'"), an 8.3
        // short name ("C:\Users\USER~1"), another account's profile.
        (new Regex(@"(?i)\b[a-z]:(\\{1,2}|/)Users\1(?!Public\b|Default\b|All Users\b)[^\\/\s""'`:*?<>|]+",
            RegexOptions.Compiled), "%USERPROFILE%"),
        // a bare GitHub, npm or GitLab token, recognisable by its own prefix wherever it appears
        (new Regex(@"\b(gh[pousr]_|github_pat_|npm_|glpat-)[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled), "$1***"),
        // "Bearer …" echoed without the header name in front of it
        (new Regex(@"(?i)\b(bearer)\s+(?!\*\*\*)[A-Za-z0-9._~+/\-]{16,}=*", RegexOptions.Compiled), "$1 ***"),
    ];

    internal static string Redact(string message)
    {
        if (ProfilePath.Length > 0)
        {
            // Only a whole profile folder: "C:\Users\test" must not turn "C:\Users\testuser\x" into "%USERPROFILE%user\x".
            message = ProfilePathPattern.Replace(message, "%USERPROFILE%");
        }

        foreach (var (pattern, replacement) in SecretPatterns)
            message = pattern.Replace(message, replacement);

        return message;
    }

    /// <summary>The last line written, kept so identical lines that follow are counted instead of repeated.</summary>
    private string? _pendingBody;
    private int _pendingRepeats;
    
    /// <summary>
    /// Redacts and queues one line, shaped like <c>18:42:11 I inst winget:Firefox 128.0.3&gt;129.0.1</c>.
    /// Deliberately terse, because the file only ever grows: the date is in the file name, milliseconds never
    /// answered a question this log is asked, and the level is one letter. That saves roughly twenty
    /// characters per line.
    /// </summary>
    private void Write(char level, string? category, string message)
    {
        var body = category is { Length: > 0 } ? $"{level} {category} {Redact(message)}" : $"{level} {Redact(message)}";

        lock (_lock)
        {
            // A tool stuck in a retry loop writes the same line hundreds of times, and each one costs the
            // same disk as a useful one. Identical consecutive lines are counted instead of repeated, and
            // the count is written out as soon as anything else has something to say.
            if (body == _pendingBody)
            {
                _pendingRepeats++;
                return;
            }

            FlushRepeats();
            _pendingBody = body;
            Append(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} {body}"));
        }
    }

    /// <summary>Writes the "and again, N times" line for whatever was last repeated. Caller holds the lock.</summary>
    private void FlushRepeats()
    {
        if (_pendingRepeats == 0)
            return;

        var repeats = _pendingRepeats;
        _pendingRepeats = 0;
        Append(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} I ×{repeats + 1} (same as above)"));
    }

    /// <summary>
    /// The most this ever blocks a caller: app exit (OnExit, an UnhandledException handler) calls this on
    /// the thread that is about to end the process, and a disk that has gone away or a genuinely stuck
    /// write must not turn that into a hang. A queued line the consumer had not gotten to yet in that case
    /// is the one thing this cannot save; everything up to the point where it stops answering already is.
    /// </summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Test seam only: when set, THIS instance's consumer waits on it before touching the file for each
    /// line, so a test can hold the consumer where a stuck disk would and prove <see cref="Flush"/> still
    /// returns within <see cref="FlushTimeout"/>. Per instance, not static: a static gate would stall
    /// other LoggingService instances running in the same test process. Never set in production.
    /// </summary>
    internal ManualResetEventSlim? TestWriteGate;

    /// <summary>
    /// Closes out a pending repeat count, then waits, bounded by <see cref="FlushTimeout"/>, for every line
    /// queued before this call (including that repeat line) to be written and flushed to disk. Safe to
    /// call twice. File mode writes on the background consumer (see <see cref="ConsumeAsync"/>), so a
    /// caller on the way out (app exit) needs the wait to be sure nothing queued is left stranded, and the
    /// bound so it can never hang the process's exit on a disk that will not answer.
    /// </summary>
    public void Flush()
    {
        TaskCompletionSource barrier;

        lock (_lock)
        {
            FlushRepeats();
            _pendingBody = null;
        }

        if (_queue is null)
            return;

        // The barrier's own enqueue shares the bound: behind a stalled writer the queue can be full, and
        // an unbounded wait for room here would hang the exit path this is called from. Enqueued outside
        // the lock so other producers keep dropping (not blocking) meanwhile.
        barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using (var timeout = new CancellationTokenSource(FlushTimeout))
        {
            try
            {
                _queue.Writer.WriteAsync(new WriteJob { Barrier = barrier }, timeout.Token).AsTask().Wait();
            }
            catch (AggregateException)
            {
                return;
            }
        }

        var remaining = FlushTimeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
            return;

        // Waited on outside the lock: the consumer never takes _lock itself, but there is no reason to
        // hold it for the duration of a disk flush other producers might be waiting to enqueue behind.
        // Wait(TimeSpan), not GetAwaiter().GetResult(): a timeout returns false instead of throwing, and
        // simply lets Flush return, the queued line(s) still behind it land whenever the consumer
        // eventually catches up (or never do, if the process really is about to die).
        barrier.Task.Wait(remaining);
    }

    private void Append(string line)
    {
        if (_sink is not null)
        {
            try
            {
                _sink(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // logging must never crash the app
            }

            return;
        }

        EnqueueLine(line);
    }

    private void EnqueueLine(string line) => EnqueueJob(new WriteJob { Line = line });

    /// <summary>Lines dropped because the queue was full; written as one note once there is room again.
    /// Guarded by <see cref="_lock"/> (every line enqueue runs under it).</summary>
    private int _droppedLines;

    /// <summary>
    /// Never throws and never blocks. The queue is only full when the writer is stalled (a disk that
    /// stopped answering) or under a flood far beyond normal volume; waiting for room there would freeze
    /// the UI thread or a crash handler for as long as the disk stays stuck, so the line is dropped and
    /// counted instead, and the count is written as soon as a later line fits.
    /// </summary>
    private void EnqueueJob(WriteJob job)
    {
        if (_droppedLines > 0)
        {
            if (!_queue!.Writer.TryWrite(new WriteJob { Line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss} W log: {_droppedLines} lines dropped, the log writer was stalled") }))
            {
                _droppedLines++;
                return;
            }

            _droppedLines = 0;
        }

        if (!_queue!.Writer.TryWrite(job))
            _droppedLines++;
    }

    /// <summary>
    /// The single consumer for <see cref="_queue"/>: every disk write, rotation check and archive runs
    /// here, off the calling thread. Lines that arrive together are drained and written as one batch, the
    /// file opened once and flushed once (<c>WaitToReadAsync</c> waits for one item, then <c>TryRead</c>
    /// drains whatever else is already available). The writer is closed once the batch is drained rather
    /// than held open: a quiet period between bursts must not keep the file locked against another
    /// process (the SHA256SUMS verifier, a support person opening it) or, in tests, against a temp
    /// directory that wants to be torn down right after a <see cref="Flush"/>.
    /// </summary>
    private async Task ConsumeAsync()
    {
        try
        {
            while (await _queue!.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var job))
                {
                    if (job.Barrier is { } barrier)
                    {
                        // Closed, not just flushed, before signalling: a caller returning from Flush may
                        // delete or move the log folder at once, and a handle still open for the rest of
                        // this batch would make that fail.
                        try
                        {
                            FlushWriter();
                            CloseOpenWriter();
                        }
                        finally
                        {
                            barrier.TrySetResult();
                        }

                        continue;
                    }

                    try
                    {
                        WriteLineToDisk(job.Line!);
                    }
                    catch (Exception)
                    {
                        // One bad line must not end the consumer: with nobody draining, the bounded
                        // queue fills and every later log call blocks forever under _lock.
                    }
                }

                FlushWriter();
                CloseOpenWriter();
            }
        }
        catch (Exception)
        {
            // There is no caller left to report to on this thread; logging must never crash the app, and
            // that includes its own background writer.
        }
    }

    private const string ArchiveDirectoryName = "archive";

    /// <summary>
    /// Runs only on the consumer thread. Two independent triggers roll a file into the archive: the
    /// calendar day changed since the app last wrote (a stale file from before was left behind, swept at
    /// most once per day, so this stays cheap on the hot path), or today's own file has grown past
    /// <see cref="MaxLogFileBytes"/> regardless of the day. Byte count is tracked in memory
    /// (<see cref="_openFileBytes"/>) rather than re-stat'd on every line: it starts from the file's length
    /// when the writer is opened and grows with every line written.
    /// </summary>
    private void WriteLineToDisk(string line)
    {
        try
        {
            // Test seam (see TestWriteGate's own remarks); never armed outside a test.
            TestWriteGate?.Wait();

            var today = DateTime.Now.Date;
            if (_lastArchiveSweep != today)
            {
                _lastArchiveSweep = today;
                CloseOpenWriter();
                SweepStaleLogs(today);
            }

            var path = LogFilePath;
            if (_openWriter is null || !string.Equals(_openPath, path, StringComparison.Ordinal))
                OpenWriterFor(path);

            // A failed rotation (another program holds the log open without delete sharing) is retried after
            // a pause, not on every following line: each attempt zips the whole 5 MB file again.
            if (_openFileBytes >= MaxLogFileBytes && DateTime.UtcNow >= _nextRotationAttempt)
            {
                CloseOpenWriter();
                if (!ArchiveAndDelete(path))
                    _nextRotationAttempt = DateTime.UtcNow + RotationRetryDelay;
                OpenWriterFor(path);
            }

            _openWriter!.WriteLine(line);
            _openFileBytes += Utf8NoBom.GetByteCount(line) + Utf8NoBom.GetByteCount(Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging must never crash the app
        }
    }

    /// <summary>
    /// Opens the writer for <paramref name="path"/>, closing whatever was open before (a
    /// different day, or the file just rotated out from under it). <see cref="FileShare.ReadWrite"/>
    /// rather than just Read: a held-open handle must not stop another LoggingService instance (or the
    /// same file opened again after a rotation elsewhere) from writing. <c>AutoFlush = false</c>: flushing
    /// is the consumer's job, once per drained batch (<see cref="FlushWriter"/>), not the framework's on
    /// every write.
    /// </summary>
    private void OpenWriterFor(string path)
    {
        _openWriter = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Utf8NoBom)
        {
            AutoFlush = false,
        };
        _openPath = path;
        _openFileBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>Never throws: a flush that fails on dispose (disk full, volume gone) still drops the
    /// writer, so the next line reopens instead of hitting a disposed one.</summary>
    private void CloseOpenWriter()
    {
        var writer = _openWriter;
        _openWriter = null;
        _openPath = null;
        try
        {
            writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // logging must never crash the app
        }
    }

    /// <summary>Never throws; a failed flush closes the writer (see <see cref="CloseOpenWriter"/>).</summary>
    private void FlushWriter()
    {
        try
        {
            _openWriter?.Flush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CloseOpenWriter();
        }
    }

    private void SweepStaleLogs(DateTime today)
    {
        var todayName = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        string[] files;
        try
        {
            files = Directory.GetFiles(_logsDirectory, "*.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in files)
        {
            // Today's own file is the live one, never archive out from under the very write it is about
            // to receive.
            if (!string.Equals(Path.GetFileNameWithoutExtension(file), todayName, StringComparison.Ordinal))
                ArchiveAndDelete(file);
        }
    }

    /// <summary>
    /// Zips one log file into <c>logs/archive/</c> and removes the plain-text original. Zip, not any
    /// stronger format, because it opens natively from Windows Explorer with no extra tool, the whole
    /// point of archiving instead of just deleting is that an old log can still be read if it is needed.
    /// </summary>
    private static bool ArchiveAndDelete(string logPath)
    {
        try
        {
            // Derived from the file's own directory rather than AppPaths directly, so a caller pointed at
            // a test's temp directory (SweepStaleLogs, WriteLineToDisk) archives into THAT directory's own
            // "archive" subfolder instead of the real app data location.
            var archiveDir = Path.Combine(Path.GetDirectoryName(logPath)!, ArchiveDirectoryName);
            Directory.CreateDirectory(archiveDir);

            var baseName = Path.GetFileNameWithoutExtension(logPath);
            var zipPath = Path.Combine(archiveDir, $"{baseName}.zip");

            // A file that hit the size cap more than once in the same day must not overwrite the
            // archive from its earlier rollover.
            var suffix = 1;
            while (File.Exists(zipPath))
                zipPath = Path.Combine(archiveDir, $"{baseName}_{suffix++}.zip");

            // Built under a temporary name and moved into place only once the archive is complete: a log
            // another process still holds open fails the entry copy AFTER the zip file was created, and
            // that must not leave an empty archive under the real name.
            var temporaryPath = zipPath + ".tmp";
            try
            {
                using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(logPath, Path.GetFileName(logPath), CompressionLevel.SmallestSize);

                File.Move(temporaryPath, zipPath);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }

            try
            {
                File.Delete(logPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original stays, so the archive just made would be a duplicate of the next attempt's.
                TryDelete(zipPath);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the source file is left in place and simply tried again later,
            // never worth losing the log entry, or the app itself, over a failed archive.
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
