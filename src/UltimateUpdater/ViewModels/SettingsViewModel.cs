using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Settings apply instantly (no Save button for toggles) and persist immediately.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly ThemeService _themeService;
    private readonly AutostartService _autostart;
    private readonly Func<Task<Services.SelfUpdate.SelfUpdateCheckOutcome>>? _checkForUpdates;

    /// <summary>
    /// Whether a source's own tool was last seen missing on this machine
    /// (<c>ScanOrchestrator.IsKnownNotInstalled</c>). Optional and defaulting to "never hidden", so a caller
    /// that cannot answer simply shows every source.
    /// </summary>
    private readonly Func<string, bool>? _isSourceNotInstalled;

    /// <summary>Snapshot of the table's rows at the moment Settings was opened, keyed the same "source:id"
    /// way <c>MainViewModel.PackageKey</c> builds its own keys, so the ignore-list dialog can
    /// show a real name instead of the raw settings key. See <see cref="ShowIgnoreList"/>.</summary>
    private readonly IReadOnlyDictionary<string, string> _ignoredPackageNames;

    [ObservableProperty]
    private int scanIntervalMinutes;

    [ObservableProperty]
    private bool scanOnStart;

    [ObservableProperty]
    private bool scanRespectMetered;

    [ObservableProperty]
    private bool preselectCritical;

    [ObservableProperty]
    private bool createRestorePoint;

    [ObservableProperty]
    private int maxParallelInstalls;

    /// <summary>How many sources' scans may run at once, the scan side of the same pair.</summary>
    [ObservableProperty]
    private int maxParallelSources;

    /// <summary>
    /// False until either parallelism slider has been moved by hand, mirrors
    /// <see cref="Models.Settings.AppSettings.ParallelismChosenByUser"/>. Drives the "set automatically"
    /// caption next to both sliders; once true, the caption is gone for good on this machine (short of a
    /// factory reset).
    /// </summary>
    [ObservableProperty]
    private bool parallelismChosenByUser;

    /// <summary>Where the registry cleaner's mandatory pre-write backup is saved.</summary>
    [ObservableProperty]
    private string registryBackupFolder;

    /// <summary>How many of those backups are kept before the oldest is pruned.</summary>
    [ObservableProperty]
    private int registryBackupRetentionCount;

    /// <summary>
    /// How many days of logs are kept. A stored value that is not one of the offered ones (an older
    /// default, or a hand-edited settings.json) is SHOWN as the nearest offered one but not written back
    /// until the user picks something; housekeeping keeps using whatever is actually stored until then.
    /// </summary>
    [ObservableProperty]
    private int logRetentionDays;

    /// <summary>Same "shown as the nearest offered value, not written back until picked"
    /// contract as <see cref="LogRetentionDays"/>.</summary>
    [ObservableProperty]
    private int historyRetentionDays;

    /// <summary>Hand files this app downloads itself to the installed antivirus before running them.</summary>
    [ObservableProperty]
    private bool scanDownloads;

    [ObservableProperty]
    private bool notifyOnFound;

    [ObservableProperty]
    private bool notifyCriticalOnly;

    [ObservableProperty]
    private bool autostartEnabled;

    [ObservableProperty]
    private bool closeToTray;

    /// <summary>Toggle inverted from the settings root's CleanupReminderOff so the switch
    /// reads naturally ("on" means the reminder can appear); the banner's own "never again" sets that
    /// flag directly, this is just the other way to reach the same setting.</summary>
    [ObservableProperty]
    private bool cleanupReminderEnabled;

    /// <summary>File targets go to the recycle bin instead of being deleted outright; the cleanup run reads
    /// the stored setting when it starts. A target that needs admin rights ignores it regardless.</summary>
    [ObservableProperty]
    private bool cleanupRecycleInsteadOfDelete;

    [ObservableProperty]
    private string selectedTheme;

    [ObservableProperty]
    private string selectedLanguage;

    public LocalizationService Loc => LocalizationService.Instance;

    public IReadOnlyList<IntervalOption> IntervalOptions { get; } =
    [
        new(0, "SettingsIntervalOff"),
        new(360, "SettingsIntervalHours6"),
        new(720, "SettingsIntervalHours12"),
        new(1440, "SettingsIntervalDaily"),
        new(10080, "SettingsIntervalWeekly"),
    ];

    /// <summary>How many registry-cleanup backups to keep before the oldest is pruned.</summary>
    public IReadOnlyList<RetentionOption> RetentionCountOptions { get; } = [new(3), new(5), new(10), new(20)];

    /// <summary>Shown next to both parallelism sliders until either one is moved by hand.</summary>
    public bool ShowParallelismAutoNote => !ParallelismChosenByUser;

    /// <summary>How long a day's log file is kept before the housekeeping at the next start removes it.</summary>
    public IReadOnlyList<DayOption> LogRetentionOptions { get; } =
        [new(3), new(7), new(30), new(365)];

    /// <summary>How long an install-history row is kept: same offered values as the log, a separate list
    /// because it is a separate setting.</summary>
    public IReadOnlyList<DayOption> HistoryRetentionOptions { get; } =
        [new(3), new(7), new(30), new(365)];

    /// <summary>Where the log files are, shown so the button below is not a leap of faith.</summary>
    public string LogFolder => AppPaths.LogsDirectory;

    /// <summary>Where the last ten one-click error reports are saved.</summary>
    public string ReportsFolder => ErrorReportBuilder.ReportsDirectory;

    /// <summary>Where settings, history, caches, logs and reports actually live right now.</summary>
    public string DataFolder => AppPaths.DataDirectory;

    /// <summary>
    /// Paths permanently excluded from the cleanup dialog via its own right-click menu
    /// (<see cref="Models.Settings.AppSettings.CleanupPermanentlyExcludedPaths"/>): this is where one is released
    /// again. Rebuilt in place by <see cref="RebuildExcludedPaths"/>
    /// rather than a fixed list like <see cref="SourceToggles"/>, because releasing a path (unlike a
    /// source) actually shrinks it at runtime.
    /// </summary>
    public ObservableCollection<ExcludedPathRow> ExcludedPaths { get; } = [];

    public int ExcludedPathCount => ExcludedPaths.Count;

    public bool HasExcludedPaths => ExcludedPaths.Count > 0;

    /// <summary>Drives the empty-state line: when there is nothing to show, the row says so quietly
    /// rather than being missing outright.</summary>
    public bool HasNoExcludedPaths => ExcludedPaths.Count == 0;

    public string ExcludedPathsHeaderText => string.Format(Loc["SettingsExcludedPathsHeader"], ExcludedPathCount);

    private void RebuildExcludedPaths()
    {
        ExcludedPaths.Clear();
        foreach (var path in _settings.Current.CleanupPermanentlyExcludedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            ExcludedPaths.Add(new ExcludedPathRow(path, ReleaseExcludedPath));

        OnPropertyChanged(nameof(ExcludedPathCount));
        OnPropertyChanged(nameof(HasExcludedPaths));
        OnPropertyChanged(nameof(HasNoExcludedPaths));
        OnPropertyChanged(nameof(ExcludedPathsHeaderText));
    }

    /// <summary>Case-insensitive, same matching <c>CleanupViewModel.PermanentlyExcludePath</c> uses to add
    /// one in the first place.</summary>
    private void ReleaseExcludedPath(string path)
    {
        var removed = _settings.Current.CleanupPermanentlyExcludedPaths
            .RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        Persist();
        RebuildExcludedPaths();
    }

    /// <summary>Releasing is reversible in effect (cleanup simply offers the path again next time it is
    /// found), so a normal confirm rather than the destructive/red one factory reset uses.</summary>
    [RelayCommand]
    private void ReleaseAllExcludedPaths()
    {
        if (ExcludedPaths.Count == 0)
            return;

        if (!Dialogs.Confirm(
                Loc["SettingsExcludedPathReleaseAllConfirmHeadline"],
                Loc["SettingsExcludedPathReleaseAllConfirmBody"],
                confirmText: Loc["SettingsExcludedPathReleaseAll"]))
        {
            return;
        }

        _settings.Current.CleanupPermanentlyExcludedPaths.Clear();
        Persist();
        RebuildExcludedPaths();
    }

    /// <summary>
    /// One switch per source. Absent from settings means on, so a source added by a later version
    /// starts enabled rather than silently missing.
    /// </summary>
    public IReadOnlyList<SourceToggle> SourceToggles { get; }

    /// <summary>
    /// Filtered view over <see cref="SourceToggles"/> for the search box, with over 100 sources planned,
    /// the plain list stopped being scannable. A live <see cref="ICollectionView"/>
    /// rather than a second collection: toggling a source must not fight the filter for which list is the
    /// truth, and "all sources" still walks the real, unfiltered <see cref="SourceToggles"/>.
    /// </summary>
    public ICollectionView SourceToggleView { get; }

    [ObservableProperty]
    private string sourceSearchText = string.Empty;

    partial void OnSourceSearchTextChanged(string value)
    {
        SourceToggleView.Refresh();
        OnPropertyChanged(nameof(HasNoSourceMatches));
    }

    /// <summary>Shown only while a search narrows the chips to nothing, so an empty result reads as "no
    /// match" rather than "every source vanished".</summary>
    public bool HasNoSourceMatches => !string.IsNullOrWhiteSpace(SourceSearchText) && SourceToggleView.IsEmpty;

    private bool FilterSourceToggle(object candidate)
    {
        if (candidate is not SourceToggle toggle)
            return true;

        // The settings list's own "show all" escape, same shape as the pill-strip toggle:
        // a source with no tool on this machine stays out of the way until asked for, never gone for good.
        if (!ShowAllSources && toggle.IsNotInstalled)
            return false;

        return string.IsNullOrWhiteSpace(SourceSearchText)
            || toggle.Display.Contains(SourceSearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Off by default, so a fresh settings window does not lead with a wall of tools that are not
    /// even on this machine. Not settings-backed, the same "this is the app's own judgement of what is
    /// worth a default look, not a saved preference" reasoning <see cref="CleanupViewModel.ShowAllCategories"/>
    /// already documents for its own twin toggle.
    /// </summary>
    [ObservableProperty]
    private bool showAllSources;

    partial void OnShowAllSourcesChanged(bool value)
    {
        SourceToggleView.Refresh();
        OnPropertyChanged(nameof(HasNoSourceMatches));
    }

    /// <summary>How many entries <see cref="ShowAllSources"/> is currently holding back.</summary>
    public int HiddenSourceCount => SourceToggles.Count(t => t.IsNotInstalled);

    public bool HasHiddenSources => HiddenSourceCount > 0;

    public string ShowAllSourcesText => string.Format(Loc["SettingsShowAllSources"], HiddenSourceCount);

    /// <summary>
    /// Tri-state for the "all sources" checkbox: every source on (true), none (false), or a mix (null →
    /// indeterminate). Bound one-way; the box's click runs <see cref="ToggleAllSources"/>.
    /// </summary>
    public bool? AllSourcesEnabled
    {
        get
        {
            if (SourceToggles.Count == 0)
                return false;

            var on = SourceToggles.Count(t => t.IsEnabled);
            return on == 0 ? false : on == SourceToggles.Count ? true : null;
        }
    }

    /// <summary>Turns every source on, or (when they are all already on) off.</summary>
    [RelayCommand]
    private void ToggleAllSources()
    {
        var turnOn = AllSourcesEnabled != true;
        foreach (var toggle in SourceToggles)
            toggle.IsEnabled = turnOn;

        OnPropertyChanged(nameof(AllSourcesEnabled));
    }

    /// <summary>The running version, e.g. "Version 0.1.0".</summary>
    public string CurrentVersionText =>
        string.Format(Loc["SettingsCurrentVersion"], Services.SelfUpdate.SelfUpdateService.CurrentVersion);

    /// <summary>Same version text, worded for the About card.</summary>
    public string AboutVersionText =>
        string.Format(Loc["AboutVersion"], Services.SelfUpdate.SelfUpdateService.CurrentVersion);

    /// <summary>
    /// One row of outbound traffic this program itself generates, mirroring
    /// the outbound traffic table of the design notes; when those notes are present, a test asserts the two
    /// tables hold the same hosts so they cannot drift apart. Hosts and the calling code are plain
    /// non-localized fields; the purpose is a resource key rendered in the current language. The calling
    /// code is a maintainer reference and is never shown in the window.
    /// </summary>
    public sealed class NetworkTarget(string hosts, string purposeKey, string callingCode, IReadOnlyList<string> sourceIds)
        : ObservableObject
    {
        public string Hosts { get; } = hosts;

        public string CallingCode { get; } = callingCode;

        public IReadOnlyList<string> SourceIds { get; } = sourceIds;

        public string Purpose => LocalizationService.Instance[purposeKey];

        public void RefreshDisplay() => OnPropertyChanged(nameof(Purpose));

        public override string ToString() => $"{Hosts}: {Purpose}";
    }

    /// <summary>
    /// The full inventory, regardless of what is installed on this machine; <see cref="NetworkTargets"/>
    /// is what the window actually shows. Kept in the SAME order and grouping as the design doc's own
    /// table, and the design doc is what to update first, this table follows it, never the other way
    /// round.
    /// </summary>
    public static IReadOnlyList<NetworkTarget> NetworkTargetCatalog { get; } =
    [
        new(
            "api.github.com, release-assets.githubusercontent.com, objects.githubusercontent.com",
            "NetPurposeSelfUpdate",
            "SelfUpdate/*UpdateChannel.cs, SelfUpdate/UpdateMetadata.cs, SelfUpdate/UpdateDownload.cs, Sources/GitHubReleaseSource.cs, Sources/SelfUpdatingToolSource.cs",
            []),
        new(
            "gitlab.com",
            "NetPurposeChangelog",
            "ViewModels/MainViewModel.cs",
            []),
        new(
            "www.nvidia.com, gfwsl.geforce.com",
            "NetPurposeNvidia",
            "Sources/NvidiaDriverSource.cs",
            ["nvidia-driver"]),
        new(
            "pypi.org, registry.npmjs.org, community.chocolatey.org, api.nuget.org, rubygems.org, repo.packagist.org, static.rust-lang.org, www.powershellgallery.com, nodejs.org, www.python.org",
            "NetPurposePackageMeta",
            "Sources/PackageManagers/*.cs, PipxSource.cs, UvToolSource.cs, VoltaSource.cs, PyenvWinSource.cs",
            []),
        new(
            "dl.google.com, storage.googleapis.com, marketplace.visualstudio.com",
            "NetPurposeSdkMeta",
            "Sources/AndroidSdkSource.cs, VsCodeExtensionSource.cs, FlutterSource.cs",
            []),
        new(
            "api.steampowered.com",
            "NetPurposeSteam",
            "Sources/SteamSource.cs",
            ["steam"]),
        new(
            "registry.ollama.ai",
            "NetPurposeOllamaRemote",
            "Sources/OllamaSource.cs",
            ["ollama"]),
        new(
            "127.0.0.1:11434",
            "NetPurposeOllamaLocal",
            "Sources/OllamaSource.cs",
            ["ollama"]),
    ];

    /// <summary>
    /// The rows actually shown: everything whose owning sources are not ALL known to be missing from this
    /// machine (an empty <see cref="NetworkTarget.SourceIds"/> means the row is not tied to one particular
    /// source and always shows, self-update and the changelog fetch are not something a "source" is
    /// switched off for). A one-time snapshot at construction, same as <see cref="SourceToggles"/>.
    /// </summary>
    public IReadOnlyList<NetworkTarget> NetworkTargets { get; }

    public bool HasNetworkTargets => NetworkTargets.Count > 0;

    /// <summary>The version, with the result of the last "check now" appended when there is one.</summary>
    public string VersionAndStatus =>
        string.IsNullOrEmpty(UpdateStatusText) ? CurrentVersionText : $"{CurrentVersionText}   ·   {UpdateStatusText}";

    private enum UpdateStatus { None, Checking, Available, UpToDate, CheckFailed }

    // The state is stored, not the rendered sentence, so a language switch re-renders it.
    private UpdateStatus _updateStatus;

    private void SetUpdateStatus(UpdateStatus status)
    {
        _updateStatus = status;
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(VersionAndStatus));
    }

    public string UpdateStatusText => _updateStatus switch
    {
        UpdateStatus.Checking => Loc["SettingsCheckingUpdates"],
        UpdateStatus.Available => Loc["SelfUpdateTitle"],
        UpdateStatus.UpToDate => Loc["SettingsUpToDate"],
        UpdateStatus.CheckFailed => Loc["SettingsCheckFailed"],
        _ => string.Empty,
    };

    [ObservableProperty]
    private bool isCheckingUpdates;

    public SettingsViewModel(
        SettingsService settings,
        LoggingService logging,
        ThemeService themeService,
        AutostartService autostart,
        IReadOnlyList<string>? sourceIds = null,
        Func<Task<Services.SelfUpdate.SelfUpdateCheckOutcome>>? checkForUpdates = null,
        IReadOnlyDictionary<string, string>? ignoredPackageNames = null,
        Func<string, bool>? isSourceNotInstalled = null)
    {
        _settings = settings;
        _logging = logging;
        _themeService = themeService;
        _autostart = autostart;
        _checkForUpdates = checkForUpdates;
        _ignoredPackageNames = ignoredPackageNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _isSourceNotInstalled = isSourceNotInstalled;

        // A broken data-folder pointer falls back silently at the point AppPaths resolves it
        // (before any logger exists); this is the first place afterward that actually owns one, so it is
        // where the one line about it finally gets written.
        if (AppPaths.ConsumePointerFallbackReason() is { } fallbackReason)
            _logging.Warn(fallbackReason);

        // Same deferred-logging shape, for the one-time folder-name migration after the app was
        // renamed (AppPaths.TryMigrateLegacyDataFolder).
        if (AppPaths.ConsumeLegacyDataFolderMigrationMessage() is { } migrationMessage)
            _logging.Info(migrationMessage);

        SourceToggles = (sourceIds ?? [])
            .Select(id => new SourceToggle(
                id, settings.Current.Sources.GetValueOrDefault(id, true), SetSourceEnabled,
                _isSourceNotInstalled?.Invoke(id) ?? false))
            .OrderBy(t => t.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var view = CollectionViewSource.GetDefaultView(SourceToggles);
        view.Filter = FilterSourceToggle;
        SourceToggleView = view;

        NetworkTargets = NetworkTargetCatalog
            .Where(target => target.SourceIds.Count == 0
                || target.SourceIds.Any(id => !(_isSourceNotInstalled?.Invoke(id) ?? false)))
            .ToList();

        var current = settings.Current;
        scanIntervalMinutes = current.Scan.IntervalMinutes;
        scanOnStart = current.Scan.OnStart;
        scanRespectMetered = current.Scan.RespectMetered;
        preselectCritical = current.Install.PreselectCritical;
        createRestorePoint = current.Install.CreateRestorePoint;
        maxParallelInstalls = current.Install.MaxParallelInstalls;
        maxParallelSources = current.Scan.MaxParallelSources;
        parallelismChosenByUser = current.ParallelismChosenByUser;
        registryBackupFolder = current.RegistryCleanup.BackupFolder;
        _acceptedRegistryBackupFolder = registryBackupFolder;
        registryBackupRetentionCount = current.RegistryCleanup.RetentionCount;
        logRetentionDays = NearestLogRetention(current.Logs.RetentionDays);
        historyRetentionDays = NearestHistoryRetention(current.HistoryRetentionDays);
        scanDownloads = current.Security.ScanDownloads;
        Services.Security.MalwareScanner.ScanDownloads = scanDownloads;
        notifyOnFound = current.Notify.OnFound;
        notifyCriticalOnly = current.Notify.CriticalOnly;
        autostartEnabled = current.Autostart;
        closeToTray = current.Ui.CloseToTray;
        cleanupReminderEnabled = !current.CleanupReminderOff;
        cleanupRecycleInsteadOfDelete = current.CleanupRecycleInsteadOfDelete;
        selectedTheme = current.Ui.Theme;
        selectedLanguage = current.Ui.Language;

        RebuildExcludedPaths();
    }

    partial void OnScanIntervalMinutesChanged(int value) => Apply(s => s.Scan.IntervalMinutes = value);

    partial void OnScanOnStartChanged(bool value) => Apply(s => s.Scan.OnStart = value);

    partial void OnScanRespectMeteredChanged(bool value) => Apply(s => s.Scan.RespectMetered = value);

    partial void OnPreselectCriticalChanged(bool value) => Apply(s => s.Install.PreselectCritical = value);

    partial void OnCreateRestorePointChanged(bool value) => Apply(s => s.Install.CreateRestorePoint = value);

    partial void OnMaxParallelInstallsChanged(int value) => ApplyParallelismChange(s => s.Install.MaxParallelInstalls = value);

    partial void OnMaxParallelSourcesChanged(int value) => ApplyParallelismChange(s => s.Scan.MaxParallelSources = value);

    partial void OnParallelismChosenByUserChanged(bool value) => OnPropertyChanged(nameof(ShowParallelismAutoNote));

    /// <summary>
    /// Set only while <see cref="ReloadFromSettings"/> writes MaxParallelSources/MaxParallelInstalls back
    /// from a just-reloaded (or freshly reset) settings object: without it, a factory reset would
    /// immediately re-pin the flag it just cleared, as the reloaded value round-trips through the two
    /// setters below.
    /// </summary>
    private bool _suppressParallelismPin;

    /// <summary>Moving either slider by hand is what "chosen by the user" means: once true, the hardware
    /// auto-tune at startup never touches these two settings again, not even after an update.</summary>
    private void ApplyParallelismChange(Action<Models.Settings.AppSettings> change)
    {
        Apply(s =>
        {
            change(s);
            if (!_suppressParallelismPin)
                s.ParallelismChosenByUser = true;
        });

        if (!_suppressParallelismPin)
            ParallelismChosenByUser = true;
    }

    /// <summary>The folder last stored (or loaded), what a rejected entry falls back to.</summary>
    private string _acceptedRegistryBackupFolder = string.Empty;

    /// <summary>Set while the box is put back to <see cref="_acceptedRegistryBackupFolder"/> or loaded from
    /// settings: neither is user input, so neither is validated again.</summary>
    private bool _registryBackupFolderIsProgrammatic;

    partial void OnRegistryBackupFolderChanged(string value)
    {
        if (_registryBackupFolderIsProgrammatic)
            return;

        var problem = RegistryBackupFolderProblem(value);
        if (problem is null)
        {
            _acceptedRegistryBackupFolder = value;
            Apply(s => s.RegistryCleanup.BackupFolder = value);
            return;
        }

        SetRegistryBackupFolderProgrammatically(_acceptedRegistryBackupFolder);
        Dialogs.Warn(Loc["SettingsRegistryBackupFolder"], problem);
    }

    private void SetRegistryBackupFolderProgrammatically(string value)
    {
        _registryBackupFolderIsProgrammatic = true;
        try
        {
            RegistryBackupFolder = value;
        }
        finally
        {
            _registryBackupFolderIsProgrammatic = false;
        }
    }

    /// <summary>Why a typed or picked registry backup folder is refused, or null when it is fine. The
    /// elevated backup itself only accepts folders inside the user profile, so a folder outside would
    /// silently stop every registry backup later.</summary>
    internal string? RegistryBackupFolderProblem(string folder)
    {
        var rejection = AppPaths.ValidateRegistryBackupFolder(folder);
        return rejection == AppPaths.DataFolderRejection.None ? null : DataFolderRejectionText(rejection);
    }

    /// <summary>The folder picker next to the backup folder box; the choice goes through the same
    /// validation as typed text.</summary>
    [RelayCommand]
    private void BrowseRegistryBackupFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc["SettingsRegistryBackupFolder"],
            InitialDirectory = Directory.Exists(RegistryBackupFolder) ? RegistryBackupFolder : AppPaths.DataDirectory,
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            RegistryBackupFolder = dialog.FolderName;
    }

    partial void OnRegistryBackupRetentionCountChanged(int value) => Apply(s => s.RegistryCleanup.RetentionCount = value);

    partial void OnLogRetentionDaysChanged(int value) => Apply(s => s.Logs.RetentionDays = value);

    partial void OnHistoryRetentionDaysChanged(int value) => Apply(s => s.HistoryRetentionDays = value);

    /// <summary>
    /// The smallest offered value that is at least what is stored, so the dropdown never comes up blank
    /// and never claims a SHORTER retention than the one actually in force: a stored 14 shows as 30, not
    /// as 7, because 7 would read as a promise to delete files that are still there.
    /// </summary>
    internal int NearestLogRetention(int days) =>
        LogRetentionOptions.FirstOrDefault(option => option.Days >= days)?.Days
        ?? LogRetentionOptions[^1].Days;

    /// <summary>Same reasoning as <see cref="NearestLogRetention"/>, its own method since the two lists
    /// could diverge later even though they share the same four values today.</summary>
    internal int NearestHistoryRetention(int days) =>
        HistoryRetentionOptions.FirstOrDefault(option => option.Days >= days)?.Days
        ?? HistoryRetentionOptions[^1].Days;

    partial void OnScanDownloadsChanged(bool value)
    {
        Apply(s => s.Security.ScanDownloads = value);
        // The sources are built without settings, so the scanner reads this static gate instead.
        Services.Security.MalwareScanner.ScanDownloads = value;
    }

    partial void OnNotifyOnFoundChanged(bool value) => Apply(s => s.Notify.OnFound = value);

    partial void OnNotifyCriticalOnlyChanged(bool value) => Apply(s => s.Notify.CriticalOnly = value);

    partial void OnCloseToTrayChanged(bool value) => Apply(s => s.Ui.CloseToTray = value);

    partial void OnCleanupReminderEnabledChanged(bool value) => Apply(s => s.CleanupReminderOff = !value);

    partial void OnCleanupRecycleInsteadOfDeleteChanged(bool value) => Apply(s => s.CleanupRecycleInsteadOfDelete = value);

    partial void OnSelectedThemeChanged(string value)
    {
        Apply(s => s.Ui.Theme = value);
        _themeService.Apply(value);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        Apply(s => s.Ui.Language = value);
        LocalizationService.Instance.Apply(value);

        // Computed text bound in this very window, not reached by the Loc[...] bindings.
        OnPropertyChanged(nameof(ExcludedPathsHeaderText));
        OnPropertyChanged(nameof(AboutVersionText));

        // Loc[...] bindings refresh through the localization service itself, but the interval items
        // are ordinary objects with their own Display property. Without this the one list in the very
        // window that switches the language stayed in the old one until it was closed and reopened.
        foreach (var option in IntervalOptions)
            option.RefreshDisplay();

        foreach (var option in RetentionCountOptions)
            option.RefreshDisplay();

        foreach (var option in LogRetentionOptions)
            option.RefreshDisplay();

        foreach (var option in HistoryRetentionOptions)
            option.RefreshDisplay();

        foreach (var toggle in SourceToggles)
            toggle.RefreshDisplay();

        OnPropertyChanged(nameof(ShowAllSourcesText));
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(VersionAndStatus));

        foreach (var target in NetworkTargets)
            target.RefreshDisplay();

        // And the main window's own computed text, its rows and its dialogs: this is the place the
        // language is normally switched from, so leaving it to the next close left the app half-translated.
        LanguageChanged?.Invoke();
    }

    /// <summary>
    /// Raised right after the language has been applied, so the owner can re-raise the computed text that no
    /// binding reaches (see <c>MainViewModel.RefreshLocalizedText</c>).
    /// </summary>
    public event Action? LanguageChanged;

    partial void OnAutostartEnabledChanged(bool value)
    {
        // registry write can fail on a locked-down machine; reflect reality rather than the wish
        if (!_autostart.TrySet(value))
        {
            AutostartEnabled = _autostart.IsEnabled();
            return;
        }

        Apply(s => s.Autostart = value);
    }

    /// <summary>
    /// The folder is created first: nothing writes a log until something happens, and Explorer answers a
    /// path that does not exist with an error box rather than a window.
    /// </summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Could not create the log folder: {ex.Message}");
        }

        UriLauncher.LaunchDetached(AppPaths.LogsDirectory, _logging);
    }

    /// <summary>Same "create it first" reasoning as <see cref="OpenLogFolder"/>: a report is only written
    /// once a Copy click actually saved one, so the folder itself may not exist yet.</summary>
    [RelayCommand]
    private void OpenReports()
    {
        try
        {
            Directory.CreateDirectory(ErrorReportBuilder.ReportsDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Could not create the error reports folder: {ex.Message}");
        }

        UriLauncher.LaunchDetached(ErrorReportBuilder.ReportsDirectory, _logging);
    }

    /// <summary>The data folder itself, same "create it first" reasoning as the two folders above
    /// it, in practice always already there since the app itself just wrote to it.</summary>
    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"Could not create the data folder: {ex.Message}");
        }

        UriLauncher.LaunchDetached(AppPaths.DataDirectory, _logging);
    }

    /// <summary>
    /// Picks a new home for the data folder, validates it with the same checks the cleanup dialog
    /// already relies on, copies everything across and records the new location for the NEXT launch to
    /// pick up. Never touches the folder currently in use, that keeps working exactly as it does today
    /// until the restart the success message asks for.
    /// </summary>
    [RelayCommand]
    private async Task ChangeDataFolderAsync()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc["SettingsDataFolderChange"],
            InitialDirectory = AppPaths.DataDirectory,
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var picked = dialog.FolderName;
        var rejection = AppPaths.ValidateNewDataFolder(picked);
        if (rejection != AppPaths.DataFolderRejection.None)
        {
            Dialogs.Warn(Loc["SettingsDataFolderChange"], DataFolderRejectionText(rejection));
            return;
        }

        // The destination is always the dedicated subfolder, never the picked folder itself.
        var effectiveTarget = AppPaths.EffectiveDataFolderTarget(picked);
        var oldFolder = AppPaths.DataDirectory;

        // The copy covers logs, caches, history and backups and can take a while on a slow drive or a
        // network share, so it runs off the UI thread; the command stays disabled until it is done.
        var (moved, blockingSkippedFiles) = await Task.Run(() =>
        {
            var ok = AppPaths.TryMoveDataFolder(effectiveTarget, out var skipped);
            return (ok, skipped);
        });

        if (!moved)
        {
            var message = blockingSkippedFiles.Count > 0
                ? string.Format(Loc["SettingsDataFolderFilesLocked"], FormatSkippedFileNames(blockingSkippedFiles))
                : Loc["SettingsDataFolderCopyFailed"];
            Dialogs.Warn(Loc["SettingsDataFolderChange"], message);
            return;
        }

        // The copy ran while the windows stayed usable: a setting changed meanwhile would otherwise be
        // missing from the new folder after the restart. The same goes for the history and the reports
        // written meanwhile.
        if (!AppPaths.SyncLiveDataTo(effectiveTarget))
        {
            _logging.Warn("The current history and reports could not be written into the new data folder.");
            Dialogs.Warn(Loc["SettingsDataFolderChange"], Loc["SettingsDataFolderCopyFailed"]);
            return;
        }

        try
        {
            _settings.WriteCopyTo(Path.Combine(effectiveTarget, "settings.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logging.Warn($"The current settings could not be written into the new data folder: {ex.Message}");
            Dialogs.Warn(Loc["SettingsDataFolderChange"], Loc["SettingsDataFolderCopyFailed"]);
            return;
        }

        if (!AppPaths.WriteDataFolderPointer(effectiveTarget))
        {
            Dialogs.Warn(Loc["SettingsDataFolderChange"], Loc["SettingsDataFolderCopyFailed"]);
            return;
        }

        _logging.Info(
            $"Data folder copied to '{effectiveTarget}'; the previous folder at '{oldFolder}' was left in place.");

        Dialogs.Success(
            Loc["SettingsDataFolderChange"], string.Format(Loc["SettingsDataFolderMoved"], effectiveTarget, oldFolder));
    }

    /// <summary>Up to three file names, then an ellipsis, never the full path (the folder is
    /// already named in the dialog's own context).</summary>
    private static string FormatSkippedFileNames(IReadOnlyList<string> files)
    {
        var names = files.Select(Path.GetFileName).Take(3);
        var text = string.Join(", ", names);
        return files.Count > 3 ? text + ", ..." : text;
    }

    private string DataFolderRejectionText(AppPaths.DataFolderRejection reason) => reason switch
    {
        AppPaths.DataFolderRejection.Link => Loc["SettingsDataFolderRejectedLink"],
        AppPaths.DataFolderRejection.SystemFolder => Loc["SettingsDataFolderRejectedSystem"],
        AppPaths.DataFolderRejection.InsideProgramFolder => Loc["SettingsDataFolderRejectedProgram"],
        AppPaths.DataFolderRejection.NonEmptyTarget => Loc["SettingsDataFolderRejectedNonEmpty"],
        AppPaths.DataFolderRejection.InsideCurrentDataFolder => Loc["SettingsDataFolderRejectedCurrent"],
        AppPaths.DataFolderRejection.TempOrCache => Loc["SettingsDataFolderRejectedTempOrCache"],
        AppPaths.DataFolderRejection.OutsideUserProfile => Loc["SettingsRegistryBackupOutsideProfile"],
        _ => Loc["SettingsDataFolderRejectedWritable"],
    };

    /// <summary>The GitHub project page shown and opened from the About section.</summary>
    public const string RepositoryUrl = "https://github.com/wbgcoding/UltimateUpdater";

    [RelayCommand]
    private void OpenRepository() => UriLauncher.LaunchDetached(RepositoryUrl, _logging);

    /// <summary>Opens the ignore-list management dialog, seeded with the row-name snapshot taken
    /// when this window was constructed. Non-modal, same "one window per kind" rule the rest of the app's
    /// owned dialogs follow.</summary>
    [RelayCommand]
    private void ShowIgnoreList()
    {
        var existing = Application.Current?.Windows.OfType<Views.IgnoreListWindow>().FirstOrDefault(open => open.IsLoaded);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var window = new Views.IgnoreListWindow(CreateIgnoreListViewModel());

        // Owned by the settings window it was opened from, so it closes with it; the main window only when
        // there is no settings window (it cannot be opened any other way, but the fallback keeps an owner).
        window.Owner = Application.Current?.Windows.OfType<Views.SettingsWindow>().FirstOrDefault(open => open.IsLoaded)
                       ?? Application.Current?.MainWindow;
        window.Show();
    }

    /// <summary>Raised when the ignore list dialog removed entries; the owner re-reads the table's ignore marks.</summary>
    public event Action? IgnoreListChanged;

    /// <summary>Raised after a factory reset deleted the app's files, so whoever holds them in memory (the
    /// enrichment cache, the history's rollback index, the main table's view state) drops them too.</summary>
    public event Action? DataReset;

    internal IgnoreListViewModel CreateIgnoreListViewModel()
    {
        var viewModel = new IgnoreListViewModel(_settings, _ignoredPackageNames);
        viewModel.Changed += () => IgnoreListChanged?.Invoke();
        return viewModel;
    }

    /// <summary>
    /// Factory state in ONE action: every
    /// option, every source switch and the ignore list go back to their defaults AND every file this app
    /// wrote (history, logs, caches) is deleted. Destructive and irreversible, so the confirmation is a red
    /// warning. The first-run source picker comes back on the next start.
    /// </summary>
    [RelayCommand]
    private async Task ResetEverythingAsync()
    {
        if (!Dialogs.Confirm(
                Loc["SettingsResetEverythingConfirmHeadline"],
                Loc["SettingsResetEverythingConfirmBody"],
                confirmText: Loc["SettingsResetEverything"],
                destructive: true))
        {
            return;
        }

        _settings.Reset();
        Persist();
        ReloadFromSettings();

        // Off the UI thread: deleting every cache/history/log file this app wrote can take a
        // while, and running it synchronously here would freeze the window for that long.
        var failed = await Task.Run(() => AppDataCleaner.DeleteCachesAndHistory(_logging));
        _logging.Info("Application reset to factory state on user request.");
        DataReset?.Invoke();

        if (failed)
            Dialogs.Warn(Loc["SettingsDeleteDataPartialHeadline"], Loc["SettingsDeleteDataPartialBody"]);
        else
            Dialogs.Success(Loc["SettingsResetDoneHeadline"], Loc["SettingsResetDoneBody"]);
    }

    /// <summary>Set while <see cref="ReloadFromSettings"/> is writing the UI-bound toggles back to what
    /// <see cref="_settings"/> already says, without this, each one round-tripped through
    /// <see cref="SetSourceEnabled"/> and saved settings.json AGAIN, once per source, even
    /// though the value being set already matches what was just persisted.</summary>
    private bool _suppressSourceToggleChanges;

    /// <summary>Re-reads every bound value from the (just replaced) settings object.</summary>
    internal void ReloadFromSettings()
    {
        var current = _settings.Current;

        // through the generated properties, so theme/language/autostart really re-apply
        ScanIntervalMinutes = current.Scan.IntervalMinutes;
        ScanOnStart = current.Scan.OnStart;
        ScanRespectMetered = current.Scan.RespectMetered;
        PreselectCritical = current.Install.PreselectCritical;
        CreateRestorePoint = current.Install.CreateRestorePoint;

        _suppressParallelismPin = true;
        try
        {
            MaxParallelInstalls = current.Install.MaxParallelInstalls;
            MaxParallelSources = current.Scan.MaxParallelSources;
        }
        finally
        {
            _suppressParallelismPin = false;
        }
        ParallelismChosenByUser = current.ParallelismChosenByUser;

        _acceptedRegistryBackupFolder = current.RegistryCleanup.BackupFolder;
        SetRegistryBackupFolderProgrammatically(current.RegistryCleanup.BackupFolder);
        RegistryBackupRetentionCount = current.RegistryCleanup.RetentionCount;
        LogRetentionDays = NearestLogRetention(current.Logs.RetentionDays);
        HistoryRetentionDays = NearestHistoryRetention(current.HistoryRetentionDays);
        ScanDownloads = current.Security.ScanDownloads;
        NotifyOnFound = current.Notify.OnFound;
        NotifyCriticalOnly = current.Notify.CriticalOnly;
        AutostartEnabled = current.Autostart;
        CloseToTray = current.Ui.CloseToTray;
        CleanupReminderEnabled = !current.CleanupReminderOff;
        CleanupRecycleInsteadOfDelete = current.CleanupRecycleInsteadOfDelete;
        SelectedTheme = current.Ui.Theme;
        SelectedLanguage = current.Ui.Language;

        RebuildExcludedPaths();

        _suppressSourceToggleChanges = true;
        try
        {
            foreach (var toggle in SourceToggles)
                toggle.IsEnabled = current.Sources.GetValueOrDefault(toggle.Id, true);
        }
        finally
        {
            _suppressSourceToggleChanges = false;
        }

        OnPropertyChanged(nameof(AllSourcesEnabled));
    }

    /// <summary>
    /// Manual "check now" for a new app version. The check itself lives on the main view model (it owns
    /// the banner and the pending update); this just drives it and reports the outcome in the card.
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_checkForUpdates is null || IsCheckingUpdates)
            return;

        IsCheckingUpdates = true;
        SetUpdateStatus(UpdateStatus.Checking);
        try
        {
            var outcome = await _checkForUpdates();
            SetUpdateStatus(outcome switch
            {
                Services.SelfUpdate.SelfUpdateCheckOutcome.Found => UpdateStatus.Available,
                Services.SelfUpdate.SelfUpdateCheckOutcome.Failed => UpdateStatus.CheckFailed,
                _ => UpdateStatus.UpToDate,
            });
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private void SetSourceEnabled(string sourceId, bool enabled)
    {
        if (_suppressSourceToggleChanges)
            return;

        Apply(s => s.Sources[sourceId] = enabled);
        // keep the "all sources" checkbox's tri-state in step when a single source is toggled
        OnPropertyChanged(nameof(AllSourcesEnabled));
    }

    private void Apply(Action<Models.Settings.AppSettings> change)
    {
        change(_settings.Current);
        Persist();
    }

    /// <summary>
    /// Every checkbox in this window ends up here, so the write is deferred and coalesced by the settings
    /// service instead of blocking the UI thread per click (see <see cref="SettingsService.SaveDeferred"/>).
    /// </summary>
    private void Persist() => _settings.SaveDeferred();

    /// <summary>
    /// Labelled the way the rows and the provider pills label it, so the switch is recognisably the
    /// same thing. The id stays the storage key and never reaches the screen.
    /// </summary>
    public sealed partial class SourceToggle : ObservableObject
    {
        private readonly Action<string, bool> _onChanged;

        [ObservableProperty]
        private bool isEnabled;

        public SourceToggle(string id, bool enabled, Action<string, bool> onChanged, bool isNotInstalled = false)
        {
            Id = id;
            isEnabled = enabled;
            _onChanged = onChanged;
            IsNotInstalled = isNotInstalled;
        }

        public string Id { get; }

        /// <summary>The tool behind this source was not found on this machine the last time
        /// anything scanned for it (<c>ScanOrchestrator.IsKnownNotInstalled</c>), taken as a one-time
        /// snapshot when the settings window opens, same as every other value in this list.</summary>
        public bool IsNotInstalled { get; }

        public string Display => SourceNames.Display(Id);

        public override string ToString() => Display;

        /// <summary>What this source is, how it works, where it comes from: shown as a hover tooltip.</summary>
        public string Description => SourceNames.Description(Id);

        partial void OnIsEnabledChanged(bool value) => _onChanged(Id, value);

        /// <summary>Display and Description are read from the resources in code, not through
        /// a binding: same reason IntervalOption/RetentionOption need this, so a language switch does
        /// not leave the source list's names and tooltips in the old language.</summary>
        internal void RefreshDisplay()
        {
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(Description));
        }
    }

    /// <summary>One row in the permanently-excluded-paths list, with the same
    /// callback-into-the-parent shape as <see cref="SourceToggle"/>.</summary>
    public sealed partial class ExcludedPathRow(string path, Action<string> onRelease) : ObservableObject
    {
        public string Path => path;

        public override string ToString() => path;

        [RelayCommand]
        private void Release() => onRelease(path);
    }

    public sealed class IntervalOption(int minutes, string resourceKey) : ObservableObject
    {
        public int Minutes => minutes;

        public string Display => LocalizationService.Instance[resourceKey];

        internal void RefreshDisplay() => OnPropertyChanged(nameof(Display));

        /// <summary>WPF's ComboBoxItem automation peer falls back to this for the item's accessible
        /// name when the bound object is not itself a string, without it, a screen reader announced
        /// the full ".NET type name" instead of the visible text (found live on the VM, `vmctl items`).</summary>
        public override string ToString() => Display;
    }

    public sealed class RetentionOption(int count) : ObservableObject
    {
        public int Count => count;

        public string Display => string.Format(LocalizationService.Instance["SettingsRegistryRetentionOption"], count);

        internal void RefreshDisplay() => OnPropertyChanged(nameof(Display));

        /// <summary>See <see cref="IntervalOption.ToString"/>.</summary>
        public override string ToString() => Display;
    }

    public sealed class DayOption(int days) : ObservableObject
    {
        public int Days => days;

        public string Display => string.Format(LocalizationService.Instance["SettingsLogRetentionOption"], days);

        internal void RefreshDisplay() => OnPropertyChanged(nameof(Display));

        /// <summary>See <see cref="IntervalOption.ToString"/>.</summary>
        public override string ToString() => Display;
    }
}
