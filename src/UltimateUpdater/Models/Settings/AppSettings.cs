namespace UltimateUpdater.Models.Settings;

/// <summary>settings.json root. schemaVersion gates future migrations.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public ScanSettings Scan { get; set; } = new();

    /// <summary>Source id → scanned or not. Absent means enabled, so a new source is on by default.</summary>
    public Dictionary<string, bool> Sources { get; set; } = new();

    public NotifySettings Notify { get; set; } = new();
    public InstallSettings Install { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public bool Autostart { get; set; }
    public SelfUpdateSettings SelfUpdate { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public List<string> Excludes { get; set; } = new();

    /// <summary>"Remind me later"; key is the same <c>PackageKey</c>/<c>VersionKey</c> form as
    /// <see cref="Excludes"/>, value is the UTC due date. Mutually exclusive with Excludes for a given key:
    /// setting one clears the other. An expired entry is dropped lazily wherever it is looked up, and swept
    /// on load too (<see cref="UltimateUpdater.Services.SettingsService"/>) so a very long-idle install never resurrects it.</summary>
    public Dictionary<string, DateTimeOffset> Snoozed { get; set; } = new();

    /// <summary>Counts a package's repeated same-cause install failures so a row that can never succeed
    /// (a crashing vendor setup, a version winget has no build for) reads as "needs manual work" instead
    /// of staying red forever. Key is <c>PackageKey</c> (never the version key; the whole point is that
    /// retrying the same package keeps failing the same way, whatever version is on offer this time).
    /// A success removes its entry immediately and completely.</summary>
    public Dictionary<string, PersistentFailureRecord> PersistentFailures { get; set; } = new();

    public LogSettings Logs { get; set; } = new();
    public RegistryCleanupSettings RegistryCleanup { get; set; } = new();

    /// <summary>Cleanup target id → checked or not, from the last time the cleanup dialog was closed. An
    /// id absent here (never saved, or a catalogue target added since) keeps that target's own
    /// DefaultEnabled. Recycle bin and leftover-installs are deliberately never written here, so they can
    /// never come back pre-checked.</summary>
    public Dictionary<string, bool> CleanupSelection { get; set; } = new();

    /// <summary>One-shot marker: the saved choice of the two system repair targets was reset once, when
    /// they changed from default-on to default-off. True from then on (and on a fresh install), so a tick
    /// the user sets later is kept.</summary>
    public bool CleanupRepairDefaultsApplied { get; set; }

    /// <summary>Moves file-target cleanup to the recycle bin instead of deleting outright.
    /// Off by default; deleting is faster and the normal case. A target that needs elevation ignores this
    /// regardless of the value here (see CleanupService.CleanDirectories).</summary>
    public bool CleanupRecycleInsteadOfDelete { get; set; }

    /// <summary>Measures the cleanup areas once at startup, in the background, after the first update scan
    /// and never on battery, so the cleanup window opens with results. On by default; it only reads, it
    /// never deletes and never asks for administrator rights.</summary>
    public bool CleanupPrescanAtStartup { get; set; } = true;

    /// <summary>The recycle bin card only lists (and would delete) items whose own deletion timestamp is
    /// at least this many days old. 0 means every age, which is also the value a freshly unticked filter
    /// sets. Default 30, so a first look at the card does not immediately offer files someone may still
    /// want back.</summary>
    public int CleanupRecycleBinMinAgeDays { get; set; } = 30;

    /// <summary>Right-click "permanently exclude": a detail-list entry the user
    /// explicitly never wants shown again; full path, case-insensitive match, same identity a real scan
    /// re-finds each time (a registry finding's own key path counts too). Applied by unchecking a matching
    /// entry every time <c>SetEntries</c> runs (<c>CleanupViewModel.ApplyPermanentExclusions</c>); this
    /// list changes what starts checked, never what the scan itself reports or measures, so a stale entry
    /// (the thing it pointed at is long gone) is simply never matched again rather than needing cleanup of
    /// its own.</summary>
    public List<string> CleanupPermanentlyExcludedPaths { get; set; } = [];

    /// <summary>How many days may pass since the last cleanup (or since the app was set up, if
    /// none ever ran) before the main window's quiet reminder banner offers one.</summary>
    public int CleanupReminderDays { get; set; } = 90;

    /// <summary>Set by the banner's "never again" choice; the settings window carries the same
    /// switch (inverted) so it can be turned back on.</summary>
    public bool CleanupReminderOff { get; set; }

    /// <summary>
    /// False until either parallelism slider (<see cref="ScanSettings.MaxParallelSources"/>,
    /// <see cref="InstallSettings.MaxParallelInstalls"/>) has been moved by hand. While false, the
    /// once-only hardware auto-tune at startup is free to keep rewriting both values for this machine;
    /// the moment it flips true, nothing ever re-tunes them again, not even a later version. A factory
    /// reset clears it, so the machine can be auto-tuned again.
    /// </summary>
    public bool ParallelismChosenByUser { get; set; }

    /// <summary>
    /// How long an install-history row is kept. Same contract as <see cref="LogSettings.RetentionDays"/>
    /// (housekeeping runs at start-up, so a shorter value takes effect on the next launch); its own field
    /// rather than folded into LogSettings, since the log and the install history are pruned independently
    /// and a settings.json from before this existed must not suddenly start deleting logs by a different
    /// rule.
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 30;
}

public sealed class ScanSettings
{
    public int IntervalMinutes { get; set; } = 1440;
    public bool OnStart { get; set; } = true;
    public bool OnlyOnAc { get; set; }

    /// <summary>An automatic scan skips itself while the active connection is metered (Fixed or
    /// Variable cost); a scan the user starts by hand always runs regardless. Defaults on, since a
    /// metered connection is the one this matters most for protecting by default.</summary>
    public bool RespectMetered { get; set; } = true;

    /// <summary>
    /// Caps how many sources' scans run at once (<see cref="Services.ScanOrchestrator"/>'s own gate,
    /// separate from the per-source lane that already stops one source from scanning itself twice at
    /// once). 1 to 16; the hardware auto-tune at startup picks the initial value for this machine once,
    /// see <see cref="AppSettings.ParallelismChosenByUser"/>. The default here only matters before that
    /// first tune has ever run (or for a settings object built without going through it, as in tests).
    /// </summary>
    public int MaxParallelSources { get; set; } = 8;
}

public sealed class NotifySettings
{
    public bool OnFound { get; set; } = true;
    public bool CriticalOnly { get; set; }
}

public sealed class InstallSettings
{
    public bool PreselectCritical { get; set; } = true;
    public bool CreateRestorePoint { get; set; } = true;

    /// <summary>
    /// How many installs may run at once (winget items among themselves, and different sources against
    /// each other); every other single source still stays one at a time internally. Clamped to 1-5 in
    /// <see cref="SettingsService"/>'s Normalize, and clamped again by the elevated worker itself, which
    /// never trusts a job field blindly.
    /// </summary>
    public int MaxParallelInstalls { get; set; } = 3;
}

/// <summary>theme/language: "system" or an explicit code (a theme name, or one of LocalizationService.SupportedLanguages).</summary>
public sealed class UiSettings
{
    public string Theme { get; set; } = "system";
    public string Language { get; set; } = "system";
    public WindowPlacement? WindowPlacement { get; set; }
    /// <summary>
    /// Off by default: with it on, closing the window leaves the app running invisibly, and the
    /// next launch appears to do nothing at all. Opt in from settings once that is understood.
    /// </summary>
    public bool CloseToTray { get; set; }

    /// <summary>The two overview toggles in the table's filter menu; remembered between runs. Ignored and
    /// snoozed rows stay out of the table unless <see cref="ShowIgnored"/> is on.</summary>
    public bool ShowIgnored { get; set; }
    public bool HideManual { get; set; }

    /// <summary>Per-table column visibility and sort, keyed by table id ("main", "history",
    /// "cleanup"). Column WIDTHS are deliberately not part of this: none of the three tables has a
    /// resize handle, so there is never a dragged width to remember.</summary>
    public Dictionary<string, TableLayoutState> Tables { get; set; } = new();
}

/// <summary>One table's remembered column chooser state. HiddenColumns holds ids from that
/// table's own static column list (declared in its view model); an id no longer in that list (a
/// removed column, or a typo from hand-editing settings.json) is simply ignored wherever this is
/// read, never an error.</summary>
public sealed class TableLayoutState
{
    public List<string> HiddenColumns { get; set; } = new();

    /// <summary>
    /// A column whose CODE default is hidden (main table's "source"/"publisher") records an
    /// explicit opt-IN here instead of in <see cref="HiddenColumns"/>. An id simply absent from both
    /// lists always keeps its own code default: for a normal, default-visible column that means shown,
    /// unchanged from the code default; for a default-hidden one it means hidden, which is what makes a
    /// settings.json saved before a default-hidden column existed behave exactly like a fresh install
    /// for it, rather than reading its total silence about the column as "shown".
    /// </summary>
    public List<string> ShownColumns { get; set; } = new();

    /// <summary>
    /// Main table only: columns the user ticked in the chooser, kept visible even while no row has a value
    /// for them (a column that is empty and not listed here hides itself). Stored as is, never rebuilt from
    /// the code defaults.
    /// </summary>
    public List<string> PinnedColumns { get; set; } = new();

    public string? SortColumn { get; set; }
    public bool SortDescending { get; set; }
}

/// <summary>Enabled is the user's preference; actual availability additionally requires the Dev-channel compile flag + gh CLI.</summary>
public sealed class SelfUpdateSettings
{
    public bool Enabled { get; set; } = true;
    public bool CheckOnStart { get; set; } = true;
}

/// <summary>
/// Extra checks on files the app downloads ITSELF (the self-update artifact, the NVIDIA driver installer).
/// Package managers fetch and verify their own downloads and are unaffected by anything here.
/// </summary>
public sealed class SecuritySettings
{
    /// <summary>
    /// Hand a downloaded file to the installed antivirus before running it. Off by default: it
    /// only ever applies to the few files this app fetches on its own, TLS, the publisher signature check
    /// and, for the self-update artifact, its SHA256SUMS verification already guard all of them, and it
    /// fails open regardless; no antivirus, or one that cannot answer, never blocks an update.
    /// </summary>
    public bool ScanDownloads { get; set; } = false;
}

/// <summary>
/// The once-only startup hardware auto-tune for both parallelism sliders. Pure: the actual probing
/// (core count, RAM, battery presence) happens at the call site, this only turns the three raw numbers
/// into the two settings values, so it can be tested without touching real hardware.
/// </summary>
public static class ParallelismAutoTune
{
    public readonly record struct Result(int MaxParallelSources, int MaxParallelInstalls);

    /// <summary>The three named machine shapes, cheapest first. A shape that matches neither of the two
    /// higher rows falls through to this one, which is why it is also the safe default.</summary>
    private static readonly (int Sources, int Installs)[] Steps =
    [
        (4, 1),
        (8, 2),
        (12, 3),
    ];

    private const long GigaByte = 1024L * 1024 * 1024;

    public static Result Compute(int processorCount, long totalRamBytes, bool hasBattery)
    {
        var step = 0;
        if (processorCount > 8 && totalRamBytes >= 16 * GigaByte)
            step = 2;
        else if (processorCount is >= 5 and <= 8 && totalRamBytes >= 8 * GigaByte)
            step = 1;

        // A battery-powered machine drops one step in both numbers; a machine already at the lowest
        // step has nowhere further down to go and stays there.
        if (hasBattery)
            step = Math.Max(0, step - 1);

        var (sources, installs) = Steps[step];
        return new Result(sources, installs);
    }
}
