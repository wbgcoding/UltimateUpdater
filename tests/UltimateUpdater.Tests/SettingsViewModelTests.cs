using System.ComponentModel;
using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

[Collection("UiLanguage")]
public class SettingsViewModelTests
{
    private static SettingsViewModel Create()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-settings-viewmodel-").FullName, "settings.json");
        return new SettingsViewModel(new SettingsService(logging, path), logging, new ThemeService(), new AutostartService(logging));
    }

    /// <summary>The recycle bin option moved here from the cleanup window: flipping the switch writes the
    /// setting the cleanup run reads, it survives a reload from disk, and a reload of the window's
    /// own state shows it again.</summary>
    [Fact]
    public void The_recycle_bin_instead_of_delete_switch_toggles_and_persists_the_setting()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-settings-viewmodel-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        Assert.False(viewModel.CleanupRecycleInsteadOfDelete);

        viewModel.CleanupRecycleInsteadOfDelete = true;

        Assert.True(settings.Current.CleanupRecycleInsteadOfDelete);
        settings.Flush();
        Assert.True(new SettingsService(logging, path).Current.CleanupRecycleInsteadOfDelete);

        viewModel.CleanupRecycleInsteadOfDelete = false;
        settings.Flush();
        Assert.False(new SettingsService(logging, path).Current.CleanupRecycleInsteadOfDelete);

        settings.Current.CleanupRecycleInsteadOfDelete = true;
        viewModel.ReloadFromSettings();
        Assert.True(viewModel.CleanupRecycleInsteadOfDelete);
    }

    /// <summary>
    /// The dropdown offers four values; a settings.json can hold any number. What is shown must never
    /// claim files are deleted sooner than they really are, so an in-between value rounds UP.
    /// </summary>
    [Theory]
    [InlineData(30, 30)]
    [InlineData(14, 30)]
    [InlineData(1, 3)]
    [InlineData(400, 365)]
    public void An_unlisted_log_retention_is_shown_as_the_next_offered_one(int stored, int shown) =>
        Assert.Equal(shown, Create().NearestLogRetention(stored));

    /// <summary>Same rounding rule as the log's own retention, its own dropdown.</summary>
    [Theory]
    [InlineData(30, 30)]
    [InlineData(14, 30)]
    [InlineData(1, 3)]
    [InlineData(400, 365)]
    public void An_unlisted_history_retention_is_shown_as_the_next_offered_one(int stored, int shown) =>
        Assert.Equal(shown, Create().NearestHistoryRetention(stored));

    /// <summary>The constructor must set every backing field directly from settings, not rely on
    /// ReloadFromSettings alone, a field wired only into the latter leaves its dropdown blank right after
    /// construction (0 matches none of the offered days).</summary>
    [Fact]
    public void HistoryRetentionDays_is_populated_from_settings_right_after_construction() =>
        Assert.Equal(30, Create().HistoryRetentionDays);

    /// <summary>Moving the scan slider is what "chosen by the user" means: once it happens, the startup
    /// hardware auto-tune must never touch either parallelism setting again.</summary>
    [Fact]
    public void Moving_the_scan_parallelism_slider_pins_the_chosen_by_user_flag()
    {
        var viewModel = Create();
        Assert.False(viewModel.ParallelismChosenByUser);

        viewModel.MaxParallelSources = viewModel.MaxParallelSources == 3 ? 5 : 3;

        Assert.True(viewModel.ParallelismChosenByUser);
    }

    /// <summary>Same contract on the install side of the same pair.</summary>
    [Fact]
    public void Moving_the_install_parallelism_slider_pins_the_chosen_by_user_flag()
    {
        var viewModel = Create();
        Assert.False(viewModel.ParallelismChosenByUser);

        viewModel.MaxParallelInstalls = viewModel.MaxParallelInstalls == 2 ? 3 : 2;

        Assert.True(viewModel.ParallelismChosenByUser);
    }

    /// <summary>
    /// A factory reset (which clears the flag in the underlying settings object) must not have that clear
    /// immediately undone by <see cref="SettingsViewModel.ReloadFromSettings"/> writing the reloaded slider
    /// values back through the same setters a real user edit uses: those writes must not themselves count
    /// as "the user moved it".
    /// </summary>
    [Fact]
    public void Reloading_settings_does_not_pin_the_flag_on_its_own()
    {
        var viewModel = Create();

        viewModel.ReloadFromSettings();

        Assert.False(viewModel.ParallelismChosenByUser);
    }

    /// <summary>
    /// SourceToggle.Display and .Description are read from the resources in code, not through a binding,
    /// so a language switch must explicitly refresh the source list's names and hover descriptions,
    /// otherwise they stay in the old language until the window is closed and reopened.
    /// </summary>
    [Fact]
    public void Switching_language_refreshes_every_source_toggles_display_and_description()
    {
        var viewModel = new SettingsViewModel(
            new SettingsService(new LoggingService(), Path.Combine(
                Directory.CreateTempSubdirectory("wu-sources-lang-").FullName, "settings.json")),
            new LoggingService(), new ThemeService(), new AutostartService(new LoggingService()), ["winget"]);

        var toggle = viewModel.SourceToggles.First(t => t.Id == "winget");
        var raisedDisplay = false;
        var raisedDescription = false;
        toggle.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.SourceToggle.Display))
                raisedDisplay = true;
            if (args.PropertyName == nameof(SettingsViewModel.SourceToggle.Description))
                raisedDescription = true;
        };

        viewModel.SelectedLanguage = viewModel.SelectedLanguage == "de" ? "en" : "de";

        Assert.True(raisedDisplay);
        Assert.True(raisedDescription);
    }

    /// <summary>
    /// ReloadFromSettings must not round-trip every source toggle through SetSourceEnabled, which saves
    /// settings.json, even when the value being written back already matches what was just persisted.
    /// Reset() clears Sources to empty (every source defaults to on); the reload's suppression keeps that
    /// dictionary empty rather than getting explicit "true" entries re-added for every single source.
    /// </summary>
    [Fact]
    public void Reloading_settings_does_not_re_populate_the_sources_dictionary_per_toggle()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-sources-reload-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget", "npm", "pip"]);

        // A real value change going into the reload, a source switched off beforehand, so the reload
        // has to flip it back to on (true) and can actually trigger SetSourceEnabled if unsuppressed.
        viewModel.SourceToggles.First(t => t.Id == "npm").IsEnabled = false;
        Assert.NotEmpty(settings.Current.Sources);

        settings.Reset();
        Assert.Empty(settings.Current.Sources);

        viewModel.ReloadFromSettings();

        Assert.Empty(settings.Current.Sources);
        Assert.All(viewModel.SourceToggles, t => Assert.True(t.IsEnabled));
    }

    /// <summary>
    /// Switching a source off has to survive the window being closed, and a source the settings file
    /// has never heard of has to start switched ON, otherwise a source added by a later version
    /// would silently never run.
    /// </summary>
    [Fact]
    public void Switching_a_source_off_is_persisted_and_unknown_sources_default_to_on()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-sources-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget", "npm"]);

        Assert.All(viewModel.SourceToggles, t => Assert.True(t.IsEnabled));

        viewModel.SourceToggles.First(t => t.Id == "npm").IsEnabled = false;

        settings.Flush();
        var reloaded = new SettingsService(logging, path);
        Assert.False(reloaded.Current.Sources["npm"]);
        Assert.True(reloaded.Current.Sources.GetValueOrDefault("winget", true));
        Assert.True(reloaded.Current.Sources.GetValueOrDefault("a-source-from-a-later-version", true));
    }

    /// <summary>
    /// The interval list is the one set of labels in the settings window that does not come from a
    /// Loc[...] binding, so a language switch must explicitly relabel it, otherwise it keeps the old
    /// language until the window is closed and reopened, in the very window that switches the language.
    /// </summary>
    [Fact]
    public void Switching_the_language_relabels_the_interval_options()
    {
        // Applied explicitly: the view model only reflects the stored language, so assigning the one
        // already stored is a no-op and would leave whatever culture ran last in place.
        LocalizationService.Instance.Apply("de");

        var viewModel = Create();
        viewModel.SelectedLanguage = "de";

        var option = viewModel.IntervalOptions[3];
        var german = option.Display;

        var relabelled = false;
        ((INotifyPropertyChanged)option).PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SettingsViewModel.IntervalOption.Display))
                relabelled = true;
        };

        viewModel.SelectedLanguage = "en";

        Assert.True(relabelled, "The interval options were never told their label changed.");
        Assert.NotEqual(german, option.Display);
        Assert.Equal("24 hours", option.Display);
    }

    [Fact]
    public void Interval_options_cover_the_documented_choices()
    {
        var minutes = Create().IntervalOptions.Select(o => o.Minutes).ToArray();

        Assert.Equal([0, 360, 720, 1440, 10080], minutes);
    }

    /// <summary>WPF's ComboBoxItem automation peer falls back to the bound object's ToString() for
    /// the item's accessible name whenever the item is not itself a string, without an override here,
    /// a screen reader announced the full ".NET type name" instead of the visible text (found live on
    /// the VM via `vmctl items`, all four settings dropdowns backed by these three option types).
    /// Before the fix this asserted the type name; ToString() had no override at all.</summary>
    [Fact]
    public void Option_types_used_as_combobox_items_have_their_display_text_as_ToString()
    {
        var viewModel = Create();

        Assert.Equal(viewModel.IntervalOptions[0].Display, viewModel.IntervalOptions[0].ToString());
        Assert.Equal(viewModel.RetentionCountOptions[0].Display, viewModel.RetentionCountOptions[0].ToString());
        Assert.Equal(viewModel.LogRetentionOptions[0].Display, viewModel.LogRetentionOptions[0].ToString());
    }

    [Fact]
    public void All_sources_toggle_reflects_and_flips_every_source()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-allsrc-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);

        var viewModel = new SettingsViewModel(
            settings, logging, new ThemeService(), new AutostartService(logging), ["winget", "npm", "scoop"]);

        Assert.True(viewModel.AllSourcesEnabled);           // all on to start

        viewModel.SourceToggles.First(t => t.Id == "npm").IsEnabled = false;
        Assert.Null(viewModel.AllSourcesEnabled);           // mixed → indeterminate

        viewModel.ToggleAllSourcesCommand.Execute(null);    // some off → turn all on
        Assert.True(viewModel.AllSourcesEnabled);
        Assert.All(viewModel.SourceToggles, t => Assert.True(t.IsEnabled));

        viewModel.ToggleAllSourcesCommand.Execute(null);    // all on → turn all off
        Assert.False(viewModel.AllSourcesEnabled);
        Assert.All(viewModel.SourceToggles, t => Assert.False(t.IsEnabled));

        settings.Flush();
        var reloaded = new SettingsService(logging, path);
        Assert.False(reloaded.Current.Sources["winget"]);
    }

    [Fact]
    public void Changing_a_toggle_persists_it()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-toggle-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        var original = viewModel.ScanOnStart;

        viewModel.ScanOnStart = !original;
        settings.Flush();

        var reloaded = new SettingsService(logging, path);
        Assert.Equal(!original, reloaded.Current.Scan.OnStart);
    }

    /// <summary>Releasing removes exactly the one path, case-insensitively (same matching the add side
    /// already uses), and persists.</summary>
    [Fact]
    public void Releasing_one_excluded_path_removes_it_and_persists()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-excluded-release-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        settings.Current.CleanupPermanentlyExcludedPaths.AddRange(["C:\\a\\Cache", "C:\\b\\Temp"]);
        settings.Save();

        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        Assert.Equal(2, viewModel.ExcludedPathCount);

        var row = viewModel.ExcludedPaths.First(r => string.Equals(r.Path, "C:\\A\\CACHE", StringComparison.OrdinalIgnoreCase));
        row.ReleaseCommand.Execute(null);

        Assert.Equal(1, viewModel.ExcludedPathCount);
        Assert.DoesNotContain(viewModel.ExcludedPaths, r => string.Equals(r.Path, "C:\\a\\Cache", StringComparison.OrdinalIgnoreCase));

        settings.Flush();
        var reloaded = new SettingsService(logging, path);
        Assert.DoesNotContain(reloaded.Current.CleanupPermanentlyExcludedPaths, p => string.Equals(p, "C:\\a\\Cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("C:\\b\\Temp", reloaded.Current.CleanupPermanentlyExcludedPaths);
    }

    /// <summary>
    /// Dialogs.Confirm always declines under Dialogs.SuppressInteractiveDialogs (there is no existing
    /// mechanism in this codebase to stub it to "confirmed", ResetEverythingAsync's own confirm has never
    /// had a test for its accepted branch either). This proves the gate actually blocks the destructive
    /// clear rather than running regardless of the answer.
    /// </summary>
    [Fact]
    public void Releasing_all_excluded_paths_is_declined_without_confirmation()
    {
        Services.Dialogs.SuppressInteractiveDialogs = true;

        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-excluded-releaseall-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        settings.Current.CleanupPermanentlyExcludedPaths.AddRange(["C:\\a\\Cache", "C:\\b\\Temp"]);
        settings.Save();

        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        Assert.Equal(2, viewModel.ExcludedPathCount);

        viewModel.ReleaseAllExcludedPathsCommand.Execute(null);

        Assert.Equal(2, viewModel.ExcludedPathCount);
        var reloaded = new SettingsService(logging, path);
        Assert.Equal(2, reloaded.Current.CleanupPermanentlyExcludedPaths.Count);
    }

    /// <summary>Same reasoning as the sources-dictionary reload test above: Reset() already clears
    /// CleanupPermanentlyExcludedPaths, and the view model's own collection has to follow.</summary>
    [Fact]
    public void Reloading_settings_empties_the_excluded_paths_collection()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-excluded-reload-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        settings.Current.CleanupPermanentlyExcludedPaths.Add("C:\\a\\Cache");
        settings.Save();

        var viewModel = new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging));
        Assert.Equal(1, viewModel.ExcludedPathCount);

        settings.Reset();
        Assert.Empty(settings.Current.CleanupPermanentlyExcludedPaths);

        viewModel.ReloadFromSettings();

        Assert.Equal(0, viewModel.ExcludedPathCount);
        Assert.True(viewModel.HasNoExcludedPaths);
    }

    /// <summary>
    /// "Delete my data" claims every file this app wrote, including the rotated history and the cleanup
    /// history. Run against a throwaway data directory, the real one belongs to whoever is running the
    /// build.
    /// </summary>
    [Fact]
    public void Deleting_the_app_data_removes_every_file_the_app_writes()
    {
        var dataDirectory = Directory.CreateTempSubdirectory("wu-appdata-clean-").FullName;
        foreach (var relative in AppDataCleaner.OwnedDirectories)
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dataDirectory, relative)).FullName, "x.dat"), "x");

        foreach (var name in AppDataCleaner.OwnedFiles)
            File.WriteAllText(Path.Combine(dataDirectory, name), "x");

        // named outright so a future trim of OwnedFiles cannot quietly drop them
        Assert.Contains("history.jsonl.1", AppDataCleaner.OwnedFiles);
        Assert.Contains("cleanup-history.json", AppDataCleaner.OwnedFiles);
        Assert.Contains("last-scan.json", AppDataCleaner.OwnedFiles);
        Assert.Contains("last-scan.json.tmp", AppDataCleaner.OwnedFiles);

        var failed = AppDataCleaner.DeleteOwnedContent(dataDirectory, new LoggingService());

        Assert.False(failed);
        Assert.Empty(Directory.GetFileSystemEntries(dataDirectory));
    }

    private static (SettingsViewModel ViewModel, SettingsService Settings) CreateWithSettings()
    {
        var logging = new LoggingService();
        var path = Path.Combine(Directory.CreateTempSubdirectory("wu-settings-viewmodel-").FullName, "settings.json");
        var settings = new SettingsService(logging, path);
        return (new SettingsViewModel(settings, logging, new ThemeService(), new AutostartService(logging)), settings);
    }

    /// <summary>A folder outside the profile is something the elevated backup refuses later; typing one keeps
    /// the previous value (setting and box) and tells the user why, once.</summary>
    [Fact]
    public void A_registry_backup_folder_outside_the_profile_keeps_the_previous_value_and_warns()
    {
        var (viewModel, settings) = CreateWithSettings();
        var before = settings.Current.RegistryCleanup.BackupFolder;
        var warnings = new List<string>();
        var suppress = Services.Dialogs.SuppressInteractiveDialogs;
        Services.Dialogs.SuppressInteractiveDialogs = true;
        Services.Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            viewModel.RegistryBackupFolder = @"Z:\Not\In\The\Profile";
        }
        finally
        {
            Services.Dialogs.WarnObserverForTests = null;
            Services.Dialogs.SuppressInteractiveDialogs = suppress;
        }

        Assert.Equal(before, viewModel.RegistryBackupFolder);
        Assert.Equal(before, settings.Current.RegistryCleanup.BackupFolder);
        Assert.Single(warnings);
    }

    [Fact]
    public void A_registry_backup_folder_inside_the_profile_is_stored()
    {
        var (viewModel, settings) = CreateWithSettings();
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "wu-test-registry-backups");
        var warnings = new List<string>();
        Services.Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            viewModel.RegistryBackupFolder = folder;
        }
        finally
        {
            Services.Dialogs.WarnObserverForTests = null;
        }

        Assert.Equal(folder, settings.Current.RegistryCleanup.BackupFolder);
        Assert.Empty(warnings);
    }

    /// <summary>A stored value is not user input: reloading the window must neither warn about it nor
    /// rewrite it.</summary>
    [Fact]
    public void Reloading_a_stored_registry_backup_folder_outside_the_profile_does_not_warn()
    {
        var (viewModel, settings) = CreateWithSettings();
        settings.Current.RegistryCleanup.BackupFolder = @"Z:\Edited\By\Hand";
        var warnings = new List<string>();
        Services.Dialogs.WarnObserverForTests = warnings.Add;
        try
        {
            viewModel.ReloadFromSettings();
        }
        finally
        {
            Services.Dialogs.WarnObserverForTests = null;
        }

        Assert.Equal(@"Z:\Edited\By\Hand", viewModel.RegistryBackupFolder);
        Assert.Equal(@"Z:\Edited\By\Hand", settings.Current.RegistryCleanup.BackupFolder);
        Assert.Empty(warnings);
    }

    [Fact]
    public void The_outside_the_profile_rejection_has_its_own_message()
    {
        var (viewModel, _) = CreateWithSettings();

        Assert.Equal(viewModel.Loc["SettingsRegistryBackupOutsideProfile"], viewModel.RegistryBackupFolderProblem(@"Z:\Elsewhere"));
        Assert.Null(viewModel.RegistryBackupFolderProblem(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "backups")));
    }
}
