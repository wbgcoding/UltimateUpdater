using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// Versions pip offers but cannot install on one interpreter (see <see cref="PipInstallFailure"/>), kept so
/// the next scan does not offer the same dead end again. An entry is tied to the exact offered version: once
/// pip offers a different one for that package, the entry is dropped and the row shows again.
/// Written by the unelevated app only; the elevated worker writes no files, so there <see cref="Add"/>
/// does nothing. A damaged or newer-format file reads as empty and never throws.
/// An entry expires after <see cref="Lifetime"/>, so a misread failure cannot hide a version for good.
/// </summary>
internal sealed class PipBlockedStore(
    string path, LoggingService logging, bool? writable = null,
    TimeProvider? time = null, Func<string, bool>? interpreterExists = null)
{
    /// <summary>2 added the per-entry timestamp; version 1 files are read, their entries count as expired.</summary>
    internal const int CurrentVersion = 2;

    internal static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    /// <summary>A few hundred short entries at most; anything larger is not this file.</summary>
    private const long MaxFileBytes = 256 * 1024;

    /// <summary>Small enough that a full file with long interpreter paths stays under <see cref="MaxFileBytes"/>.</summary>
    internal const int MaxEntries = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, bool> _interpreterExists = interpreterExists ?? File.Exists;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly bool _writable = writable ?? !ElevationService.IsElevatedWorkerProcess;

    internal static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "pip-blocked.json");

    internal sealed record Entry(string Interpreter, string Package, string Version, DateTimeOffset? BlockedAt = null);

    private sealed class FileModel
    {
        public int Version { get; set; }

        public List<EntryModel>? Blocked { get; set; }
    }

    private sealed class EntryModel
    {
        public string? Interpreter { get; set; }

        public string? Package { get; set; }

        public string? Version { get; set; }

        public DateTimeOffset? BlockedAt { get; set; }
    }

    /// <summary>PEP 503 normalisation, so "PyMuPDF", "pymupdf" and "py_mu.pdf" are one package.</summary>
    internal static string NormalizeName(string name) =>
        Regex.Replace(name.Trim(), @"[-_.]+", "-").ToLowerInvariant();

    private static bool Same(Entry entry, string interpreter, string package) =>
        string.Equals(entry.Interpreter, interpreter, StringComparison.OrdinalIgnoreCase)
        && NormalizeName(entry.Package) == NormalizeName(package);

    internal IReadOnlyList<Entry> Load()
    {
        lock (_gate)
            return LoadLocked();
    }

    private List<Entry> LoadLocked()
    {
        if (path.Length == 0)
            return [];

        try
        {
            var text = AtomicFile.TryReadAllText(path, MaxFileBytes);
            if (text is null)
            {
                if (File.Exists(path))
                    logging.Info("pip: the blocked-updates file is too large; ignoring it.");
                return [];
            }

            var model = JsonSerializer.Deserialize<FileModel>(text, JsonOptions);
            if (model is null || model.Version < 1 || model.Version > CurrentVersion)
            {
                logging.Info($"pip: the blocked-updates file has an unsupported version ({model?.Version.ToString() ?? "none"}); ignoring it.");
                return [];
            }

            // Version 1 had no timestamp: its entries are expired, like any entry without one.
            var now = _time.GetUtcNow();
            return (model.Blocked ?? [])
                .Where(e => !string.IsNullOrWhiteSpace(e.Interpreter)
                            && !string.IsNullOrWhiteSpace(e.Package)
                            && !string.IsNullOrWhiteSpace(e.Version)
                            && model.Version >= 2
                            && e.BlockedAt is { } at && now - at < Lifetime)
                .TakeLast(MaxEntries)
                .Select(e => new Entry(e.Interpreter!, e.Package!, e.Version!, e.BlockedAt))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logging.Warn($"pip: could not read the blocked-updates file; treating it as empty: {ex.Message}");
            return [];
        }
    }

    private void SaveLocked(List<Entry> entries)
    {
        if (!_writable)
            return;

        try
        {
            var model = new FileModel
            {
                Version = CurrentVersion,
                Blocked = entries.TakeLast(MaxEntries)
                    .Select(e => new EntryModel
                    {
                        Interpreter = e.Interpreter, Package = e.Package, Version = e.Version, BlockedAt = e.BlockedAt,
                    })
                    .ToList(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logging.Warn($"pip: could not save the blocked-updates file: {ex.Message}");
        }
    }

    /// <summary>Remembers that <paramref name="version"/> of <paramref name="package"/> cannot be installed on
    /// <paramref name="interpreter"/>. An older entry of the same package there is replaced.</summary>
    internal void Add(string interpreter, string package, string version)
    {
        if (!_writable)
            return;

        lock (_gate)
        {
            var entries = LoadLocked();
            entries.RemoveAll(e => Same(e, interpreter, package));
            entries.Add(new Entry(interpreter, package, version, _time.GetUtcNow()));

            // Newest last, so a full list drops the oldest.
            if (entries.Count > MaxEntries)
                entries.RemoveRange(0, entries.Count - MaxEntries);
            SaveLocked(entries);
        }
    }

    /// <summary>
    /// Splits one interpreter's outdated list into what to show and what is a known dead end. A package that
    /// now offers a different version than the one remembered is shown and its entry is forgotten. Entries
    /// of packages that are not listed at all stay: an empty or partial outdated list (index unreachable)
    /// must not wipe the memory.
    /// </summary>
    internal (IReadOnlyList<OutdatedPackage> Visible, IReadOnlyList<OutdatedPackage> Hidden) Apply(
        string interpreter, IReadOnlyList<OutdatedPackage> outdated)
    {
        lock (_gate)
        {
            var entries = LoadLocked();

            // An interpreter that was uninstalled can never be offered again; its entries would only pile up.
            var gone = entries.Select(e => e.Interpreter).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(i => !_interpreterExists(i)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = gone.Count > 0 && entries.RemoveAll(e => gone.Contains(e.Interpreter)) > 0;
            if (entries.Count == 0)
            {
                if (changed)
                    SaveLocked(entries);
                return (outdated, []);
            }

            var visible = new List<OutdatedPackage>();
            var hidden = new List<OutdatedPackage>();
            foreach (var package in outdated)
            {
                var entry = entries.FirstOrDefault(e => Same(e, interpreter, package.Id));
                if (entry is null)
                {
                    visible.Add(package);
                }
                else if (string.Equals(entry.Version, package.Available, StringComparison.Ordinal))
                {
                    hidden.Add(package);
                }
                else
                {
                    visible.Add(package);
                    entries.Remove(entry);
                    changed = true;
                }
            }

            if (changed)
                SaveLocked(entries);

            return (visible, hidden);
        }
    }
}
