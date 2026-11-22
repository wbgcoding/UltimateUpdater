using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UltimateUpdater.ViewModels.Settings;

/// <summary>Settings apply instantly (no Save button for toggles) and persist immediately.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LoggingService _logging;
    private readonly ThemeService _themeService;
    private readonly AutostartService _autostart;
    private readonly Func<Task<Services.SelfUpdate.SelfUpdateCheckOutcome>>? _checkForUpdates;

    /// <summary>Whether a source's own tool was last seen missing on this machine. Optional: a caller that cannot answer shows every source.</summary>
    private readonly Func<string, bool>? _isSourceNotInstalled;

    /// <summary>Snapshot of the table's rows when Settings was opened, keyed like <c>MainViewModel.PackageKey</c>, so the ignore list can show a real name instead of the raw key. See <see cref="ShowIgnoreList"/>.</summary>
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

    /// <summary>False until either parallelism slider has been moved by hand; mirrors <see cref="Models.Settings.AppSettings.ParallelismChosenByUser"/> and drives the "set automatically" caption. Once true it stays true on this machine, short of a factory reset.</summary>
    [ObservableProperty]
    private bool parallelismChosenByUser;

    /// <summary>Where the registry cleaner's mandatory pre-write backup is saved.</summary>
    [ObservableProperty]
    private string registryBackupFolder;

    /// <summary>How many of those backups are kept before the oldest is pruned.</summary>
    [ObservableProperty]
    private int registryBackupRetentionCount;

    /// <summary>How many days of logs are kept. A stored value that is not an offered one (a hand-edited settings.json) is shown as the nearest offered one but not written back until the user picks; housekeeping keeps using the stored value until then.</summary>
    [ObservableProperty]
    private int logRetentionDays;

    /// <summary>Same "shown as the nearest offered value, not written back until picked" contract as <see cref="LogRetentionDays"/>.</summary>
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

    [ObservableProperty]
    private bool autoUpdateCheck;

    /// <summary>Inverted from the settings root's CleanupReminderOff so the switch reads naturally; the banner's "never again" sets that flag directly.</summary>
    [ObservableProperty]
    private bool cleanupReminderEnabled;

    /// <summary>Measure the cleanup areas in the background at startup (see <c>AppSettings.CleanupPrescanAtStartup</c>).</summary>
    [ObservableProperty]
    private bool cleanupPrescanAtStartup;

    /// <summary>File targets go to the recycle bin instead of being deleted outright; read when a cleanup starts. A target that needs admin rights ignores it.</summary>
    [ObservableProperty]
    private bool cleanupRecycleInsteadOfDelete;

    [ObservableProperty]
    private string selectedTheme;

    /// <summary>Off: the app's own blue. On: the accent colour from the Windows settings.</summary>
    [ObservableProperty]
    private bool useSystemAccent;

    /// <summary>The two named themes carry their own colour, so the choice has no effect there.</summary>
    public bool CanChooseAccent => SelectedTheme is not ("hacker" or "neon");

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

    /// <summary>How long an install-history row is kept; the same offered values as the log, in a separate list for a separate setting.</summary>
    public IReadOnlyList<DayOption> HistoryRetentionOptions { get; } =
        [new(3), new(7), new(30), new(365)];

    /// <summary>Where the log files are, shown beside the button that opens them.</summary>
    public string LogFolder => AppPaths.LogsDirectory;

    /// <summary>Where the last ten one-click error reports are saved.</summary>
    public string ReportsFolder => ErrorReportBuilder.ReportsDirectory;

    /// <summary>Where settings, history, caches, logs and reports actually live right now.</summary>
    public string DataFolder => AppPaths.DataDirectory;

    /// <summary>Paths permanently excluded from the cleanup dialog via its right-click menu (<see cref="Models.Settings.AppSettings.CleanupPermanentlyExcludedPaths"/>); this is where one is released again. Rebuilt in place by <see cref="RebuildExcludedPaths"/>, since releasing a path shrinks the list at runtime.</summary>
    public ObservableCollection<ExcludedPathRow> ExcludedPaths { get; } = [];

    public int ExcludedPathCount => ExcludedPaths.Count;

    public bool HasExcludedPaths => ExcludedPaths.Count > 0;

    /// <summary>Drives the empty-state line.</summary>
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

    /// <summary>Case-insensitive, matching <c>CleanupViewModel.PermanentlyExcludePath</c>.</summary>
    private void ReleaseExcludedPath(string path)
    {
        var removed = _settings.Current.CleanupPermanentlyExcludedPaths
            .RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        Persist();
        RebuildExcludedPaths();
    }

    /// <summary>Releasing is reversible (cleanup simply offers the path again), so a normal confirm rather than the red one factory reset uses.</summary>
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

    /// <summary>One switch per source. Absent from settings means on, so a source added by a later version starts enabled.</summary>
    public IReadOnlyList<SourceToggle> SourceToggles { get; }

    /// <summary>Filtered view over <see cref="SourceToggles"/> for the search box. A live view rather than a second collection, so toggling a source does not fight the filter for which list is the truth; "all sources" still walks the unfiltered list.</summary>
    public ICollectionView SourceToggleView { get; }

    [ObservableProperty]
    private string sourceSearchText = string.Empty;

    partial void OnSourceSearchTextChanged(string value)
    {
        SourceToggleView.Refresh();
        OnPropertyChanged(nameof(HasNoSourceMatches));
    }

    /// <summary>Shown only while a search narrows the chips to nothing, so an empty result reads as "no match".</summary>
    public bool HasNoSourceMatches => !string.IsNullOrWhiteSpace(SourceSearchText) && SourceToggleView.IsEmpty;

    private bool FilterSourceToggle(object candidate)
    {
        if (candidate is not SourceToggle toggle)
            return true;

        // A source with no tool on this machine stays out of the way until "show all" is on.
        if (!ShowAllSources && toggle.IsNotInstalled)
            return false;

        return string.IsNullOrWhiteSpace(SourceSearchText)
            || toggle.Display.Contains(SourceSearchText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Off by default, so a fresh window does not lead with tools that are not on this machine. Not settings-backed: it is the app's own judgement of a default look, like <see cref="CleanupViewModel.ShowAllCategories"/>.</summary>
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

    /// <summary>Tri-state for the "all sources" checkbox: every source on, none, or a mix (null). Bound one-way; the click runs <see cref="ToggleAllSources"/>.</summary>
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

    /// <summary>The full legal notes behind the short About line, one paragraph each, for its tooltip.</summary>
    public string AboutLegalFull =>
        string.Join("\n\n", Loc["AboutThirdParty"], Loc["AboutTrademarks"], Loc["AboutIcons"]);

    /// <summary>One row of outbound traffic this program itself generates, mirroring the outbound traffic table of the design notes; a test asserts both tables hold the same hosts. The calling code is a maintainer reference and is never shown.</summary>
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

    /// <summary>The full inventory, regardless of what is installed; <see cref="NetworkTargets"/> is what the window shows. Same order and grouping as the design doc's table, which is updated first.</summary>
    public static IReadOnlyList<NetworkTarget> NetworkTargetCatalog { get; } =
    [
        new(
            "api.github.com, release-assets.githubusercontent.com, objects.githubusercontent.com",
            "NetPurposeSelfUpdate",
            "SelfUpdate/*UpdateChannel.cs, SelfUpdate/UpdateMetadata.cs, SelfUpdate/UpdateDownload.cs, Sources/DevTools/GitHubReleaseSource.cs, Sources/SelfUpdatingToolSource.cs",
            []),
        new(
            "gitlab.com",
            "NetPurposeChangelog",
            "ViewModels/MainViewModel.cs",
            []),
        new(
            "www.nvidia.com, gfwsl.geforce.com",
            "NetPurposeNvidia",
            "Sources/Drivers/NvidiaDriverSource.cs",
            ["nvidia-driver"]),
        new(
            "pypi.org, registry.npmjs.org, community.chocolatey.org, api.nuget.org, rubygems.org, repo.packagist.org, static.rust-lang.org, www.powershellgallery.com, nodejs.org, www.python.org",
            "NetPurposePackageMeta",
            "Sources/PackageManagers/*.cs, PipxSource.cs, UvToolSource.cs, VoltaSource.cs, PyenvWinSource.cs",
            []),
        new(
            "dl.google.com, storage.googleapis.com, marketplace.visualstudio.com",
            "NetPurposeSdkMeta",
            "Sources/DevTools/AndroidSdkSource.cs, VsCodeExtensionSource.cs, FlutterSource.cs",
            []),
        new(
            "api.steampowered.com",
            "NetPurposeSteam",
            "Sources/Launchers/SteamSource.cs",
            ["steam"]),
        new(
            "registry.ollama.ai",
            "NetPurposeOllamaRemote",
            "Sources/DevTools/OllamaSource.cs",
            ["ollama"]),
        new(
            "127.0.0.1:11434",
            "NetPurposeOllamaLocal",
            "Sources/DevTools/OllamaSource.cs",
            ["ollama"]),
    ];

    /// <summary>The rows shown: everything whose owning sources are not all known to be missing. An empty <see cref="NetworkTarget.SourceIds"/> means the row is tied to no source and always shows (self-update, changelog fetch). A snapshot at construction, like <see cref="SourceToggles"/>.</summary>
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

        // A broken data-folder pointer falls back silently where AppPaths resolves it, before any logger exists; this is the first place that owns one, so the line is written here.
        if (AppPaths.ConsumePointerFallbackReason() is { } fallbackReason)
            _logging.Warn(fallbackReason);

        // Same deferred logging, for the one-time legacy folder-name migration.
        if (AppPaths.ConsumeLegacyDataFolderMigrationMessage() is { } migrationMessage)
            _logging.Info(migrationMessage);

        if (AppPaths.ConsumeLegacyDataFolderMigrationFailure() is { } migrationFailure)
            _logging.Warn(migrationFailure);

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
        scanIntervalMinutes = NearestScanInterval(current.Scan.IntervalMinutes);
        scanOnStart = current.Scan.OnStart;
        scanRespectMetered = current.Scan.RespectMetered;
        preselectCritical = current.Install.PreselectCritical;
        createRestorePoint = current.Install.CreateRestorePoint;
        maxParallelInstalls = current.Install.MaxParallelInstalls;
        maxParallelSources = current.Scan.MaxParallelSources;
        parallelismChosenByUser = current.ParallelismChosenByUser;
        registryBackupFolder = current.RegistryCleanup.BackupFolder;
        _acceptedRegistryBackupFolder = registryBackupFolder;
        registryBackupRetentionCount = NearestRegistryBackupRetention(current.RegistryCleanup.RetentionCount);
        logRetentionDays = NearestLogRetention(current.Logs.RetentionDays);
        historyRetentionDays = NearestHistoryRetention(current.HistoryRetentionDays);
        scanDownloads = current.Security.ScanDownloads;
        Services.Security.MalwareScanner.ScanDownloads = scanDownloads;
        notifyOnFound = current.Notify.OnFound;
        notifyCriticalOnly = current.Notify.CriticalOnly;
        autostartEnabled = current.Autostart;
        closeToTray = current.Ui.CloseToTray;
        autoUpdateCheck = current.SelfUpdate.Enabled;
        cleanupReminderEnabled = !current.CleanupReminderOff;
        cleanupRecycleInsteadOfDelete = current.CleanupRecycleInsteadOfDelete;
        cleanupPrescanAtStartup = current.CleanupPrescanAtStartup;
        selectedTheme = current.Ui.Theme;
        useSystemAccent = current.Ui.UseSystemAccent;
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

    /// <summary>Set only while <see cref="ReloadFromSettings"/> writes the parallelism values back from a reloaded or reset settings object, so the setters do not re-pin the flag a factory reset just cleared.</summary>
    private bool _suppressParallelismPin;

    /// <summary>Moving either slider by hand is what "chosen by the user" means: the hardware auto-tune at startup never touches these settings again.</summary>
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

    /// <summary>The folder last stored or loaded; what a rejected entry falls back to.</summary>
    private string _acceptedRegistryBackupFolder = string.Empty;

    /// <summary>Set while the box is put back or loaded from settings: neither is user input, so neither is validated.</summary>
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

    /// <summary>Why a typed or picked registry backup folder is refused, or null when fine. The elevated backup only accepts folders inside the user profile, so another folder would silently stop every backup.</summary>
    internal string? RegistryBackupFolderProblem(string folder)
    {
        var rejection = AppPaths.ValidateRegistryBackupFolder(folder);
        return rejection == AppPaths.DataFolderRejection.None ? null : DataFolderRejectionText(rejection);
    }

    /// <summary>The folder picker; the choice goes through the same validation as typed text.</summary>
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

    /// <summary>The smallest offered value at least what is stored, so the dropdown is never blank and never claims a shorter retention than the one in force: a stored 14 shows as 30, since 7 would promise deleting files that are still kept.</summary>
    internal int NearestLogRetention(int days) =>
        LogRetentionOptions.FirstOrDefault(option => option.Days >= days)?.Days
        ?? LogRetentionOptions[^1].Days;

    /// <summary>Same rule as <see cref="NearestLogRetention"/> for the registry backup count.</summary>
    internal int NearestRegistryBackupRetention(int count) =>
        RetentionCountOptions.FirstOrDefault(option => option.Count >= count)?.Count
        ?? RetentionCountOptions[^1].Count;

    /// <summary>The offered interval closest to the stored one. Off (0) is picked only for 0 itself or below: a stored 60 minutes means a scan was wanted and must not turn into "never".</summary>
    internal int NearestScanInterval(int minutes)
    {
        if (minutes <= 0)
            return 0;

        return IntervalOptions
            .Where(option => option.Minutes > 0)
            .OrderBy(option => Math.Abs(option.Minutes - minutes))
            .First().Minutes;
    }

    /// <summary>Same rule as <see cref="NearestLogRetention"/>, kept separate because the two lists may diverge.</summary>
    internal int NearestHistoryRetention(int days) =>
        HistoryRetentionOptions.FirstOrDefault(option => option.Days >= days)?.Days
        ?? HistoryRetentionOptions[^1].Days;

    partial void OnScanDownloadsChanged(bool value)
    {
        Apply(s => s.Security.ScanDownloads = value);
        // The sources are built without settings, so the scanner reads this static gate.
        Services.Security.MalwareScanner.ScanDownloads = value;
    }

    partial void OnNotifyOnFoundChanged(bool value) => Apply(s => s.Notify.OnFound = value);

    partial void OnNotifyCriticalOnlyChanged(bool value) => Apply(s => s.Notify.CriticalOnly = value);

    partial void OnCloseToTrayChanged(bool value) => Apply(s => s.Ui.CloseToTray = value);

    partial void OnAutoUpdateCheckChanged(bool value) => Apply(s => s.SelfUpdate.Enabled = value);

    partial void OnCleanupReminderEnabledChanged(bool value) => Apply(s => s.CleanupReminderOff = !value);

    partial void OnCleanupRecycleInsteadOfDeleteChanged(bool value) => Apply(s => s.CleanupRecycleInsteadOfDelete = value);

    partial void OnCleanupPrescanAtStartupChanged(bool value) => Apply(s => s.CleanupPrescanAtStartup = value);

    partial void OnSelectedThemeChanged(string value)
    {
        Apply(s => s.Ui.Theme = value);
        _themeService.Apply(value);
        OnPropertyChanged(nameof(CanChooseAccent));
    }

    partial void OnUseSystemAccentChanged(bool value)
    {
        Apply(s => s.Ui.UseSystemAccent = value);
        _themeService.SetUseSystemAccent(value);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        Apply(s => s.Ui.Language = value);
        LocalizationService.Instance.Apply(value);

        // Computed text bound in this window, not reached by the Loc[...] bindings.
        OnPropertyChanged(nameof(ExcludedPathsHeaderText));
        OnPropertyChanged(nameof(AboutVersionText));
        OnPropertyChanged(nameof(AboutLegalFull));

        // Loc[...] bindings refresh through the localization service, but the interval items are plain objects with their own Display property.
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

        // And the main window's computed text, rows and dialogs: this is where the language is normally switched.
        LanguageChanged?.Invoke();
    }

    /// <summary>Raised right after the language has been applied, so the owner can re-raise the computed text that no binding reaches (see <c>MainViewModel.RefreshLocalizedText</c>).</summary>
    public event Action? LanguageChanged;

    partial void OnAutostartEnabledChanged(bool value)
    {
        // The registry write can fail on a locked-down machine; reflect reality rather than the wish.
        if (!_autostart.TrySet(value))
        {
            AutostartEnabled = _autostart.IsEnabled();
            return;
        }

        Apply(s => s.Autostart = value);
    }

    [RelayCommand]
    private void OpenLogFolder() => UriLauncher.OpenFolderDetached(AppPaths.LogsDirectory, "log folder", _logging);

    /// <summary>Creates the folder first, as <see cref="OpenLogFolder"/> does: it exists only once a report has been saved.</summary>
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

    /// <summary>The data folder itself; created first like the two above, though in practice it already exists.</summary>
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

    /// <summary>Picks a new home for the data folder, validates it as the cleanup dialog does, copies everything across and records the location for the next launch. The folder in use is untouched until the restart.</summary>
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

        // Only a subfolder this move creates may be removed again if a later step fails.
        var createdTarget = !Directory.Exists(effectiveTarget);

        // The copy can take a while on a slow drive or network share, so it runs off the UI thread; the command stays disabled until done.
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

        // The copy ran while the windows stayed usable: a setting, history entry or report written meanwhile would otherwise be missing from the new folder.
        if (!AppPaths.SyncLiveDataTo(effectiveTarget))
        {
            _logging.Warn("The current history and reports could not be written into the new data folder.");
            AppPaths.DiscardCreatedDataFolder(effectiveTarget, createdTarget);
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
            AppPaths.DiscardCreatedDataFolder(effectiveTarget, createdTarget);
            Dialogs.Warn(Loc["SettingsDataFolderChange"], Loc["SettingsDataFolderCopyFailed"]);
            return;
        }

        if (!AppPaths.WriteDataFolderPointer(effectiveTarget))
        {
            AppPaths.DiscardCreatedDataFolder(effectiveTarget, createdTarget);
            Dialogs.Warn(Loc["SettingsDataFolderChange"], Loc["SettingsDataFolderCopyFailed"]);
            return;
        }

        _logging.Info(
            $"Data folder copied to '{effectiveTarget}'; the previous folder at '{oldFolder}' was left in place.");

        Dialogs.Success(
            Loc["SettingsDataFolderChange"], string.Format(Loc["SettingsDataFolderMoved"], effectiveTarget, oldFolder));
    }

    /// <summary>Up to three file names, then an ellipsis, never the full path.</summary>
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

    /// <summary>Opens the ignore-list dialog, seeded with the row-name snapshot taken when this window was constructed. Non-modal, one window per kind.</summary>
    [RelayCommand]
    private void ShowIgnoreList()
    {
        var existing = Application.Current?.Windows.OfType<IgnoreListWindow>().FirstOrDefault(open => open.IsLoaded);
        if (existing is not null)
        {
            existing.Activate();
            return;
        }

        var ignoreList = CreateIgnoreListViewModel();
        var window = new IgnoreListWindow(ignoreList);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openIgnoreList, ignoreList))
                _openIgnoreList = null;
        };

        // Owned by the settings window so it closes with it; the main window is the fallback owner.
        window.Owner = Application.Current?.Windows.OfType<SettingsWindow>().FirstOrDefault(open => open.IsLoaded)
                       ?? Application.Current?.MainWindow;
        window.Show();
    }

    /// <summary>Raised when the ignore list dialog removed entries; the owner re-reads the table's ignore marks.</summary>
    public event Action? IgnoreListChanged;

    /// <summary>Raised after a factory reset deleted the app's files, so whoever holds them in memory (enrichment cache, rollback index, table view state) drops them too.</summary>
    public event Action? DataReset;

    internal IgnoreListViewModel CreateIgnoreListViewModel()
    {
        var viewModel = new IgnoreListViewModel(_settings, _ignoredPackageNames);
        viewModel.Changed += () => IgnoreListChanged?.Invoke();
        _openIgnoreList = viewModel;
        return viewModel;
    }

    /// <summary>The ignore list dialog while open, so a reset or an ignore made in the table can make it re-read its list.</summary>
    private IgnoreListViewModel? _openIgnoreList;

    /// <summary>The table changed the ignore list (the main view model forwards its marks event here).</summary>
    internal void RefreshOpenIgnoreList() => _openIgnoreList?.Refresh();

    /// <summary>Factory state in one action: every option, source switch and the ignore list go back to defaults and every file this app wrote (history, logs, caches) is deleted. Irreversible, so the confirmation is a red warning.</summary>
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

        // Off the UI thread: deleting every cache, history and log file can take a while.
        var failed = await Task.Run(() => AppDataCleaner.DeleteCachesAndHistory(_logging));
        _logging.Info("Application reset to factory state on user request.");
        DataReset?.Invoke();
        RefreshOpenIgnoreList();

        if (failed)
            Dialogs.Warn(Loc["SettingsDeleteDataPartialHeadline"], Loc["SettingsDeleteDataPartialBody"]);
        else
            Dialogs.Success(Loc["SettingsResetDoneHeadline"], Loc["SettingsResetDoneBody"]);
    }

    /// <summary>Set while <see cref="ReloadFromSettings"/> writes the UI-bound toggles back, so each does not round-trip through <see cref="SetSourceEnabled"/> and save settings.json again.</summary>
    private bool _suppressSourceToggleChanges;

    /// <summary>Re-reads every bound value from the (just replaced) settings object.</summary>
    internal void ReloadFromSettings()
    {
        var current = _settings.Current;

        // Through the generated properties, so theme, language and autostart really re-apply.
        ScanIntervalMinutes = NearestScanInterval(current.Scan.IntervalMinutes);
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
        RegistryBackupRetentionCount = NearestRegistryBackupRetention(current.RegistryCleanup.RetentionCount);
        LogRetentionDays = NearestLogRetention(current.Logs.RetentionDays);
        HistoryRetentionDays = NearestHistoryRetention(current.HistoryRetentionDays);
        ScanDownloads = current.Security.ScanDownloads;
        NotifyOnFound = current.Notify.OnFound;
        NotifyCriticalOnly = current.Notify.CriticalOnly;
        AutostartEnabled = current.Autostart;
        CloseToTray = current.Ui.CloseToTray;
        AutoUpdateCheck = current.SelfUpdate.Enabled;
        CleanupReminderEnabled = !current.CleanupReminderOff;
        CleanupRecycleInsteadOfDelete = current.CleanupRecycleInsteadOfDelete;
        CleanupPrescanAtStartup = current.CleanupPrescanAtStartup;
        SelectedTheme = current.Ui.Theme;
        UseSystemAccent = current.Ui.UseSystemAccent;
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

    /// <summary>Manual "check now" for a new app version. The check lives on the main view model, which owns the banner and the pending update; this drives it and reports the outcome in the card.</summary>
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
        // Keeps the "all sources" tri-state in step when a single source is toggled.
        OnPropertyChanged(nameof(AllSourcesEnabled));
    }

    private void Apply(Action<Models.Settings.AppSettings> change)
    {
        change(_settings.Current);
        Persist();
    }

    /// <summary>Every checkbox in this window ends up here, so the write is deferred and coalesced instead of blocking the UI thread per click (see <see cref="SettingsService.SaveDeferred"/>).</summary>
    private void Persist() => _settings.SaveDeferred();

    /// <summary>Labelled the way the rows and provider pills label it. The id stays the storage key and never reaches the screen.</summary>
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

        /// <summary>The tool behind this source was not found the last time anything scanned for it (<c>ScanOrchestrator.IsKnownNotInstalled</c>); a snapshot when the window opens.</summary>
        public bool IsNotInstalled { get; }

        public string Display => SourceNames.Display(Id);

        public override string ToString() => Display;

        /// <summary>What this source is, how it works, where it comes from: shown as a hover tooltip.</summary>
        public string Description => SourceNames.Description(Id);

        partial void OnIsEnabledChanged(bool value) => _onChanged(Id, value);

        /// <summary>Display and Description are read from the resources in code, so a language switch must re-raise them.</summary>
        internal void RefreshDisplay()
        {
            OnPropertyChanged(nameof(Display));
            OnPropertyChanged(nameof(Description));
        }
    }

    /// <summary>One row in the permanently-excluded-paths list, with the same callback shape as <see cref="SourceToggle"/>.</summary>
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

        /// <summary>WPF's ComboBoxItem automation peer uses this as the item's accessible name when the bound object is not a string; without it a screen reader announces the .NET type name.</summary>
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
