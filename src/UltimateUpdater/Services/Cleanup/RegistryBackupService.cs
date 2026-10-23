using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The mandatory backup before any registry cleanup write: every root key about to be touched is exported,
/// in memory, into ONE dated file before a single value is deleted. Fails CLOSED, if even one export fails,
/// nothing is deleted this run; a registry writer earns no benefit of the doubt the rest of this app's
/// cleanup does not need.
/// </summary>
public static class RegistryBackupService
{
    private const string FilePrefix = "ultimate updater registry backup ";
    private const string RegHeader = "Windows Registry Editor Version 5.00";

    // Win32 registry value type ids (winnt.h REG_*), for reading and writing the RAW type + bytes rather
    // than trust .NET's own widened RegistryValueKind (see AppendValueLine's doc comment).
    private const uint RegSzType = 1;
    private const uint RegExpandSzType = 2;
    private const uint RegBinaryType = 3;
    private const uint RegDwordType = 4;
    private const uint RegMultiSzType = 7;
    private const uint RegLinkType = 6;
    private const uint RegQwordType = 11;

    private const int ErrorSuccess = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;
    private const uint RegOptionOpenLink = 0x00000008;
    private const int KeyRead = 0x20019; // STANDARD_RIGHTS_READ | KEY_QUERY_VALUE | KEY_ENUMERATE_SUB_KEYS | KEY_NOTIFY
    private const int KeyReadWrite = 0x2001F; // KEY_READ | KEY_WRITE, the access a writable OpenSubKey asks for
    private const int KeyNameInformationClass = 3; // NtQueryKey's KEY_INFORMATION_CLASS.KeyNameInformation

    // A same-user registry link cycle must never recurse forever; this is generous for any
    // real key tree while still being a hard, provable bound.
    private const int MaxKeyDepth = 64;

    // A value that keeps growing between the size query and the read retries with the
    // freshly reported size; bounded so a value that never stops growing cannot spin here indefinitely.
    private const int MaxSizeRetries = 5;

    /// <summary>The backup file plus the roots that actually went into it. Only a finding under one of
    /// those roots is covered by the backup: a root that was a link or did not exist at backup time is not,
    /// even if it is an ordinary key again by the time the delete runs.</summary>
    internal sealed record RegistryBackupResult(string Path, IReadOnlyList<string> ExportedRoots);

    /// <summary>The path-only form of <see cref="BackupRootsBeforeWriteAsync"/>.</summary>
    public static async Task<string?> BackupBeforeWriteAsync(
        IReadOnlyList<string> rootKeys, string backupFolder, int retentionCount, LoggingService logging,
        CancellationToken cancellationToken) =>
        (await BackupRootsBeforeWriteAsync(rootKeys, backupFolder, retentionCount, logging, cancellationToken))?.Path;

