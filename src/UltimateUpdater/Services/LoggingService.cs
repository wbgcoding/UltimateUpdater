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
    /// A log file is rolled into the archive once it reaches this size, independent of the day rollover, so
    /// one very talkative day cannot grow into the gigabytes.
    /// </summary>
    private const long MaxLogFileBytes = 5 * 1024 * 1024;

    private readonly object _lock = new();
    private readonly Action<string>? _sink;
    private readonly string _logsDirectory;

    /// <summary>
    /// The disk writes (open/rotate/append/flush) run on one dedicated consumer drained from this bounded
    /// channel, so a caller on any thread never blocks on file I/O or a rotation. Null in sink mode: the
    /// elevated worker never touches disk. When full, <see cref="EnqueueJob"/> drops and counts a line
    /// rather than wait.
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

    /// <summary>Guards the once-per-day sweep for a stale log file: checked on every write, but the directory
    /// scan only runs when the date changes. Only touched from the consumer thread.</summary>
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
    /// <paramref name="logsDirectory"/> lets tests exercise <see cref="ReadAround"/> and the write path
    /// against a temp directory. Production code leaves it null and gets the real AppPaths location.
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
    /// into belongs to the unelevated user, who can replace it with a reparse point aimed somewhere only
    /// administrators may write, which would turn each log line into an arbitrary create-or-append as admin.
    /// The worker keeps its lines in memory and hands them to the app over its pipe. Deliberately does NOT
    /// read <see cref="AppPaths"/>: that would run its static initializer (data directory resolution, folder
    /// creation) as admin, following a same-user-writable pointer file. <see cref="_logsDirectory"/> stays
    /// empty; <see cref="Append"/> returns through the sink before <see cref="LogFilePath"/> is built.
    /// </summary>
    public LoggingService(Action<string> sink)
    {
        _sink = sink;
        _logsDirectory = string.Empty;
    }

    /// <summary>Resolved per write, not once, so an app left running overnight rolls to the new day's file.</summary>
    private string LogFilePath => Path.Combine(_logsDirectory, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd}.log"));

    public void Info(string message) => Write('I', null, message);

    public void Warn(string message) => Write('W', null, message);

    public void Error(string message, Exception? exception = null) =>
        Write('E', null, exception is null ? message : $"{message}: {exception}");

    /// <summary>
    /// One recorded event, tagged with a short category so a class of them can be picked out of a day's file:
    /// <c>app</c>, <c>scan</c>, <c>inst</c>, <c>dl</c>, <c>clean</c>, <c>self</c>.
    /// </summary>
    public void Event(string category, string message) => Write('I', category, message);

    /// <summary>The same, for an event that went wrong.</summary>
    public void EventFailed(string category, string message) => Write('W', category, message);

    /// <summary>A read-around result is capped: it is a fallback for a missing stored detail, not a substitute
    /// for one.</summary>
    private const int MaxReadAroundLines = 200;

    /// <summary>
    /// Lines from the day's log file(s) whose own timestamp falls within <paramref name="window"/> of
    /// <paramref name="when"/>, from today's file and the previous day's when the window crosses midnight.
    /// <paramref name="category"/>, when given, keeps only lines written through
    /// <see cref="Event"/>/<see cref="EventFailed"/> with that exact category. Read-only; a missing or
    /// unreadable file is skipped.
    /// </summary>
    public IReadOnlyList<string> ReadAround(DateTimeOffset when, TimeSpan window, string? category = null)
    {
        // Sink mode has no real log directory (_logsDirectory stays empty on purpose). An empty string is a
        // relative path, so without this guard a caller would read around the current directory.
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

    /// <summary>The line's leading "HH:mm:ss" against the file's own date: the file name carries the date,
    /// the line never repeats it.</summary>
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

    /// <summary>"HH:mm:ss LEVEL CATEGORY message...": the category is the third space-separated token, when
    /// the line carries one at all (a plain Info/Warn/Error line has none).</summary>
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
        // Same guard as ReadAround: sink mode has no log directory, and an empty _logsDirectory is a relative
        // path that would make the sweep delete files relative to the current directory.
        if (_sink is not null)
            return;

        // A non-positive value puts the cutoff in the future and deletes every log. settings.json is
        // hand-editable.
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
            // Housekeeping is never worth failing a startup over.
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
                // Best effort; retried next start.
            }
        }
    }

    /// <summary>
    /// The user's profile directory, and what it is replaced with. Package managers print absolute paths into
    /// their output, and the log is a file the user may hand to someone else, so the account name is replaced
    /// while the rest of the path stays.
    /// </summary>
    private static readonly string ProfilePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    /// <summary>
    /// The same directory the way npm, pip and node print it, with forward slashes, which the replacement
    /// above would not match.
    /// </summary>
    private static readonly string ProfilePathWithForwardSlashes = ProfilePath.Replace('\\', '/');

    private static readonly Regex ProfilePathPattern = new(
        $@"(?i)(?:{Regex.Escape(ProfilePath)}|{Regex.Escape(ProfilePathWithForwardSlashes)})(?![^\\/\s""'])",
        RegexOptions.Compiled);

    /// <summary>
    /// The credential shapes that turn up in a package manager's own output: a private registry token from
    /// an npm config, a git remote carrying a password, an authorization header echoed back by a failed
    /// tool. The log may be handed to someone else, so the shape is kept and the value is not.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] SecretPatterns =
    [
        // scheme://user:secret@host (the secret may itself contain "@": it runs to the last "@" before the host)
        (new Regex(@"(?<=://)[^\s/:@]+:[^\s/]+(?=@)", RegexOptions.Compiled), "***:***"),
        // ?token=... &api_key=... &access_token=...
        (new Regex(@"(?i)([?&](?:api[_-]?key|access[_-]?token|auth|token|secret|password|pwd)=)[^&\s""']+",
            RegexOptions.Compiled), "$1***"),
        // Authorization: Bearer ... / "authorization": "Basic ..."
        (new Regex(@"(?i)(authorization\W{0,4})(bearer|basic|token)\s+\S+", RegexOptions.Compiled), "$1$2 ***"),
        // key = value in a config line a tool printed back (also the JSON form "_authToken": "...")
        (new Regex(@"(?i)\b(_authToken|_password|api[_-]?key|access[_-]?token|client[_-]?secret)\b""?\s*[:=]\s*""?[^\s""]+",
            RegexOptions.Compiled), "$1=***"),
        // Any user's profile folder in forms the literal replace below cannot see: doubled backslashes (Python's
        // repr of a path), an 8.3 short name ("C:\Users\USER~1"), another account's profile.
        (new Regex(@"(?i)\b[a-z]:(\\{1,2}|/)Users\1(?!Public\b|Default\b|All Users\b)[^\\/\s""'`:*?<>|]+",
            RegexOptions.Compiled), "%USERPROFILE%"),
        // A bare GitHub, npm or GitLab token, recognisable by its own prefix wherever it appears.
        (new Regex(@"\b(gh[pousr]_|github_pat_|npm_|glpat-)[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled), "$1***"),
        // "Bearer ..." echoed without the header name in front of it.
        (new Regex(@"(?i)\b(bearer)\s+(?!\*\*\*)[A-Za-z0-9._~+/\-]{16,}=*", RegexOptions.Compiled), "$1 ***"),
    ];

    internal static string Redact(string message)
    {
        if (ProfilePath.Length > 0)
        {
            // Only a whole profile folder: "C:\Users\user" must not turn "C:\Users\username\x" into "%USERPROFILE%name\x".
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
    /// Terse on purpose: the date is in the file name and the level is one letter.
    /// </summary>
    private void Write(char level, string? category, string message)
    {
        var body = category is { Length: > 0 } ? $"{level} {category} {Redact(message)}" : $"{level} {Redact(message)}";

        lock (_lock)
        {
            // Identical consecutive lines (a tool stuck in a retry loop) are counted instead of repeated; the count
            // is written out as soon as anything else has something to say.
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
    /// The most this ever blocks a caller. App exit calls Flush on the thread about to end the process, and a
    /// vanished disk or stuck write must not turn that into a hang. A line the consumer had not reached yet
    /// in that case is lost.
    /// </summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Test seam only: when set, this instance's consumer waits on it before touching the file for each line,
    /// so a test can hold the consumer like a stuck disk and prove <see cref="Flush"/> still returns within
    /// <see cref="FlushTimeout"/>. Per instance, not static, so other instances in the test process are not
    /// stalled. Never set in production.
    /// </summary>
    internal ManualResetEventSlim? TestWriteGate;

    /// <summary>
    /// Closes out a pending repeat count, then waits, bounded by <see cref="FlushTimeout"/>, for every line
    /// queued before this call to be written and flushed. Safe to call twice. A caller on the way out (app
    /// exit) needs the wait so nothing queued is stranded, and the bound so a disk that will not answer
    /// cannot hang the exit.
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

        // The barrier's own enqueue shares the bound: behind a stalled writer the queue can be full, and an
        // unbounded wait would hang the exit path. Enqueued outside the lock so other producers keep dropping
        // instead of blocking.
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

        // Waited on outside the lock so producers can keep enqueuing. Wait(TimeSpan) returns false on timeout
        // instead of throwing; the queued lines still land whenever the consumer catches up.
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
                // Logging must never crash the app.
            }

            return;
        }

        EnqueueLine(line);
    }

    private void EnqueueLine(string line) => EnqueueJob(new WriteJob { Line = line });

    /// <summary>Lines dropped because the queue was full; written as one note once there is room again.
    /// Guarded by <see cref="_lock"/>.</summary>
    private int _droppedLines;

    /// <summary>
    /// Never throws and never blocks. The queue is only full when the writer is stalled or under a flood, and
    /// waiting would freeze the UI thread or a crash handler for as long as the disk stays stuck, so the line
    /// is dropped and counted instead.
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
    /// The single consumer for <see cref="_queue"/>: every disk write, rotation check and archive runs here,
    /// off the calling thread. Lines that arrive together are drained and written as one batch, with the file
    /// opened and flushed once. The writer is closed once the batch is drained rather than held open, so a
    /// quiet period does not keep the file locked against another process or a test tearing down its temp
    /// directory after a <see cref="Flush"/>.
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
                        // Closed, not just flushed, before signalling: a caller returning from Flush may delete or move the log
                        // folder at once.
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
                        // One bad line must not end the consumer: with nobody draining the queue every later line would drop.
                    }
                }

                FlushWriter();
                CloseOpenWriter();
            }
        }
        catch (Exception)
        {
            // No caller left to report to; logging must never crash the app, including its own background writer.
        }
    }

    private const string ArchiveDirectoryName = "archive";

    /// <summary>
    /// Runs only on the consumer thread. A file is rolled into the archive when the calendar day changed
    /// since the last write (swept at most once per day) or when today's file has grown past
    /// <see cref="MaxLogFileBytes"/>. The byte count is tracked in memory (<see cref="_openFileBytes"/>)
    /// instead of re-stat'd on every line.
    /// </summary>
    private void WriteLineToDisk(string line)
    {
        try
        {
            // Test seam; never armed outside a test.
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

            // A failed rotation (another program holds the log open without delete sharing) is retried after a
            // pause, not on every line: each attempt zips the whole file again.
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
            // Logging must never crash the app.
        }
    }

    /// <summary>
    /// Opens the writer for <paramref name="path"/>, closing whatever was open before. <see cref="FileShare.ReadWrite"/>
    /// so a held-open handle does not stop another instance from writing. <c>AutoFlush = false</c>: flushing is
    /// the consumer's job, once per drained batch (<see cref="FlushWriter"/>).
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

    /// <summary>Never throws: a flush that fails on dispose (disk full, volume gone) still drops the writer,
    /// so the next line reopens instead of hitting a disposed one.</summary>
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
            // Logging must never crash the app.
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
            // Today's own file is the live one: never archive it out from under the write it is about to receive.
            if (!string.Equals(Path.GetFileNameWithoutExtension(file), todayName, StringComparison.Ordinal))
                ArchiveAndDelete(file);
        }
    }

    /// <summary>
    /// Zips one log file into <c>logs/archive/</c> and removes the plain-text original. Zip opens natively
    /// from Windows Explorer, so an old log stays readable without extra tools.
    /// </summary>
    private static bool ArchiveAndDelete(string logPath)
    {
        try
        {
            // Derived from the file's own directory, not AppPaths, so a caller pointed at a test's temp directory
            // archives into that directory's own "archive" subfolder.
            var archiveDir = Path.Combine(Path.GetDirectoryName(logPath)!, ArchiveDirectoryName);
            Directory.CreateDirectory(archiveDir);

            var baseName = Path.GetFileNameWithoutExtension(logPath);
            var zipPath = Path.Combine(archiveDir, $"{baseName}.zip");

            // A file that hit the size cap more than once in a day must not overwrite its earlier archive.
            var suffix = 1;
            while (File.Exists(zipPath))
                zipPath = Path.Combine(archiveDir, $"{baseName}_{suffix++}.zip");

            // Built under a temporary name and moved into place once complete: a log another process holds open
            // fails the entry copy after the zip was created, which must not leave an empty archive under the
            // real name.
            var temporaryPath = zipPath + ".tmp";
            try
            {
                using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
                    archive.CreateEntryFromFile(logPath, Path.GetFileName(logPath), CompressionLevel.SmallestSize);

                File.Move(temporaryPath, zipPath);
            }
            catch
            {
                FileDeletion.TryDelete(temporaryPath);
                throw;
            }

            try
            {
                File.Delete(logPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original stays, so this archive would only duplicate the next attempt's.
                FileDeletion.TryDelete(zipPath);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the source file stays and is tried again later.
            return false;
        }
    }
}
