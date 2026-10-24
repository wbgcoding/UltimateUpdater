using System.Collections;
using System.Reflection;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("wu-settings-test-").FullName;
    private readonly LoggingService _logging = new();

    private string SettingsPath => Path.Combine(_tempDir, "settings.json");

    /// <summary>A data folder copy made in the background gets the settings as they are in memory, including
    /// a change that was never saved yet.</summary>
    [Fact]
    public void WriteCopyTo_writes_the_unsaved_in_memory_state()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Current.Ui.Theme = "dark";
        var copy = Path.Combine(_tempDir, "copy-settings.json");

        service.WriteCopyTo(copy);

        Assert.Equal("dark", new SettingsService(_logging, copy).Current.Ui.Theme);
    }

    [Fact]
    public void New_settings_file_yields_defaults()
    {
        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(1, service.Current.SchemaVersion);
        Assert.Equal("system", service.Current.Ui.Theme);
        Assert.Equal("system", service.Current.Ui.Language);
        Assert.Equal(1440, service.Current.Scan.IntervalMinutes);
    }

    /// <summary>A file written by an older build still carries the removed pill toggle; loading it must
    /// neither fail nor drop the neighbouring values.</summary>
    [Fact]
    public void A_file_with_the_removed_hide_unavailable_sources_field_still_loads()
    {
        File.WriteAllText(SettingsPath,
            "{\"ui\":{\"theme\":\"dark\",\"hideUnavailableSources\":false,\"closeToTray\":true}}");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("dark", service.Current.Ui.Theme);
        Assert.True(service.Current.Ui.CloseToTray);
    }

    [Fact]
    public void Repair_target_choice_is_reset_once_and_the_flag_set()
    {
        File.WriteAllText(SettingsPath,
            "{\"cleanupSelection\":{\"system-repair-dism\":true,\"system-repair-sfc\":true,\"temp-files\":true,\"other\":false}}");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.True(service.Current.CleanupRepairDefaultsApplied);
        Assert.False(service.Current.CleanupSelection.ContainsKey("system-repair-dism"));
        Assert.False(service.Current.CleanupSelection.ContainsKey("system-repair-sfc"));
        Assert.True(service.Current.CleanupSelection["temp-files"]);
        Assert.False(service.Current.CleanupSelection["other"]);

        // Persisted straight away: a reload neither brings the ticks back nor resets again.
        var reloaded = new SettingsService(_logging, SettingsPath);
        Assert.True(reloaded.Current.CleanupRepairDefaultsApplied);
        Assert.False(reloaded.Current.CleanupSelection.ContainsKey("system-repair-dism"));
    }

    [Fact]
    public void Repair_target_tick_is_kept_once_the_reset_has_run()
    {
        File.WriteAllText(SettingsPath,
            "{\"cleanupRepairDefaultsApplied\":true,\"cleanupSelection\":{\"system-repair-dism\":true,\"system-repair-sfc\":true}}");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.True(service.Current.CleanupSelection["system-repair-dism"]);
        Assert.True(service.Current.CleanupSelection["system-repair-sfc"]);
    }

    [Fact]
    public void Fresh_install_ends_with_the_repair_reset_flag_set()
    {
        var service = new SettingsService(_logging, SettingsPath);

        Assert.True(service.Current.CleanupRepairDefaultsApplied);
        Assert.Empty(service.Current.CleanupSelection);
    }

    [Fact]
    public void Save_then_reload_round_trips_values()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Current.Ui.Theme = "dark";
        service.Current.Ui.Language = "de";
        service.Current.Autostart = true;
        service.Save();

        var reloaded = new SettingsService(_logging, SettingsPath);

        Assert.Equal("dark", reloaded.Current.Ui.Theme);
        Assert.Equal("de", reloaded.Current.Ui.Language);
        Assert.True(reloaded.Current.Autostart);
    }

    [Fact]
    public void Save_writes_utf8_without_bom()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Save();

        var bytes = File.ReadAllBytes(SettingsPath);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom, "settings.json must not have a UTF-8 BOM");
        Assert.Contains("\"schemaVersion\"", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Corrupt_settings_file_falls_back_to_defaults_instead_of_throwing()
    {
        File.WriteAllText(SettingsPath, "{ not valid json ");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("system", service.Current.Ui.Theme);
    }

    /// <summary>
    /// A source removed from the registry after an older build wrote it into "sources" (an id no source
    /// currently registers, e.g. one of the removed link only driver rows) must not crash the load, and
    /// must not live on for ever either: it is dropped on load (against <see cref="SourceRegistry.KnownIds"/>)
    /// rather than sitting unused in the dictionary indefinitely.
    /// </summary>
    [Fact]
    public void Unknown_source_id_in_the_file_loads_without_throwing()
    {
        File.WriteAllText(
            SettingsPath,
            """
            { "sources": { "amd-driver": false, "winget": true } }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("system", service.Current.Ui.Theme);
        Assert.True(service.Current.Sources.GetValueOrDefault("winget"));
        Assert.False(service.Current.Sources.ContainsKey("amd-driver"));
    }

    /// <summary>
    /// The scenario a real settings.json showed in the field: a source id removed from the registry
    /// (dotnet-workload) survived indefinitely because nothing ever pruned it. Two unknown ids are dropped
    /// on load; every known id survives, including one explicitly switched off (false is a real, meaningful
    /// value here, not a stand-in for "absent").
    /// </summary>
    [Fact]
    public void Unknown_source_ids_are_dropped_while_every_known_id_survives()
    {
        File.WriteAllText(
            SettingsPath,
            """
            { "sources": { "dotnet-workload": true, "amd-driver": false, "winget": true, "scoop": false } }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.False(service.Current.Sources.ContainsKey("dotnet-workload"));
        Assert.False(service.Current.Sources.ContainsKey("amd-driver"));
        Assert.True(service.Current.Sources.GetValueOrDefault("winget"));
        Assert.True(service.Current.Sources.ContainsKey("scoop"));
        Assert.False(service.Current.Sources["scoop"]);

        service.Save();
        var reloaded = new SettingsService(_logging, SettingsPath);

        Assert.False(reloaded.Current.Sources.ContainsKey("dotnet-workload"));
        Assert.False(reloaded.Current.Sources.ContainsKey("amd-driver"));
        Assert.True(reloaded.Current.Sources.GetValueOrDefault("winget"));
        Assert.False(reloaded.Current.Sources["scoop"]);
    }

    /// <summary>A file written before ignored rows were hidden by default still carries the old switch;
    /// it is dropped on load and the new default (ignored rows hidden) applies.</summary>
    [Fact]
    public void The_old_hide_ignored_switch_in_the_file_is_ignored_and_ignored_rows_stay_hidden_by_default()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "schemaVersion": 1,
              "ui": { "hideIgnored": false, "hideManual": true }
            }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.False(service.Current.Ui.ShowIgnored);
        Assert.True(service.Current.Ui.HideManual);
    }

    /// <summary>A file written while the retry switch still existed carries the removed member; it is
    /// ignored on load and the other install values survive.</summary>
    [Fact]
    public void The_removed_retry_and_first_run_members_in_an_old_file_still_load()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "schemaVersion": 1,
              "firstRunDone": true,
              "install": { "retryFailedOnce": false, "maxParallelInstalls": 4 }
            }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(4, service.Current.Install.MaxParallelInstalls);
    }

    /// <summary>
    /// An explicit null is VALID JSON, so it deserialises into a null section instead of falling into the
    /// corrupt-file path, and the crash then happened at startup, far from the file that caused it.
    /// </summary>
    [Fact]
    public void Explicit_nulls_in_the_file_are_replaced_by_defaults()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "schemaVersion": 1,
              "scan": null,
              "sources": null,
              "notify": null,
              "install": null,
              "ui": null,
              "selfUpdate": null,
              "security": null,
              "excludes": null,
              "logs": null
            }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(1440, service.Current.Scan.IntervalMinutes);
        Assert.NotNull(service.Current.Sources);
        Assert.True(service.Current.Notify.OnFound);
        Assert.True(service.Current.Install.PreselectCritical);
        Assert.Equal("system", service.Current.Ui.Theme);
        Assert.Equal("system", service.Current.Ui.Language);
        Assert.True(service.Current.SelfUpdate.Enabled);
        Assert.False(service.Current.Security.ScanDownloads);
        Assert.Empty(service.Current.Excludes);
        Assert.Equal(30, service.Current.Logs.RetentionDays);
    }

    /// <summary>
    /// The same crash class as the sections above, for the cleanup collections: a hand-edited (or older)
    /// file carrying an explicit null here takes the app down the moment the cleanup dialog reads the
    /// saved selection, or a scan applies the permanent exclusions.
    /// </summary>
    [Fact]
    public void Explicit_nulls_in_the_cleanup_collections_are_replaced_by_defaults()
    {
        File.WriteAllText(
            SettingsPath,
            """
            {
              "cleanupSelection": null,
              "cleanupPermanentlyExcludedPaths": null
            }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Empty(service.Current.CleanupSelection);
        Assert.Empty(service.Current.CleanupPermanentlyExcludedPaths);

        // the two accesses the app makes, in the same order, a null collection would throw on either
        Assert.False(service.Current.CleanupSelection.TryGetValue("user-temp", out _));
        Assert.DoesNotContain(@"C:\Temp\A", service.Current.CleanupPermanentlyExcludedPaths);
    }

    /// <summary>The table layouts are read while the main window is built, so a null anywhere in them
    /// failed every launch.</summary>
    [Fact]
    public void Explicit_nulls_in_the_table_layouts_are_replaced_by_defaults()
    {
        File.WriteAllText(
            SettingsPath,
            """
            { "ui": { "tables": { "main": { "hiddenColumns": null, "shownColumns": null, "pinnedColumns": null }, "history": null } } }
            """);

        var service = new SettingsService(_logging, SettingsPath);

        var main = service.Current.Ui.Tables["main"];
        Assert.Empty(main.HiddenColumns);
        Assert.Empty(main.ShownColumns);
        Assert.Empty(main.PinnedColumns);
        Assert.False(service.Current.Ui.Tables.ContainsKey("history"));

        File.WriteAllText(SettingsPath, """{ "ui": { "tables": null } }""");
        Assert.Empty(new SettingsService(_logging, SettingsPath).Current.Ui.Tables);
    }

    /// <summary>A blank entry matches no real path and would only ever be compared against one, the same
    /// treatment the ignore list already gets.</summary>
    [Fact]
    public void A_blank_entry_in_the_permanent_cleanup_exclusions_is_dropped()
    {
        File.WriteAllText(
            SettingsPath, """{ "cleanupPermanentlyExcludedPaths": ["C:\\Temp\\A", null, "   "] }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal([@"C:\Temp\A"], service.Current.CleanupPermanentlyExcludedPaths);
    }

    /// <summary>A null inside the ignore list is a key nothing can look up; it is dropped on load.</summary>
    [Fact]
    public void A_null_entry_in_the_ignore_list_is_dropped()
    {
        File.WriteAllText(SettingsPath, """{ "excludes": ["winget:vlc@1.0", null, "  "] }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(["winget:vlc@1.0"], service.Current.Excludes);
    }

    /// <summary>
    /// The ignore list is the one setting a user expects to outlive the app by definition, an update
    /// ignored today must not come back tomorrow. Both key forms are covered: one exact version
    /// ("source:id@version") and a whole package ("source:id").
    /// </summary>
    [Fact]
    public void The_ignore_list_survives_a_reload_in_both_key_forms()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Current.Excludes.Add("winget:Mozilla.Firefox@141.0");
        service.Current.Excludes.Add("choco:7zip");
        service.Save();

        Assert.Contains("\"excludes\"", File.ReadAllText(SettingsPath));

        var reloaded = new SettingsService(_logging, SettingsPath);

        Assert.Equal(["winget:Mozilla.Firefox@141.0", "choco:7zip"], reloaded.Current.Excludes);
    }

    /// <summary>Same crash class as the null-collection tests above, for the new Snoozed dictionary, a
    /// hand-edited or older settings.json without it must not take the app down the first time a scan
    /// looks a package up in it.</summary>
    [Fact]
    public void An_explicit_null_snoozed_dictionary_is_replaced_by_defaults()
    {
        File.WriteAllText(SettingsPath, """{ "snoozed": null }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Empty(service.Current.Snoozed);
        Assert.False(service.Current.Snoozed.TryGetValue("winget:vlc", out _));
    }

    /// <summary>
    /// A snooze whose due date has already passed must not resurrect the row the moment the app is
    /// reopened, the whole point of "remind me later" is that it eventually stops hiding the update.
    /// An entry not yet due survives untouched.
    /// </summary>
    [Fact]
    public void An_expired_snooze_is_swept_on_load_but_a_future_one_survives()
    {
        var past = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var future = DateTimeOffset.UtcNow.AddDays(7).ToString("O");
        File.WriteAllText(
            SettingsPath,
            $$"""{ "snoozed": { "winget:vlc": "{{past}}", "choco:7zip": "{{future}}" } }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.False(service.Current.Snoozed.ContainsKey("winget:vlc"));
        Assert.True(service.Current.Snoozed.ContainsKey("choco:7zip"));
    }

    /// <summary>The snooze list survives a reload, same as the ignore list already does.</summary>
    [Fact]
    public void The_snoozed_dictionary_survives_a_reload()
    {
        var service = new SettingsService(_logging, SettingsPath);
        var due = DateTimeOffset.UtcNow.AddDays(1);
        service.Current.Snoozed["winget:Mozilla.Firefox@141.0"] = due;
        service.Save();

        var reloaded = new SettingsService(_logging, SettingsPath);

        Assert.Equal(due, reloaded.Current.Snoozed["winget:Mozilla.Firefox@141.0"]);
    }

    /// <summary>Same crash class as the null-collection tests above, for the new PersistentFailures
    /// dictionary, a hand-edited or older settings.json without it must not take the app down the first
    /// time an install result looks a package up in it.</summary>
    [Fact]
    public void An_explicit_null_persistent_failures_dictionary_is_replaced_by_defaults()
    {
        File.WriteAllText(SettingsPath, """{ "persistentFailures": null }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Empty(service.Current.PersistentFailures);
        Assert.False(service.Current.PersistentFailures.TryGetValue("winget:vlc", out _));
    }

    /// <summary>A null record value (a value type couldn't do this, but this dictionary's value is a
    /// class) is a broken entry nothing can read a Count or Signature from; it is dropped on load rather
    /// than surfacing a NullReferenceException the first time it is touched.</summary>
    [Fact]
    public void A_null_valued_entry_in_persistent_failures_is_dropped()
    {
        File.WriteAllText(SettingsPath, """{ "persistentFailures": { "winget:vlc": null } }""");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Empty(service.Current.PersistentFailures);
    }

    /// <summary>The persistent-failure record survives a reload, same as Snoozed and the ignore list.</summary>
    [Fact]
    public void The_persistent_failures_dictionary_survives_a_reload()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Current.PersistentFailures["winget:Mozilla.Firefox"] = new PersistentFailureRecord
        {
            Signature = "GenericFailure:1602",
            Count = 3,
            LastAt = DateTimeOffset.UtcNow,
            LastErrorText = "mock tool (exit code 1602)",
        };
        service.Save();

        var reloaded = new SettingsService(_logging, SettingsPath);

        var record = reloaded.Current.PersistentFailures["winget:Mozilla.Firefox"];
        Assert.Equal("GenericFailure:1602", record.Signature);
        Assert.Equal(3, record.Count);
        Assert.Equal("mock tool (exit code 1602)", record.LastErrorText);
    }

    /// <summary>
    /// MainViewModel.RowActions caps and redacts LastErrorText only when it is newly SET, so a
    /// value already sitting in settings.json from before that cap existed (or from a larger cap that
    /// later shrank) must be brought in line the moment it is loaded, not left on disk uncapped for ever.
    /// </summary>
    [Fact]
    public void An_oversized_persistent_failure_error_text_already_on_disk_is_capped_on_load()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var longError = new string('e', 5000) + $" reading {home}\\AppData\\config.json";

        var onDisk = new
        {
            persistentFailures = new Dictionary<string, object>
            {
                ["winget:vlc"] = new
                {
                    signature = "x",
                    count = 3,
                    lastAt = "2026-01-01T00:00:00+00:00",
                    lastErrorText = longError,
                },
            },
        };
        File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(onDisk));

        var service = new SettingsService(_logging, SettingsPath);

        var record = service.Current.PersistentFailures["winget:vlc"];
        Assert.True(record.LastErrorText!.Length <= 4000);
        Assert.DoesNotContain(home, record.LastErrorText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Reset everything" must mean everything: every section, including Security, has to return to its
    /// default, an omitted section would quietly survive a reset the UI claims is complete.
    /// </summary>
    [Fact]
    public void A_factory_reset_matches_the_defaults_in_every_section()
    {
        var service = new SettingsService(_logging, SettingsPath);

        service.Current.SchemaVersion = 99;
        service.Current.Scan.IntervalMinutes = 15;
        service.Current.Scan.OnStart = false;
        service.Current.Notify.CriticalOnly = true;
        service.Current.Install.CreateRestorePoint = false;
        service.Current.Ui.Theme = "dark";
        service.Current.Ui.HideManual = true;
        service.Current.Autostart = true;
        service.Current.SelfUpdate.Enabled = false;
        service.Current.Security.ScanDownloads = false;
        service.Current.Logs.RetentionDays = 90;
        service.Current.Sources["winget"] = false;
        service.Current.Excludes.Add("winget:vlc@1.0");
        service.Current.Snoozed["choco:7zip"] = DateTimeOffset.UtcNow.AddDays(7);
        service.Current.PersistentFailures["choco:7zip"] = new PersistentFailureRecord
        {
            Signature = "GenericFailure:1",
            Count = 3,
            LastAt = DateTimeOffset.UtcNow,
            LastErrorText = "mock tool (exit code 1)",
        };
        service.Current.CleanupSelection["user-temp"] = false;
        service.Current.CleanupRecycleInsteadOfDelete = true;
        service.Current.CleanupPrescanAtStartup = false;
        service.Current.HistoryRetentionDays = 365;
        service.Current.CleanupPermanentlyExcludedPaths.Add(@"C:\keep-me");

        service.Reset();

        var defaults = new AppSettings();
        Assert.Equal(defaults.SchemaVersion, service.Current.SchemaVersion);
        Assert.Equal(defaults.Scan.IntervalMinutes, service.Current.Scan.IntervalMinutes);
        Assert.Equal(defaults.Scan.OnStart, service.Current.Scan.OnStart);
        Assert.Equal(defaults.Notify.CriticalOnly, service.Current.Notify.CriticalOnly);
        Assert.Equal(defaults.Install.CreateRestorePoint, service.Current.Install.CreateRestorePoint);
        Assert.Equal(defaults.Ui.Theme, service.Current.Ui.Theme);
        Assert.Equal(defaults.Ui.HideManual, service.Current.Ui.HideManual);
        Assert.Equal(defaults.Autostart, service.Current.Autostart);
        Assert.Equal(defaults.SelfUpdate.Enabled, service.Current.SelfUpdate.Enabled);
        Assert.Equal(defaults.Security.ScanDownloads, service.Current.Security.ScanDownloads);
        Assert.Equal(defaults.Logs.RetentionDays, service.Current.Logs.RetentionDays);
        Assert.Empty(service.Current.Sources);
        Assert.Empty(service.Current.Excludes);
        Assert.Empty(service.Current.Snoozed);
        Assert.Empty(service.Current.PersistentFailures);
        Assert.Empty(service.Current.CleanupSelection);
        Assert.Equal(defaults.CleanupRecycleInsteadOfDelete, service.Current.CleanupRecycleInsteadOfDelete);
        Assert.True(service.Current.CleanupPrescanAtStartup);
        Assert.Equal(defaults.HistoryRetentionDays, service.Current.HistoryRetentionDays);
        Assert.Empty(service.Current.CleanupPermanentlyExcludedPaths);
    }

    /// <summary>
    /// Reflection guard for <see cref="SettingsService.Reset"/>: sets every public settable property of
    /// <see cref="AppSettings"/> (recursively into every nested settings class, and every collection filled
    /// with a non-default entry) to a non-default value, then asserts Reset() brings the serialized object
    /// back to exactly what <c>new AppSettings()</c> serializes to. A member added to AppSettings, or a
    /// class it references, and forgotten in Reset() fails this test with the property that still differs,
    /// instead of surviving silently like the four members this guards against.
    /// </summary>
    [Fact]
    public void Reset_matches_a_fresh_AppSettings_for_every_reflected_property()
    {
        var service = new SettingsService(_logging, SettingsPath);
        MutateAllProperties(service.Current);

        service.Reset();

        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var actual = JsonSerializer.Serialize(service.Current, options);
        var expected = JsonSerializer.Serialize(new AppSettings(), options);

        Assert.Equal(expected, actual);
    }

    /// <summary>Walks every public settable property of <paramref name="obj"/> and sets it to something
    /// other than its current value, recursing into nested settings classes and filling every list/dict
    /// with one non-default entry. Generic on purpose: it has no knowledge of which properties Reset() is
    /// supposed to cover, so it cannot be fooled by a new member added on only one side.</summary>
    private static void MutateAllProperties(object obj, HashSet<object>? visited = null)
    {
        visited ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!visited.Add(obj))
            return;

        foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead)
                continue;

            var type = prop.PropertyType;

            if (type == typeof(int))
            {
                if (prop.CanWrite)
                    prop.SetValue(obj, (int)prop.GetValue(obj)! + 1000);
            }
            else if (type == typeof(bool))
            {
                if (prop.CanWrite)
                    prop.SetValue(obj, !(bool)prop.GetValue(obj)!);
            }
            else if (type == typeof(double))
            {
                if (prop.CanWrite)
                    prop.SetValue(obj, (double)prop.GetValue(obj)! + 12345.5);
            }
            else if (type == typeof(string))
            {
                if (prop.CanWrite)
                    prop.SetValue(obj, "reset-guard-" + prop.Name);
            }
            else if (type == typeof(DateTimeOffset))
            {
                if (prop.CanWrite)
                    prop.SetValue(obj, DateTimeOffset.UtcNow.AddDays(3));
            }
            else if (typeof(IDictionary).IsAssignableFrom(type))
            {
                if (prop.GetValue(obj) is not IDictionary dict)
                    continue;

                dict.Clear();
                var args = type.GetGenericArguments();
                var keyType = args[0];
                var valueType = args[1];
                object key = keyType == typeof(string) ? "reset-guard-key" : Activator.CreateInstance(keyType)!;
                object? value = valueType == typeof(bool) ? true
                    : valueType == typeof(DateTimeOffset) ? DateTimeOffset.UtcNow.AddDays(3)
                    : valueType == typeof(string) ? "reset-guard-value"
                    : Activator.CreateInstance(valueType);
                if (value is not null && valueType.IsClass && valueType != typeof(string))
                    MutateAllProperties(value, visited);
                dict.Add(key, value);
            }
            else if (typeof(IList).IsAssignableFrom(type) && type != typeof(string))
            {
                if (prop.GetValue(obj) is not IList list)
                    continue;

                list.Clear();
                var elementType = type.GetGenericArguments()[0];
                object element = elementType == typeof(string) ? "reset-guard-item" : Activator.CreateInstance(elementType)!;
                list.Add(element);
            }
            else if (type.IsClass && type != typeof(object))
            {
                var value = prop.GetValue(obj);
                if (value is null && prop.CanWrite)
                {
                    value = Activator.CreateInstance(type);
                    prop.SetValue(obj, value);
                }

                if (value is not null)
                    MutateAllProperties(value, visited);
            }
        }
    }

    [Fact]
    public void A_trailing_comma_and_a_comment_still_load()
    {
        File.WriteAllText(SettingsPath, "{ // theme\n \"ui\": { \"theme\": \"dark\", }, }");

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("dark", service.Current.Ui.Theme);
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void A_garbage_file_is_kept_as_a_copy_and_defaults_are_returned()
    {
        const string garbage = "{ this is not json";
        File.WriteAllText(SettingsPath, garbage);

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("system", service.Current.Ui.Theme);
        var copy = Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
        Assert.Equal(garbage, File.ReadAllText(copy));
        Assert.Matches(@"settings\.json\.corrupt-\d{8}-\d{6}$", copy);
    }

    [Fact]
    public void An_oversized_file_is_kept_too()
    {
        File.WriteAllText(SettingsPath, new string('"', 6 * 1024 * 1024));

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("system", service.Current.Ui.Theme);
        var kept = Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
        Assert.Equal(6L * 1024 * 1024, new FileInfo(kept).Length);
    }

    [Fact]
    public void Only_the_three_newest_unreadable_copies_are_kept()
    {
        foreach (var stamp in new[] { "20200101-000001", "20200101-000002", "20200101-000003", "20200101-000004" })
            File.WriteAllText($"{SettingsPath}.corrupt-{stamp}", stamp);
        File.WriteAllText(SettingsPath, "not json");

        _ = new SettingsService(_logging, SettingsPath);

        var names = Directory.GetFiles(_tempDir, "settings.json.corrupt-*").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, names.Count);
        Assert.DoesNotContain("settings.json.corrupt-20200101-000001", names);
        Assert.DoesNotContain("settings.json.corrupt-20200101-000002", names);
        Assert.Contains("settings.json.corrupt-20200101-000004", names);
    }

    [Fact]
    public void A_locked_file_loads_defaults_and_no_save_overwrites_it()
    {
        const string original = "{\"ui\":{\"theme\":\"light\"}}";
        File.WriteAllText(SettingsPath, original);
        var before = File.ReadAllBytes(SettingsPath);

        SettingsService service;
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            service = new SettingsService(_logging, SettingsPath);

        Assert.Equal("system", service.Current.Ui.Theme);

        service.Current.Ui.Theme = "dark";
        service.Save();
        service.Flush();

        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void A_lock_that_is_released_within_the_retries_still_loads_the_file()
    {
        File.WriteAllText(SettingsPath, "{\"ui\":{\"theme\":\"light\"}}");

        var holder = File.Open(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = new Thread(() =>
        {
            Thread.Sleep(150);
            holder.Dispose();
        });
        release.Start();

        var service = new SettingsService(_logging, SettingsPath);
        release.Join();

        Assert.Equal("light", service.Current.Ui.Theme);
    }

    [Fact]
    public void A_valid_file_leaves_no_copy_behind()
    {
        File.WriteAllText(SettingsPath, "{\"ui\":{\"theme\":\"light\"}}");

        _ = new SettingsService(_logging, SettingsPath);

        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    /// <summary>
    /// The file's size is checked BEFORE it is opened, so an oversized settings.json is never buffered
    /// whole, it falls back to defaults exactly like a corrupt or unreadable file always has.
    /// </summary>
    [Fact]
    public void An_oversized_settings_file_falls_back_to_defaults_without_being_read_whole()
    {
        // One valid character repeated well past the limit, still fails on size before JSON ever runs.
        File.WriteAllText(SettingsPath, new string('"', 6 * 1024 * 1024));

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(1, service.Current.SchemaVersion);
        Assert.Equal("system", service.Current.Ui.Theme);
    }

    /// <summary>
    /// The whole point of <see cref="SettingsService.SaveDeferred"/>: a burst of UI clicks (window
    /// placement, a toggle) must collapse into ONE disk write, not one per call, and none of them may
    /// happen on the calling thread. <see cref="SettingsService.DiskWrites"/> is the test seam that counts
    /// real writes; if the coalescing regresses to a write-per-call, this catches it instantly.
    /// </summary>
    [Fact]
    public void SaveDeferred_coalesces_a_burst_of_calls_into_a_single_write()
    {
        var service = new SettingsService(_logging, SettingsPath);

        for (var i = 0; i < 20; i++)
        {
            service.Current.Ui.Theme = i % 2 == 0 ? "dark" : "light";
            service.SaveDeferred();
        }

        // Nothing has actually reached disk yet: the write only happens after the delay elapses.
        Assert.False(File.Exists(SettingsPath));
        Assert.Equal(0, service.DiskWrites);

        service.Flush();

        Assert.Equal(1, service.DiskWrites);
        Assert.True(File.Exists(SettingsPath));
        Assert.Contains("\"light\"", File.ReadAllText(SettingsPath));
    }

    /// <summary>A queued save with nothing pending must not write again, <see cref="SettingsService.Flush"/>
    /// is called unconditionally from the exit path and before every elevated job.</summary>
    [Fact]
    public void Flush_without_a_pending_save_does_nothing()
    {
        var service = new SettingsService(_logging, SettingsPath);

        service.Flush();

        Assert.Equal(0, service.DiskWrites);
        Assert.False(File.Exists(SettingsPath));
    }

    /// <summary>The scenario the whole feature exists for: app exit must not lose a change that was still
    /// sitting in the 250 ms coalescing delay.</summary>
    [Fact]
    public void A_deferred_save_still_pending_at_exit_is_flushed()
    {
        var service = new SettingsService(_logging, SettingsPath);
        service.Current.Autostart = true;
        service.SaveDeferred();

        service.Flush();

        var reloaded = new SettingsService(_logging, SettingsPath);
        Assert.True(reloaded.Current.Autostart);
    }

    /// <summary>
    /// SaveDeferred's write runs on a thread-pool timer callback (OnSaveTimer) while the UI thread keeps
    /// mutating Current's collections (adding an exclude, snoozing a package, ...). That race used to throw
    /// InvalidOperationException ("Collection was modified") straight out of the enumerator JsonSerializer
    /// uses, unhandled on a thread-pool thread - fatal, it takes the whole process down. OnSaveTimer is
    /// invoked directly (there being no WPF dispatcher in this headless test to marshal the real fix onto)
    /// so the race is forced deterministically rather than left to timer scheduling luck.
    /// </summary>
    [Fact]
    public async Task Concurrent_mutation_during_a_deferred_save_never_throws_out_of_the_timer_callback()
    {
        var service = new SettingsService(_logging, SettingsPath);
        var onSaveTimer = typeof(SettingsService).GetMethod("OnSaveTimer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var savePendingField = typeof(SettingsService).GetField("_savePending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pendingLockField = typeof(SettingsService).GetField("_pendingLock", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pendingLock = pendingLockField.GetValue(service)!;

        using var stop = new CancellationTokenSource();
        // ONE mutator thread, matching production exactly: only the UI thread ever touches Current's
        // collections, single-threaded, the same way the settings window's own source toggles do. A
        // Dictionary throws on the very first structural change seen during enumeration (its own version
        // check is far stricter than List<T>'s), which is what makes Sources reproduce this race reliably
        // rather than depending on precise timing.
        var mutator = Task.Run(() =>
        {
            // Add then Remove, never just an in-place value update: a Dictionary's version only advances
            // on a STRUCTURAL change, so re-assigning an already-present key (this test's first attempt)
            // never trips the enumerator's check at all and the race window silently vanishes.
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                service.Current.Sources[$"src-{i}"] = true;
                if (i >= 20)
                    service.Current.Sources.Remove($"src-{i - 20}");
                i++;
            }
        });

        Exception? escaped = null;
        try
        {
            for (var i = 0; i < 2000 && escaped is null; i++)
            {
                lock (pendingLock)
                    savePendingField.SetValue(service, true);

                try
                {
                    onSaveTimer.Invoke(service, null);
                }
                catch (TargetInvocationException ex)
                {
                    escaped = ex.InnerException;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await mutator;
        }

        Assert.Null(escaped);
    }

    // ---- Registry-backup default: profile root -> %APPDATA%\UltimateUpdater\registry-backups ----

    /// <summary>
    /// The old default put exported .reg files directly in the user's home directory. A settings.json
    /// saved under that old default must be rewritten to the new one on load, so an existing install picks
    /// up the safer location automatically rather than needing the user to notice and change it by hand.
    /// </summary>
    [Fact]
    public void Loading_settings_rewrites_the_old_profile_root_default_to_the_new_app_data_default()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(SettingsPath, BuildSettingsJson(profile));

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(RegistryCleanupSettings.DefaultBackupFolder, service.Current.RegistryCleanup.BackupFolder);
    }

    /// <summary>
    /// The app itself was renamed from Windows Updater to Ultimate Updater, and the roaming data folder
    /// followed. A settings.json saved under the old app name's default backup folder must be rewritten to
    /// the current default on load, the same way the older profile-root migration above already works.
    /// </summary>
    [Fact]
    public void Loading_settings_rewrites_the_old_app_name_default_to_the_current_app_data_default()
    {
        File.WriteAllText(SettingsPath, BuildSettingsJson(RegistryCleanupSettings.LegacyDefaultBackupFolder));

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(RegistryCleanupSettings.DefaultBackupFolder, service.Current.RegistryCleanup.BackupFolder);
    }

    /// <summary>A folder the user picked themselves, even one that happens to sit inside the profile tree,
    /// must never be silently overwritten by the migration above, only an EXACT match of the old default is
    /// rewritten.</summary>
    [Fact]
    public void A_user_chosen_registry_backup_folder_survives_loading()
    {
        var chosen = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "MyRegBackups");
        File.WriteAllText(SettingsPath, BuildSettingsJson(chosen));

        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(chosen, service.Current.RegistryCleanup.BackupFolder);
    }

    /// <summary>A brand-new settings file (no migration involved at all) must already carry the new default,
    /// not the old profile-root one.</summary>
    [Fact]
    public void A_new_settings_file_already_carries_the_new_registry_backup_default()
    {
        var service = new SettingsService(_logging, SettingsPath);

        Assert.Equal(RegistryCleanupSettings.DefaultBackupFolder, service.Current.RegistryCleanup.BackupFolder);
    }

    /// <summary>A minimal settings.json naming only the one field under test; <c>Normalize</c> fills in
    /// every other section's defaults on load.</summary>
    private static string BuildSettingsJson(string registryBackupFolder)
    {
        var escaped = registryBackupFolder.Replace("\\", "\\\\");
        return "{\"registryCleanup\":{\"backupFolder\":\"" + escaped + "\",\"retentionCount\":5}}";
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