    /// <summary>
    /// Exports every root key into one backup file under <paramref name="backupFolder"/>, then prunes old
    /// backups beyond <paramref name="retentionCount"/>. Returns the backup file path with the roots it
    /// holds, or null when there was nothing to back up (none of the keys exist) OR when a key that DOES
    /// exist could not be exported, the caller must not delete anything in the latter case, and there is
    /// nothing to delete in the former either way.
    /// </summary>
    internal static async Task<RegistryBackupResult?> BackupRootsBeforeWriteAsync(
        IReadOnlyList<string> rootKeys, string backupFolder, int retentionCount, LoggingService logging,
        CancellationToken cancellationToken)
    {
        if (rootKeys.Count == 0)
            return null;

        // The folder text crosses the pipe from unelevated settings the user can edit freely; without this
        // check an edited (or tampered) settings.json could point an ELEVATED process at an arbitrary path,
        // e.g. a system directory the user could not otherwise write to. Keep the elevated write inside
        // the same boundary the user already fully owns.
        if (!IsWithinUserProfile(backupFolder))
        {
            logging.Error($"Registry cleanup: refusing to back up to '{backupFolder}', outside the user profile.");
            return null;
        }

        // Checked BEFORE anything is created: creating the folder first would let a junction already
        // sitting in the profile get FOLLOWED by CreateDirectory before this check ever ran. The text check
        // above proves what the path SAYS, not where it LEADS, a junction planted anywhere along it, by
        // the unelevated user who owns the profile outright, sends this elevated write wherever its target
        // points.
        if (CleanupSafety.AncestryLeadsThroughLink(backupFolder))
        {
            logging.Error($"Registry cleanup: refusing to back up to '{backupFolder}', it leads through a link.");
            return null;
        }

        try
        {
            Directory.CreateDirectory(backupFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logging.Error($"Registry cleanup: could not create the backup folder '{backupFolder}'.", ex);
            return null;
        }

        // Re-checked right after creation: CreateDirectory itself resolves through any link planted between
        // the check above and this call, and an attacker could equally have swapped the leaf immediately
        // after it was created. This still cannot close the window across the registry reads below, the
        // final check right before the write does that.
        if (CleanupSafety.AncestryLeadsThroughLink(backupFolder))
        {
            logging.Error($"Registry cleanup: refusing to back up to '{backupFolder}', it leads through a link.");
            return null;
        }

        var sections = new List<string>();
        var exportedRoots = new List<string>();
        long exportedChars = 0;

        foreach (var rootKey in rootKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked BEFORE exporting: an export that comes back empty must never be read as "the key
            // does not exist", it could just as easily be a key that vanished between this check and the
            // read. Knowing the answer up front lets a real failure fail closed.
            if (!RegistryCleanupScanner.KeyExists(rootKey))
            {
                logging.Info($"Registry cleanup: '{rootKey}' does not exist; nothing to back up there.");
                continue;
            }

            // No temp file: an export written by reg.exe into %TEMP% (a folder the SAME unprivileged user
            // fully owns) would need a handle-plus-owner check to trust back. Reading the key's values
            // directly through the .NET registry API leaves no intermediate file for anyone to swap. Built in the exact same REGEDIT5 shape `reg export` writes, so the merged
            // backup stays a plain, importable .reg file.
            try
            {
                var section = ExportKeyTreeTextOrNull(rootKey, logging, cancellationToken);
                if (section is null)
                {
                    // A link (or a key gone since the check): nothing was read, so nothing under it may be
                    // deleted either.
                    logging.Warn($"Registry cleanup: '{rootKey}' could not be opened without following a link; skipping it and everything under it.");
                    continue;
                }

                // The ceiling is for the whole backup file, not per root: many roots together can add up too.
                exportedChars += section.Length;
                EnsureExportFits(exportedChars, rootKey, MaxExportBytes);

                sections.Add(section);
                exportedRoots.Add(rootKey);
            }
            // Any exception fails this run closed like every other export error (an OutOfMemoryException or
            // an ArgumentOutOfRangeException out of StringBuilder must not escape). OperationCanceledException
            // is the one deliberate exception: a cancelled run is not a backup failure to log and swallow,
            // it must keep propagating.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logging.Error($"Registry cleanup: backing up '{rootKey}' failed; nothing will be deleted this run.", ex);
                return null;
            }
        }

        if (sections.Count == 0)
            return null;

        // Re-checked one last time right before the write that actually lands on disk, closing the window
        // between the first check and this write.
        if (CleanupSafety.AncestryLeadsThroughLink(backupFolder))
        {
            logging.Error($"Registry cleanup: refusing to back up to '{backupFolder}', it leads through a link.");
            return null;
        }

        var content = MergeSections(sections);
        var encoding = new UnicodeEncoding(false, true);

        // Two categories backed up in the same minute would otherwise build the same date+time name.
        // FileMode.CreateNew refuses to open an existing file at all (never silently overwrites a sibling
        // category's backup), and a collision retries under a counted suffix rather than failing the run.
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var backupPath = BuildBackupPath(backupFolder, collisionSuffix: attempt > 1 ? attempt : null);
            try
            {
                // Opened with DELETE access so a file that landed in the wrong place is removed through THIS
                // handle, never by re-resolving the path, which a same-user link swap could aim elsewhere.
                using var handle = CleanupNative.CreateFileW(
                    backupPath, GenericWrite | DeleteAccess, 0, IntPtr.Zero, CreateNew, FileAttributeNormal, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ErrorFileExists or ErrorAlreadyExists)
                        continue;

                    throw new IOException($"Creating the backup file failed with Win32 error {error}.");
                }

                // The link check above proves what the path SAID a moment ago, not
                // where THIS handle actually landed - a junction planted in the gap between that check and
                // this CreateNew would still have let it succeed, just somewhere else entirely. Verifying
                // the open handle's own resolved path is the only way to know where the bytes about to be
                // written really are, so this is checked before a single byte of the export goes into it.
                if (!WrittenFileLandedInExpectedFolder(handle, backupFolder))
                {
                    logging.Error($"Registry cleanup: the backup file did not land inside '{backupFolder}' as expected; deleting it and refusing to delete anything this run.");
                    SetDeleteOnClose(handle, true);
                    return null;
                }

                // Marked delete-pending before the first byte and cleared only once everything is flushed:
                // a failed or cancelled write (a full disk, say) then removes its own partial file when the
                // handle closes, instead of leaving a truncated .reg that pruning would count as a backup.
                if (!SetDeleteOnClose(handle, true))
                    throw new IOException($"Marking the new backup file for cleanup failed with Win32 error {Marshal.GetLastWin32Error()}.");

                await using var file = new FileStream(handle, FileAccess.Write);
                await using var writer = new StreamWriter(file, encoding);
                await writer.WriteAsync(content);
                await writer.FlushAsync(cancellationToken);

                if (!SetDeleteOnClose(handle, false))
                    throw new IOException($"Keeping the finished backup file failed with Win32 error {Marshal.GetLastWin32Error()}.");

                PruneOldBackups(backupFolder, retentionCount, logging);
                return new RegistryBackupResult(backupPath, exportedRoots);
            }
            // A real name collision never reaches here, it is answered by CreateNew's own error above. Any
            // IOException at this point is a failed write whose partial file is already gone with the handle,
            // so retrying under the next suffix would only repeat the failure.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logging.Error($"Registry cleanup: could not write the backup file '{backupPath}'.", ex);
                return null;
            }
        }

        logging.Error("Registry cleanup: could not find a free backup filename after 20 attempts.");
        return null;
    }

    /// <summary>
    /// A.reg file's header line must appear exactly once; every export after the first carries its own
    /// copy, which would otherwise corrupt the merged file. Pure and unit-tested on its own.
    /// </summary>
    internal static string MergeSections(IReadOnlyList<string> sections)
    {
        const string header = "Windows Registry Editor Version 5.00";
        var merged = new StringBuilder(header).AppendLine().AppendLine();

        foreach (var section in sections)
        {
            // Only the section's own LEADING copy of the header is stripped, a value's DATA could
            // legitimately contain this exact text, and a blanket Replace over the whole section would
            // silently destroy that too, corrupting the one artifact a registry cleanup can be undone from.
            var trimmed = section.TrimStart();
            if (trimmed.StartsWith(header, StringComparison.Ordinal))
                trimmed = trimmed[header.Length..].TrimStart();

            merged.AppendLine(trimmed);
        }

        return merged.ToString();
    }

    /// <summary>
    /// Builds the same text a `reg export &lt;rootKey&gt;` would have written, straight from the live
    /// registry, depth first: the root key's header and values, then each subkey's section the same way,
    /// so a category's whole subtree ends up in one section. Read-only, no process, no file.
    /// <paramref name="logging"/> is optional so tests can call this without a real
    /// <see cref="LoggingService"/>; a skipped registry link is then simply not reported.
    /// </summary>
    internal static string ExportKeyTreeText(
        string rootKey, LoggingService? logging = null, CancellationToken cancellationToken = default) =>
        ExportKeyTreeTextOrNull(rootKey, logging, cancellationToken)
        ?? new StringBuilder(RegHeader).Append("\r\n").Append("\r\n").ToString();

    /// <summary>The export text, or null when the root could not be opened: it is gone, or a link on the
    /// way (an HKCU root is walked segment by segment, so a link in the MIDDLE of the path counts, not only
    /// one on the last segment).</summary>
    internal static string? ExportKeyTreeTextOrNull(
        string rootKey, LoggingService? logging = null, CancellationToken cancellationToken = default,
        long maxExportBytes = MaxExportBytes)
    {
        var sb = new StringBuilder(RegHeader).Append("\r\n").Append("\r\n");

        var separator = rootKey.IndexOf('\\');
        if (separator <= 0)
            return null;

        var hive = rootKey[..separator];
        var subPath = rootKey[(separator + 1)..];

        RegistryKey? key;
        if (hive == "HKCU")
        {
            key = OpenHkcuLinkSafe(subPath, writable: false, logging, throwOnOtherFailure: true);
        }
        else if (hive == "HKLM")
        {
            key = OpenKeyForExport(
                Registry.LocalMachine.Handle, subPath, rootKey, depth: 0, logging,
                followRootLink: FollowsRootLink(rootKey));
        }
        else
        {
            return null;
        }

        if (key is null)
            return null;

        using (key)
            ExportKeyRecursive(key, rootKey, sb, depth: 0, logging, cancellationToken, maxExportBytes);

        return sb.ToString();
    }

    /// <summary>
    /// Whether a real registry link at the very root of a backup is followed to its target. Only HKLM roots
    /// are: a 64-bit Windows install redirects some of them (WOW6432Node) through such a link. An HKCU
    /// root sits in a hive the unprivileged user fully owns, so a link there is a planted redirect and the
    /// elevated run must not read, or later delete, what it points at.
    /// </summary>
    internal static bool FollowsRootLink(string rootKey) =>
        rootKey.StartsWith(@"HKLM\", StringComparison.Ordinal);

    /// <summary>The largest single registry value the backup reads. The key tree sits in a hive the
    /// unprivileged user writes, and the elevated worker allocates whatever size a value reports, so an
    /// unbounded read lets that user exhaust the worker's memory.</summary>
    internal const int MaxValueBytes = 16 * 1024 * 1024;

    /// <summary>The largest export text. Past it the backup fails and, because the caller deletes only
    /// after a backup exists, nothing under the root is touched.</summary>
    internal const long MaxExportBytes = 256L * 1024 * 1024;

    internal static void EnsureValueFits(long size, string valueName, string? keyPath)
    {
        if (size > MaxValueBytes)
            throw new IOException($"registry value '{valueName}' under '{keyPath}' is {size} bytes, over the {MaxValueBytes} byte backup limit; refusing to back it up.");
    }

    internal static void EnsureExportFits(long total, string keyPath, long maxExportBytes)
    {
        if (total > maxExportBytes)
            throw new IOException($"the registry export under '{keyPath}' grew past the {maxExportBytes} byte backup limit; refusing to back it up.");
    }

    private static void EnsureNameIsWritable(string name, string kind, string keyPath)
    {
        foreach (var c in name)
        {
            if (c < ' ')
                throw new IOException($"a registry {kind} name under '{keyPath}' contained the control character U+{(int)c:X4}; refusing to back it up.");
        }
    }

    private static void ExportKeyRecursive(
        RegistryKey key, string keyPath, StringBuilder sb, int depth, LoggingService? logging, CancellationToken cancellationToken,
        long maxExportBytes)
    {
        // Per key, not per root: one root can hold a very large tree, and Stop must not wait for all of it.
        // Cancelling abandons the export, so the caller deletes nothing.
        cancellationToken.ThrowIfCancellationRequested();

        sb.Append('[').Append(ToFullHiveName(keyPath)).Append(']').Append("\r\n");

        foreach (var valueName in key.GetValueNames())
        {
            AppendValueLine(sb, key, valueName, keyPath);
            EnsureExportFits(sb.Length, keyPath, maxExportBytes);
        }

        sb.Append("\r\n");

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            // The depth cap, unchanged in spirit: a same-user link cycle must never recurse without
            // end (an uncatchable StackOverflow would kill the elevated worker outright). Checked before
            // the child is even opened.
            if (depth + 1 > MaxKeyDepth)
                throw new IOException($"registry key nesting under '{keyPath}' exceeded the depth cap of {MaxKeyDepth}.");

            EnsureNameIsWritable(subKeyName, "key", keyPath);

            var childPath = $@"{keyPath}\{subKeyName}";

            // Opened relative to THIS key's own already-open handle, one path segment at a time.
            // REG_OPTION_OPEN_LINK only protects the FINAL component of the path it is given, so
            // re-resolving the full path from the root would follow any INTERMEDIATE ancestor that is a
            // link. One segment at a time gives every level its own OPEN_LINK-protected open and link check.
            using var child = OpenKeyForExport(key.Handle, subKeyName, childPath, depth + 1, logging);
            if (child is null)
                continue; // vanished, or a confirmed real link (already logged by OpenKeyForExport)

            ExportKeyRecursive(child, childPath, sb, depth + 1, logging, cancellationToken, maxExportBytes);
        }
    }

    /// <summary>
    /// Reads each value by its RAW Win32 type and bytes (<see cref="TryReadRawValueWithRetry"/>), never
    /// through <see cref="RegistryKey.GetValueKind"/>/<see cref="RegistryKey.GetValue(string)"/>: .NET's
    /// wrapper WIDENS an oversized REG_DWORD (a same-user value of 5-8 bytes comes back as a QWORD, longer
    /// as a raw <c>byte[]</c> that <c>Convert.ToInt64</c> cannot cast, which would abort every registry
    /// cleanup on such a value). Reading raw type and bytes keeps the emitted tag matching the value's
    /// ACTUAL byte length.
    /// </summary>
    private static void AppendValueLine(StringBuilder sb, RegistryKey key, string valueName, string keyPath)
    {
        // RegQueryValueExW's lpValueName is marshaled as a null-terminated string; a
        // name with an EMBEDDED NUL would silently query a DIFFERENT, shorter value than the one just
        // enumerated (whatever text precedes the first NUL). There is no way to marshal around that here,
        // so a name shaped like this fails the whole backup closed rather than silently reading, or
        // silently omitting, the wrong value.
        if (valueName.Contains('\0'))
            throw new IOException($"a registry value name under '{keyPath}' contained an embedded NUL character; refusing to read it.");

        // A line break or any other control character in a name cannot be written into a REGEDIT5 line:
        // the file would come out malformed yet still count as a good backup, and the deletion that follows
        // trusts that backup. Failing the whole backup is the only closed outcome, since the backup is per
        // root key and the caller deletes everything once it returns a path.
        EnsureNameIsWritable(valueName, "value", keyPath);

        // Read BEFORE the name is written to the section, and ERROR_FILE_NOT_FOUND (the
        // value vanished between GetValueNames and this read) writes NOTHING for it, not even the name.
        // The old `hex(0):` placeholder for ANY read failure meant a restore could recreate a value that
        // never existed, or paper over a real read error as if the value were simply empty.
        if (!TryReadRawValueWithRetry(key.Handle, valueName, out var regType, out var raw, keyPath))
            return;

        sb.Append(valueName.Length == 0 ? "@" : $"\"{EscapeRegText(valueName)}\"").Append('=');

        switch (regType)
        {
            case RegSzType:
                AppendStringValue(sb, raw);
                break;

            case RegExpandSzType:
                sb.Append("hex(2):").Append(ToHexBytes(raw));
                break;

            case RegMultiSzType:
                sb.Append("hex(7):").Append(ToHexBytes(raw));
                break;

            case RegBinaryType:
                sb.Append("hex:").Append(ToHexBytes(raw));
                break;

            case RegDwordType:
                // The shorthand is only correct for an exact 4-byte value; anything else (5-8 bytes would be
                // cut to 4, anything longer is uncastable) is written as the exact raw
                // bytes under the same type tag instead of guessing at a 32-bit value that was never there.
                if (raw.Length == 4)
                    sb.Append("dword:").Append(BitConverter.ToUInt32(raw).ToString("x8"));
                else
                    sb.Append("hex(4):").Append(ToHexBytes(raw));
                break;

            case RegQwordType:
                // hex(b) is the REG_QWORD type tag regardless of the raw byte count; a real 8-byte QWORD
                // and an oversized/undersized one are both written the exact same way, from the raw bytes.
                sb.Append("hex(b):").Append(ToHexBytes(raw));
                break;

            default:
                // Unknown/rare types (REG_DWORD_BIG_ENDIAN, REG_LINK, the three resource-list kinds, or
                // anything future) always round-trip as their own raw type tag and raw bytes; nothing here
                // ever loses data by pretending to know a shape it does not.
                sb.Append("hex(").Append(regType.ToString("x")).Append("):").Append(ToHexBytes(raw));
                break;
        }

        sb.Append("\r\n");
    }

    /// <summary>
    /// A REG_SZ read raw as UTF-16LE, with exactly one trailing NUL terminator trimmed if the registry
    /// stored one (`reg export` never writes it back). <paramref name="raw"/> only gets a quoted string
    /// when re-encoding the decoded text reproduces the EXACT same bytes, which catches every way a
    /// REG_SZ can fail to be clean UTF-16 (odd byte length, lone surrogate half: <see cref="Encoding.Unicode"/>
    /// silently replaces those). An embedded NUL (it would cut the string dead on import) and a raw CR/LF
    /// (it would corrupt REGEDIT5's single-line quoted format) are checked separately, since both
    /// round-trip fine as .NET text. Any of the three sends the value to <c>hex(1):</c> (REG_SZ's own
    /// type tag) with the exact raw bytes.
    /// </summary>
    private static void AppendStringValue(StringBuilder sb, byte[] raw)
    {
        var text = Encoding.Unicode.GetString(raw);
        var trailingNulStripped = false;
        if (text.EndsWith('\0'))
        {
            text = text[..^1];
            trailingNulStripped = true;
        }

        if (!RoundTripsCleanly(text, raw, trailingNulStripped) || text.Contains('\0') || text.Contains('\r') || text.Contains('\n'))
        {
            sb.Append("hex(1):").Append(ToHexBytes(raw));
            return;
        }

        sb.Append('"').Append(EscapeRegText(text)).Append('"');
    }

    /// <summary>
    /// True only when re-encoding <paramref name="text"/> reproduces <paramref name="raw"/> byte for byte
    /// (minus the one trailing NUL already stripped out of <paramref name="text"/>, if there was one). Any
    /// mismatch, including one caused by an odd raw length or a lone surrogate half that
    /// <see cref="Encoding.Unicode"/> silently replaced on decode, means the quoted form would not be the
    /// same data, so this returns false rather than risk it.
    /// </summary>
    private static bool RoundTripsCleanly(string text, byte[] raw, bool trailingNulStripped)
    {
        try
        {
            var reEncoded = Encoding.Unicode.GetBytes(text);
            var expected = trailingNulStripped ? raw[..^2] : raw;
            return reEncoded.AsSpan().SequenceEqual(expected);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>
    /// True only for a genuine registry symlink. A value named <c>SymbolicLinkValue</c> of type REG_LINK is
    /// not proof by itself: the raw <c>RegSetValueExW</c> call can plant it on an ordinary key (proven in
    /// <c>RegistryBackupExportFormatTests</c>), a decoy that would make a real key silently vanish from
    /// the backup, with deletion then proceeding over data that was never saved.
    /// <see cref="HasSymbolicLinkMarker"/> is only the cheap PREFILTER (a real link always carries the
    /// marker); when the marker is present, <see cref="IsRealLink"/> decides via the key's own
    /// resolution behavior, which a same-user attacker cannot forge.
    /// </summary>
    private static bool HasSymbolicLinkMarker(SafeRegistryHandle handle) =>
        TryReadRawValueWithRetry(handle, "SymbolicLinkValue", out var regType, out _) && regType == RegLinkType;

    /// <summary>Test seam: a live registry link cannot be created from a test without NtCreateKey, so a test
    /// plants the marker value on an ordinary scratch key and decides here whether it counts as a real link.
    /// Per async flow, so a parallel test never sees another test's detector.</summary>
    internal static readonly AsyncLocal<Func<SafeRegistryHandle, string, SafeRegistryHandle, bool>?> RealLinkDetectorForTest = new();

    /// <summary>
    /// True only when <paramref name="linkHandle"/> (opened WITH <c>REG_OPTION_OPEN_LINK</c>) actually
    /// resolves to a DIFFERENT underlying key than the very same name opened WITHOUT that option: a genuine
    /// link's plain open follows it to its target elsewhere, while a decoy's plain open lands right back on
    /// itself. Each open's own NT key name (<see cref="TryGetNtKeyName"/>, via <c>NtQueryKey</c>) is what
    /// gets compared, the one thing a same-user attacker cannot forge, unlike a value's name or type.
    /// </summary>
    private static bool IsRealLink(SafeRegistryHandle parentHandle, string subKeyName, SafeRegistryHandle linkHandle)
    {
        if (RealLinkDetectorForTest.Value is { } fake)
            return fake(parentHandle, subKeyName, linkHandle);

        var linkName = TryGetNtKeyName(linkHandle);
        if (linkName is null)
            return true; // could not verify; treat it as a real link rather than risk following a decoy

        var result = RegOpenKeyExW(parentHandle, subKeyName, ulOptions: 0, KeyRead, out var plainHandle);
        using (plainHandle)
        {
            if (result != ErrorSuccess || plainHandle.IsInvalid)
                return true;

            var plainName = TryGetNtKeyName(plainHandle);
            return plainName is null || !string.Equals(linkName, plainName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The key's own NT path as <c>NtQueryKey(KeyNameInformation)</c> reports it, straight off the handle;
    /// two calls, the same Win32/NT idiom as <see cref="TryReadRawValueWithRetry"/>: the first just to
    /// learn the required size, the second to actually read into a buffer sized for it. Returns null on any
    /// failure, callers treat that as "could not verify" rather than crash.
    /// </summary>
    private static string? TryGetNtKeyName(SafeRegistryHandle handle)
    {
        if (handle.IsInvalid)
            return null;

        NtQueryKey(handle, KeyNameInformationClass, [], 0, out var neededBytes);
        if (neededBytes <= 4)
            return null;

        var buffer = new byte[neededBytes];
        var status = NtQueryKey(handle, KeyNameInformationClass, buffer, buffer.Length, out _);
        if (status < 0)
            return null;

        var nameLength = BitConverter.ToInt32(buffer, 0);
        if (nameLength <= 0 || nameLength > buffer.Length - 4)
            return null;

        return Encoding.Unicode.GetString(buffer, 4, nameLength);
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryKey(
        SafeRegistryHandle keyHandle, int keyInformationClass, byte[] keyInformation, int length, out int resultLength);

    /// <summary>
    /// Reads one value's RAW Win32 type and bytes straight off <paramref name="handle"/>, bypassing every
    /// interpretation <see cref="RegistryKey.GetValue(string)"/> applies. Two calls, the Win32 idiom: the
    /// first with a null buffer to learn size and type, the second to read.
    ///
    /// A value that GROWS between the calls comes back as ERROR_MORE_DATA on the second; this retries with
    /// the newly reported size, bounded by <see cref="MaxSizeRetries"/> so a forever-growing value cannot
    /// spin. ERROR_FILE_NOT_FOUND on EITHER call (the value vanished since GetValueNames/the marker check)
    /// returns false, the only case treated as "nothing to write"; every other error throws and fails the
    /// whole backup closed.
    /// </summary>
    private static bool TryReadRawValueWithRetry(
        SafeRegistryHandle handle, string valueName, out uint regType, out byte[] raw, string? keyPath = null) =>
        TryReadRawValueWithRetryCore(MaxSizeRetries, valueName, _ => OneRawReadAttempt(handle, valueName, keyPath), out regType, out raw);

    /// <summary>Outcome of one size-query-then-read attempt, the unit <see cref="TryReadRawValueWithRetryCore"/> retries.</summary>
    internal enum RawReadOutcome { Success, MoreData, NotFound }

    internal readonly record struct RawReadAttempt(RawReadOutcome Outcome, uint RegType, byte[] Raw);

    /// <summary>
    /// One real size-query-then-read pair against the live registry, the same two Win32 calls
    /// <see cref="TryReadRawValueWithRetry"/> always used. Extracted so <see cref="TryReadRawValueWithRetryCore"/>
    /// can be driven by a fake attempt delegate in tests, deterministically, with no real thread racing a
    /// real registry key.
    /// </summary>
    private static RawReadAttempt OneRawReadAttempt(SafeRegistryHandle handle, string valueName, string? keyPath)
    {
        uint size = 0;
        var sizeResult = RegQueryValueExSize(handle, valueName, IntPtr.Zero, out var regType, IntPtr.Zero, ref size);
        if (sizeResult == ErrorFileNotFound)
            return new RawReadAttempt(RawReadOutcome.NotFound, regType, []);
        if (sizeResult != ErrorSuccess)
            throw new IOException($"could not query registry value '{valueName}' (Win32 error {sizeResult}).");

        if (size == 0)
            return new RawReadAttempt(RawReadOutcome.Success, regType, []);

        // Before the allocation: the size is whatever a user-writable value reports.
        EnsureValueFits(size, valueName, keyPath);

        var buffer = new byte[size];
        var dataResult = RegQueryValueExData(handle, valueName, IntPtr.Zero, out regType, buffer, ref size);
        if (dataResult == ErrorSuccess)
        {
            if (size != buffer.Length)
                Array.Resize(ref buffer, (int)size);
            return new RawReadAttempt(RawReadOutcome.Success, regType, buffer);
        }

        if (dataResult == ErrorFileNotFound)
            return new RawReadAttempt(RawReadOutcome.NotFound, regType, []);
        if (dataResult == ErrorMoreData)
            return new RawReadAttempt(RawReadOutcome.MoreData, regType, []); // grew again between the size query and the read; retry with the fresh size

        throw new IOException($"could not read registry value '{valueName}' (Win32 error {dataResult}).");
    }

    /// <summary>
    /// The bounded retry loop itself, independent of where an attempt's result comes from. Production calls
    /// this through <see cref="OneRawReadAttempt"/> against the live registry; tests call it with a fake
    /// <paramref name="attempt"/> delegate that reports "more data" (or "more data" then success) on demand,
    /// so the "kept growing" exception and the successful-after-a-regrow path are both deterministic, no
    /// background thread racing a real key needed.
    /// </summary>
    internal static bool TryReadRawValueWithRetryCore(
        int maxRetries, string valueName, Func<int, RawReadAttempt> attempt, out uint regType, out byte[] raw)
    {
        regType = 0;
        raw = [];

        for (var i = 0; i < maxRetries; i++)
        {
            var result = attempt(i);
            if (result.Outcome == RawReadOutcome.NotFound)
                return false;
            if (result.Outcome == RawReadOutcome.Success)
            {
                regType = result.RegType;
                raw = result.Raw;
                return true;
            }
            // MoreData: grew again between the size query and the read; retry with the fresh size
        }

        throw new IOException($"registry value '{valueName}' kept growing across {maxRetries} read attempts; refusing to guess at its size.");
    }

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExSize(
        SafeRegistryHandle hKey, string lpValueName, IntPtr lpReserved, out uint lpType, IntPtr lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegQueryValueExData(
        SafeRegistryHandle hKey, string lpValueName, IntPtr lpReserved, out uint lpType, byte[] lpData, ref uint lpcbData);

    private static string ToHexBytes(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(bytes.Count * 3 - 1);
        for (var i = 0; i < bytes.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(bytes[i].ToString("x2"));
        }

        return sb.ToString();
    }

    /// <summary>Backslash and quote are the only characters a REGEDIT5 string needs escaped.</summary>
    private static string EscapeRegText(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary><c>reg export</c> always writes the long hive name, never the <c>HKLM</c>/<c>HKCU</c>
    /// shorthand this app's own key paths use internally.</summary>
    private static string ToFullHiveName(string keyPath)
    {
        var separator = keyPath.IndexOf('\\');
        var (hive, rest) = separator <= 0 ? (keyPath, string.Empty) : (keyPath[..separator], keyPath[separator..]);

        var full = hive switch
        {
            "HKLM" => "HKEY_LOCAL_MACHINE",
            "HKCU" => "HKEY_CURRENT_USER",
            _ => hive,
        };

        return full + rest;
    }

    /// <summary>
    /// Opens <paramref name="subKey"/> relative to <paramref name="hKey"/> with <c>REG_OPTION_OPEN_LINK</c>
    /// (an ordinary, non-link key opened this way behaves like a normal open). Used for BOTH the backup
    /// root (<paramref name="hKey"/> a hive handle, <paramref name="subKey"/> the rest of the root's path)
    /// and every recursive child (the parent's open handle, a single path segment).
    ///
    /// Only ERROR_FILE_NOT_FOUND (the key vanished between the existence check and this open, the same
    /// harmless race as in <see cref="TryReadRawValueWithRetry"/>) means "nothing to add". Every OTHER
    /// failure, access-denied above all (a same-user HKCU key can carry a deny-read ACE that blocks even
    /// this elevated process), throws, and the callers fail the WHOLE backup closed: otherwise a root key
    /// with real, un-backed-up data would pass as a header-only "success" that deletion proceeds over.
    ///
    /// A key carrying the <c>SymbolicLinkValue</c> marker (<see cref="HasSymbolicLinkMarker"/>) is
    /// verified through <see cref="IsRealLink"/> before being trusted as a link; at the backup ROOT
    /// (<paramref name="depth"/> 0) even an unverified marker fails the whole backup closed, since a
    /// silent skip there is the "success but empty" shape this guards against.
    /// </summary>
    internal static RegistryKey? OpenKeyForExport(
        SafeRegistryHandle hKey, string subKey, string fullPathForLog, int depth, LoggingService? logging,
        bool followRootLink = false)
    {
        var result = RegOpenKeyExW(hKey, subKey, RegOptionOpenLink, KeyRead, out var linkHandle);
        if (result == ErrorFileNotFound)
        {
            linkHandle.Dispose();
            return null;
        }

        if (result != ErrorSuccess || linkHandle.IsInvalid)
        {
            linkHandle.Dispose();
            throw new IOException($"could not open registry key '{fullPathForLog}' (Win32 error {result}).");
        }

        if (HasSymbolicLinkMarker(linkHandle))
        {
            if (IsRealLink(hKey, subKey, linkHandle))
            {
                if (depth == 0 && !followRootLink)
                {
                    // Throwing (not returning null) is what fails the whole backup closed: a null here would
                    // come back as a header-only section that still counts as a successful backup.
                    linkHandle.Dispose();
                    throw new IOException($"registry key '{fullPathForLog}' is a link; refusing to back up or clean what it points at.");
                }

                if (depth == 0)
                {
                    // A caller-NAMED backup root (unlike a link found while recursing) is not attacker-controlled
                    // content: it is the fixed set of roots RegistryCleanupScanner already scans and deletes
                    // through, and that path opens keys the ORDINARY way, transparently following exactly this
                    // kind of real link (WOW6432Node\...\App Paths is one on 64-bit Windows, proven by
                    // RegistryBackupExportFormatTests). Refusing to look past a verified real link at the root
                    // would make every entry under it un-backup-able and therefore un-deletable. So it is
                    // followed (opened WITHOUT REG_OPTION_OPEN_LINK) and the backup covers the SAME data the
                    // scan and delete operate on. Below the root a verified real link is skipped, never
                    // followed: the caller never asked for that path, only for what recursion found beneath
                    // something it trusts.
                    linkHandle.Dispose();
                    var plainResult = RegOpenKeyExW(hKey, subKey, ulOptions: 0, KeyRead, out var plainHandle);
                    if (plainResult != ErrorSuccess || plainHandle.IsInvalid)
                    {
                        plainHandle.Dispose();
                        throw new IOException($"could not open registry key '{fullPathForLog}' (Win32 error {plainResult}).");
                    }

                    logging?.Warn($"Registry cleanup: '{fullPathForLog}' is a registry link at the backup root; following it to its target rather than refusing the whole backup.");
                    return RegistryKey.FromHandle(plainHandle);
                }

                logging?.Warn($"Registry cleanup: '{fullPathForLog}' is a registry link; skipping it rather than following it.");
                linkHandle.Dispose();
                return null;
            }

            // A decoy: the marker value is present but the key does not actually resolve anywhere else.
            // Falls through and is backed up as the ordinary key it actually is, root or not.
        }

        return RegistryKey.FromHandle(linkHandle);
    }

    /// <summary>
    /// Opens an HKCU path one segment at a time, each with its own REG_OPTION_OPEN_LINK open and link
    /// check, and returns null when any segment (the last included) is a real link, does not exist or
    /// cannot be opened. Plain <c>OpenSubKey</c> follows a link at every component, and a user-writable HKCU
    /// key can be turned into a link to HKLM by an unprivileged process, which the elevated cleanup would
    /// then read and delete through.
    /// </summary>
    internal static RegistryKey? OpenHkcuLinkSafe(
        string subPath, bool writable, LoggingService? logging = null, bool throwOnOtherFailure = false)
    {
        // Only the per-user hive mounts its classes through a link that is followed (see
        // IsClassesMountPosition), so the exception is enabled here and nowhere else.
        return OpenBelowLinkSafe(
            Registry.CurrentUser.Handle, subPath, writable, logging, "HKCU", allowClassesMount: true, throwOnOtherFailure: throwOnOtherFailure);
    }

    /// <summary>
    /// The per-segment walk behind <see cref="OpenHkcuLinkSafe"/>, from any already open key: each segment is
    /// opened with REG_OPTION_OPEN_LINK and a real link anywhere (the last segment included) ends the walk
    /// with null. <paramref name="start"/> itself is not examined; the caller chose it. Write rights go only
    /// to the last segment.
    /// </summary>
    internal static RegistryKey? OpenBelowLinkSafe(
        SafeRegistryHandle start, string relativePath, bool writable, LoggingService? logging,
        string displayRoot = "HKCU", bool allowClassesMount = false, bool throwOnOtherFailure = false)
    {
        var segments = relativePath.Split('\\');
        if (segments.Any(string.IsNullOrEmpty))
            return null;

        var parent = start;
        SafeRegistryHandle? ownedParent = null;

        for (var i = 0; i < segments.Length; i++)
        {
            var rights = i == segments.Length - 1 && writable ? KeyReadWrite : KeyRead;
            var result = RegOpenKeyExW(parent, segments[i], RegOptionOpenLink, rights, out var handle);
            if (result != ErrorSuccess || handle.IsInvalid)
            {
                handle.Dispose();
                ownedParent?.Dispose();

                // A backup must fail closed on a key it cannot read (a deny ACE on a root with real
                // data), only a key that is simply not there may pass as "nothing to back up".
                if (throwOnOtherFailure && result != ErrorFileNotFound)
                    throw new IOException($"could not open registry key '{displayRoot}\\{string.Join('\\', segments[..(i + 1)])}' (Win32 error {result}).");

                return null;
            }

            if (HasSymbolicLinkMarker(handle) && IsRealLink(parent, segments[i], handle))
            {
                var mounted = allowClassesMount && IsClassesMountPosition(segments, i)
                    ? OpenOwnClassesMount(parent, segments[i], rights)
                    : null;
                if (mounted is null)
                {
                    logging?.Warn($"Registry cleanup: '{displayRoot}\\{string.Join('\\', segments[..(i + 1)])}' is a registry link; not following it.");
                    handle.Dispose();
                    ownedParent?.Dispose();
                    return null;
                }

                handle.Dispose();
                handle = mounted;
            }

            ownedParent?.Dispose();
            ownedParent = handle;
            parent = handle;
        }

        return RegistryKey.FromHandle(ownedParent!);
    }

    /// <summary>True only for the second segment of <c>Software\Classes</c>: the one place Windows mounts
    /// the per-user classes hive. A link named "Classes" anywhere else is an ordinary link.</summary>
    internal static bool IsClassesMountPosition(string[] segments, int index) =>
        index == 1 && segments[0].Equals("Software", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows itself mounts the per-user classes hive at<c>HKCU\Software\Classes</c> through a real
    /// registry link (to <c>\Registry\User\&lt;own SID&gt;_Classes</c>), so refusing every link would make
    /// every cleanup root under it unreachable. That one link, recognised by the target it resolves to
    /// (the user's own classes hive, which the same user owns anyway), is followed; any other link is not.
    /// Returns the opened target, or null when <paramref name="name"/> is not that mount.
    /// </summary>
    private static SafeRegistryHandle? OpenOwnClassesMount(SafeRegistryHandle parent, string name, int rights)
    {
        if (!name.Equals("Classes", StringComparison.OrdinalIgnoreCase))
            return null;

        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            return null;

        var result = RegOpenKeyExW(parent, name, ulOptions: 0, rights, out var plain);
        if (result == ErrorSuccess && !plain.IsInvalid
            && string.Equals(TryGetNtKeyName(plain), $@"\REGISTRY\USER\{sid}_Classes", StringComparison.OrdinalIgnoreCase))
        {
            return plain;
        }

        plain.Dispose();
        return null;
    }

    /// <summary>How <see cref="DeleteKeyTreeLinkSafe"/> ended; anything but Deleted means the finding does
    /// not count.</summary>
    internal enum KeyTreeDeleteStatus
    {
        Deleted,

        /// <summary>The top key is not there (any more).</summary>
        NotFound,

        /// <summary>A real link inside the tree, or the tree is deeper than the cap: nothing was deleted.</summary>
        RefusedLink,

        /// <summary>A key of the tree could not be opened for delete: nothing was deleted.</summary>
        OpenFailed,

        /// <summary>A key refused the delete (swapped, or gained a child since it was opened); the
        /// NTSTATUS says which. Keys below it may already be gone.</summary>
        DeleteFailed,
    }

    private const int KeyEnumerateSubKeys = 0x0008;
    private const int KeyQueryValue = 0x0001;
    private const int DeleteRight = 0x00010000;
    private const int StatusKeyDeleted = unchecked((int)0xC000017C);

    /// <summary>
    /// Deletes <paramref name="name"/> below <paramref name="parent"/> and everything under it without ever
    /// resolving a name twice. Phase 1 opens the whole subtree key by key, each relative to its parent's
    /// handle with REG_OPTION_OPEN_LINK, and keeps every handle; a real link, a failed open or a tree
    /// deeper than the cap refuses the WHOLE finding with nothing deleted (a bottom-up delete would
    /// otherwise remove siblings before a link further along is met). Phase 2 deletes bottom-up through those
    /// same handles with NtDeleteKey: a key swapped since phase 1 reports STATUS_KEY_DELETED and a key that
    /// gained a child since reports STATUS_CANNOT_DELETE, so neither takes anything it was not shown.
    /// <paramref name="betweenPhases"/> is the test seam that runs after the handles are held.
    /// </summary>
    internal static KeyTreeDeleteStatus DeleteKeyTreeLinkSafe(
        SafeRegistryHandle parent, string name, out int ntStatus, Action? betweenPhases = null)
    {
        ntStatus = 0;
        var held = new List<RegistryKey>();
        var postOrder = new List<RegistryKey>();
        try
        {
            var opened = OpenTreeForDelete(parent, name, 0, held, postOrder);
            if (opened != KeyTreeDeleteStatus.Deleted)
                return opened;

            betweenPhases?.Invoke();

            foreach (var key in postOrder)
            {
                // NtDeleteKey on a key somebody else already deleted can still report success, which would
                // count a delete this run did not make; a query on the dead handle fails with KEY_DELETED.
                try
                {
                    _ = key.SubKeyCount;
                }
                catch (IOException)
                {
                    ntStatus = StatusKeyDeleted;
                    return KeyTreeDeleteStatus.DeleteFailed;
                }

                ntStatus = NtDeleteKey(key.Handle);
                if (ntStatus != 0)
                    return KeyTreeDeleteStatus.DeleteFailed;
            }

            return KeyTreeDeleteStatus.Deleted;
        }
        finally
        {
            foreach (var key in held)
                key.Dispose();
        }
    }

    /// <summary>Phase 1. Returns Deleted to mean "opened without a problem".</summary>
    private static KeyTreeDeleteStatus OpenTreeForDelete(
        SafeRegistryHandle parent, string name, int depth, List<RegistryKey> held, List<RegistryKey> postOrder)
    {
        if (depth > MaxKeyDepth)
            return KeyTreeDeleteStatus.RefusedLink;

        var result = RegOpenKeyExW(
            parent, name, RegOptionOpenLink, DeleteRight | KeyEnumerateSubKeys | KeyQueryValue, out var handle);
        if (result == ErrorFileNotFound)
        {
            handle.Dispose();
            // A child that went away since its parent was listed is simply not there to delete.
            return depth == 0 ? KeyTreeDeleteStatus.NotFound : KeyTreeDeleteStatus.Deleted;
        }

        if (result != ErrorSuccess || handle.IsInvalid)
        {
            handle.Dispose();
            return KeyTreeDeleteStatus.OpenFailed;
        }

        var key = RegistryKey.FromHandle(handle);
        held.Add(key);
        if (HasSymbolicLinkMarker(handle) && IsRealLink(parent, name, handle))
            return KeyTreeDeleteStatus.RefusedLink;

        string[] children;
        try
        {
            children = key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return KeyTreeDeleteStatus.OpenFailed;
        }

        foreach (var child in children)
        {
            var childStatus = OpenTreeForDelete(handle, child, depth + 1, held, postOrder);
            if (childStatus != KeyTreeDeleteStatus.Deleted)
                return childStatus;
        }

        postOrder.Add(key);
        return KeyTreeDeleteStatus.Deleted;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtDeleteKey(SafeRegistryHandle keyHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegOpenKeyExW(
        SafeRegistryHandle hKey, string lpSubKey, uint ulOptions, int samDesired, out SafeRegistryHandle phkResult);

    /// <summary>
    /// Same-day rerun gets a time suffix rather than overwriting the first backup of the day.
    /// <paramref name="collisionSuffix"/> is the actual guarantee against two categories landing on the
    /// same name in the same minute, the caller supplies it only after
    /// <see cref="FileMode.CreateNew"/> has already proven the un-suffixed name is taken.
    /// </summary>
    internal static string BuildBackupPath(string backupFolder, DateTime? now = null, int? collisionSuffix = null)
    {
        var moment = now ?? DateTime.Now;
        var date = moment.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

        if (collisionSuffix is { } n)
        {
            var time = moment.ToString("HH-mm", CultureInfo.InvariantCulture);
            return Path.Combine(backupFolder, $"{FilePrefix}{date} {time} ({n}).reg");
        }

        var basePath = Path.Combine(backupFolder, $"{FilePrefix}{date}.reg");
        if (!File.Exists(basePath))
            return basePath;

        var timeOnly = moment.ToString("HH-mm", CultureInfo.InvariantCulture);
        return Path.Combine(backupFolder, $"{FilePrefix}{date} {timeOnly}.reg");
    }

    internal static void PruneOldBackups(string backupFolder, int retentionCount, LoggingService logging)
    {
        if (retentionCount <= 0)
            return;

        try
        {
            var backups = Directory.EnumerateFiles(backupFolder, $"{FilePrefix}*.reg")
                .Select(path => new FileInfo(path))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            foreach (var stale in backups.Skip(retentionCount))
            {
                if (!TryDeleteInsideFolder(stale.FullName, backupFolder))
                    logging.Warn($"Registry cleanup: left old backup '{stale.FullName}' in place, it no longer resolves inside the backup folder.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"Registry cleanup: could not prune old backups in '{backupFolder}': {ex.Message}");
        }
    }

    /// <summary>
    /// True only when <paramref name="handle"/>'s own resolved path sits directly inside
    /// <paramref name="expectedFolder"/>. <c>GetFinalPathNameByHandleW</c> asks Windows where the handle
    /// ACTUALLY points, following any reparse point on the way there the same way the write that produced
    /// the handle already did, so this proves the same thing a text-based path check never can: not what
    /// the caller believes it wrote to, but where the bytes really are. Pure translation of that one Win32
    /// answer plus a normalized string compare, so a real junction in a temp tree can prove this directly.
    /// </summary>
    internal static bool WrittenFileLandedInExpectedFolder(SafeFileHandle handle, string expectedFolder)
    {
        var actualPath = TryGetFinalPath(handle);
        if (actualPath is null)
            return false;

        var actualFolder = Path.GetDirectoryName(actualPath);
        if (actualFolder is null)
            return false;

        string expectedFull;
        try
        {
            expectedFull = Path.GetFullPath(expectedFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return TrimTrailingSeparator(actualFolder).Equals(TrimTrailingSeparator(expectedFull), StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// <c>GetFinalPathNameByHandleW</c> defaults to the <c>\\?\</c>-prefixed DOS form; stripped here so the
    /// result compares directly against <see cref="Path.GetFullPath"/>'s own output, which never carries it.
    /// </summary>
    internal static string? TryGetFinalPath(SafeFileHandle handle)
    {
        var path = VerifiedDelete.FinalPathOf(handle);
        return path is not null && path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>Pure so the boundary itself is unit-testable without touching the real filesystem.</summary>
    internal static bool IsWithinUserProfile(string backupFolder, string? userProfile = null)
    {
        if (string.IsNullOrWhiteSpace(backupFolder))
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(backupFolder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var profile = Path.GetFullPath(userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return full.Equals(profile, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deletes one old backup by HANDLE, never by path. This runs elevated in a folder the unelevated user
    /// owns: between the enumeration and a path-based delete, that folder could be swapped for a junction
    /// (or an object-manager link) and the elevated delete would land on any file the link names. The file
    /// is opened without following a reparse point at its leaf, its resolved path is checked to still sit
    /// directly inside <paramref name="expectedFolder"/>, and only then is that same handle marked for
    /// deletion. Anything else leaves the file alone.
    /// </summary>
    internal static bool TryDeleteInsideFolder(string path, string expectedFolder)
    {
        using var handle = CleanupNative.CreateFileW(
            path, DeleteAccess | FileReadAttributes, FileShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return false;

        if (!WrittenFileLandedInExpectedFolder(handle, expectedFolder))
            return false;

        return SetDeleteOnClose(handle, true);
    }

    /// <summary>Sets or clears the delete-pending state of the file behind <paramref name="handle"/>,
    /// which needs DELETE access. Clearing it before the handle closes keeps the file.</summary>
    private static bool SetDeleteOnClose(SafeFileHandle handle, bool delete)
    {
        var disposition = new FileDispositionInfo { DeleteFile = delete ? (byte)1 : (byte)0 };
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile, int fileInformationClass, ref FileDispositionInfo lpFileInformation, uint dwBufferSize);
}
