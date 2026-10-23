using UltimateUpdater.Services;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>The ignore-list management dialog reads straight from
/// <see cref="Models.Settings.AppSettings.Excludes"/> and writes straight back, the same round-trip
/// discipline <see cref="SettingsViewModel"/>'s own ExcludedPaths list already follows.</summary>
[Collection("UiLanguage")]
public class IgnoreListViewModelTests
{
    private static SettingsService CreateSettings(string prefix, out string path)
    {
        path = System.IO.Path.Combine(System.IO.Directory.CreateTempSubdirectory(prefix).FullName, "settings.json");
        return new SettingsService(new LoggingService(), path);
    }

    /// <summary>A scoped npm package starts its name with "@": that one is part of the name, only a later
    /// "@" starts the version.</summary>
    [Fact]
    public void A_scoped_npm_package_keeps_its_name_and_version_apart()
    {
        Services.LocalizationService.Instance.Apply("en");

        var settings = CreateSettings("wu-ignorelist-scoped-", out _);
        settings.Current.Excludes.AddRange(["npm:@angular/cli", "npm:@vue/cli@5.0.8"]);
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["npm:@angular/cli"] = "@angular/cli",
            ["npm:@vue/cli"] = "@vue/cli",
        };

        var viewModel = new IgnoreListViewModel(settings, names);

        var angular = viewModel.Entries.Single(e => e.Key == "npm:@angular/cli");
        Assert.Equal("@angular/cli", angular.Name);
        Assert.Equal("all versions", angular.VersionText);

        var vue = viewModel.Entries.Single(e => e.Key == "npm:@vue/cli@5.0.8");
        Assert.Equal("@vue/cli", vue.Name);
        Assert.Equal("5.0.8", vue.VersionText);
    }

    /// <summary>A whole-package key ("source:id") resolves to the snapshot's name and shows "all versions";
    /// a per-version key ("source:id@version") resolves to the same name but shows the real version; a key
    /// whose package is not in the snapshot falls back to the raw key and is flagged unknown.</summary>
    [Fact]
    public void The_list_is_built_from_settings_and_resolves_known_names()
    {
        Services.LocalizationService.Instance.Apply("en");

        var settings = CreateSettings("wu-ignorelist-build-", out _);
        settings.Current.Excludes.AddRange(["winget:git", "npm:eslint@9.0.0", "scoop:ghost-tool"]);

        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["winget:git"] = "Git",
            ["npm:eslint"] = "ESLint",
        };

        var viewModel = new IgnoreListViewModel(settings, names);

        Assert.Equal(3, viewModel.Entries.Count);
        Assert.True(viewModel.HasEntries);
        Assert.False(viewModel.HasNoEntries);

        var gitEntry = viewModel.Entries.Single(e => e.Key == "winget:git");
        Assert.Equal("Git", gitEntry.Name);
        Assert.True(gitEntry.IsKnownPackage);
        Assert.Equal("all versions", gitEntry.VersionText);

        var eslintEntry = viewModel.Entries.Single(e => e.Key == "npm:eslint@9.0.0");
        Assert.Equal("ESLint", eslintEntry.Name);
        Assert.True(eslintEntry.IsKnownPackage);
        Assert.Equal("9.0.0", eslintEntry.VersionText);

        var unknownEntry = viewModel.Entries.Single(e => e.Key == "scoop:ghost-tool");
        Assert.Equal("scoop:ghost-tool", unknownEntry.Name);
        Assert.False(unknownEntry.IsKnownPackage);
    }

    [Fact]
    public void An_empty_ignore_list_reports_no_entries()
    {
        var settings = CreateSettings("wu-ignorelist-empty-", out _);
        var viewModel = new IgnoreListViewModel(settings);

        Assert.Empty(viewModel.Entries);
        Assert.False(viewModel.HasEntries);
        Assert.True(viewModel.HasNoEntries);
    }

    /// <summary>Removing one row writes the exact same key back out of Excludes and drops just that row.</summary>
    [Fact]
    public void Removing_one_entry_updates_settings_and_the_list()
    {
        var settings = CreateSettings("wu-ignorelist-remove-", out var path);
        settings.Current.Excludes.AddRange(["winget:git", "npm:eslint@9.0.0"]);
        settings.Save();

        var viewModel = new IgnoreListViewModel(settings);
        var entry = viewModel.Entries.Single(e => e.Key == "winget:git");
        entry.RemoveCommand.Execute(null);

        Assert.Single(viewModel.Entries);
        Assert.DoesNotContain("winget:git", settings.Current.Excludes);
        Assert.Contains("npm:eslint@9.0.0", settings.Current.Excludes);

        settings.Flush();
        var reloaded = new SettingsService(new LoggingService(), path);
        Assert.DoesNotContain("winget:git", reloaded.Current.Excludes);
    }

    [Fact]
    public void The_remove_all_confirmation_names_the_count_and_says_what_happens()
    {
        var settings = CreateSettings("wu-ignorelist-removeall-text-", out _);
        settings.Current.Excludes.AddRange(["winget:git", "npm:eslint@9.0.0", "pip:rich"]);

        var viewModel = new IgnoreListViewModel(settings);
        var (headline, body, confirmText) = viewModel.BuildRemoveAllConfirmation();

        var loc = LocalizationService.Instance;
        Assert.Equal(string.Format(loc["IgnoreListRemoveAllHeadline"], 3), headline);
        Assert.Contains("3", headline);
        Assert.Equal(loc["IgnoreListRemoveAllBody"], body);
        Assert.NotEmpty(body);
        Assert.Equal(loc["IgnoreListRemoveAll"], confirmText);
    }

    /// <summary>Same fail-closed proof as <c>SettingsViewModelTests.Releasing_all_excluded_paths_is_declined_without_confirmation</c>:
    /// Dialogs.Confirm always declines under SuppressInteractiveDialogs, so this proves the remove-all
    /// action is actually gated behind it rather than clearing regardless.</summary>
    [Fact]
    public void Removing_all_entries_is_declined_without_confirmation()
    {
        Dialogs.SuppressInteractiveDialogs = true;

        var settings = CreateSettings("wu-ignorelist-removeall-", out _);
        settings.Current.Excludes.AddRange(["winget:git", "npm:eslint@9.0.0"]);
        settings.Save();

        var viewModel = new IgnoreListViewModel(settings);
        viewModel.RemoveAllCommand.Execute(null);

        Assert.Equal(2, viewModel.Entries.Count);
        Assert.Equal(2, settings.Current.Excludes.Count);
    }
}
