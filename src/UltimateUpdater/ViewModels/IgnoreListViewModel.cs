using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Backs the ignore-list management dialog: every <see cref="Models.Settings.AppSettings.Excludes"/>
/// entry, optionally resolved to a display name from a caller-supplied lookup keyed the same "source:id"
/// way <c>MainViewModel.PackageKey</c> builds its own keys. A key with no matching name (nothing was
/// supplied, or the package is not in whatever snapshot the caller took) is still listed, per the item's
/// own spec, just with <see cref="IgnoreListEntryViewModel.IsKnownPackage"/> false so the view can style
/// it as muted rather than silently dropping it. No dedicated copy key exists for a "not installed right
/// now" sentence (the item's Copy section names only four keys, none of them this), so the distinction is
/// visual only, not textual.
/// </summary>
public sealed partial class IgnoreListViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IReadOnlyDictionary<string, string> _packageNames;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<IgnoreListEntryViewModel> Entries { get; } = [];

    public bool HasEntries => Entries.Count > 0;

    public bool HasNoEntries => Entries.Count == 0;

    /// <summary>Raised after an entry (or all of them) was removed, so the table can re-read the ignore list
    /// at once instead of when the settings window is eventually closed.</summary>
    public event Action? Changed;

    public IgnoreListViewModel(SettingsService settings, IReadOnlyDictionary<string, string>? packageNames = null)
    {
        _settings = settings;
        _packageNames = packageNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Rebuild();
    }

    /// <summary>Rebuilt from the live settings list every time it changes, same "read the source of truth
    /// back" discipline <see cref="SettingsViewModel.RebuildExcludedPaths"/> already uses for its own list.</summary>
    private void Rebuild()
    {
        Entries.Clear();

        foreach (var key in _settings.Current.Excludes.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            // The version follows the LAST "@", and only one past the "source:" prefix: a scoped npm
            // package ("npm:@angular/cli") starts its own name with one.
            var atIndex = key.LastIndexOf('@');
            if (atIndex <= key.IndexOf(':') + 1)
                atIndex = -1;
            var packageKey = atIndex >= 0 ? key[..atIndex] : key;
            var version = atIndex >= 0 ? key[(atIndex + 1)..] : null;
            var known = _packageNames.TryGetValue(packageKey, out var name);

            Entries.Add(new IgnoreListEntryViewModel(key, known ? name! : packageKey, version, known, Remove));
        }

        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(HasNoEntries));
    }

    private void Remove(string key)
    {
        if (!_settings.Current.Excludes.Remove(key))
            return;

        _settings.SaveDeferred();
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>The confirmation says what removing does (the entries come back on the next search) and
    /// how many, with the button keeping its own short label.</summary>
    internal (string Headline, string Body, string ConfirmText) BuildRemoveAllConfirmation() =>
        (string.Format(Loc["IgnoreListRemoveAllHeadline"], Entries.Count),
            Loc["IgnoreListRemoveAllBody"],
            Loc["IgnoreListRemoveAll"]);

    [RelayCommand]
    private void RemoveAll()
    {
        if (Entries.Count == 0)
            return;

        var (headline, body, confirmText) = BuildRemoveAllConfirmation();
        if (!Dialogs.Confirm(headline, body, confirmText: confirmText, destructive: true))
            return;

        _settings.Current.Excludes.Clear();
        _settings.SaveDeferred();
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>One row: a package/version key from Excludes plus its resolved display name. Same
    /// callback-into-the-parent shape as <see cref="SettingsViewModel.ExcludedPathRow"/>.</summary>
    public sealed partial class IgnoreListEntryViewModel : ObservableObject
    {
        private readonly Action<string> _onRemove;

        public string Key { get; }

        public string Name { get; }

        private readonly string? _version;

        public bool IsKnownPackage { get; }

        /// <summary>The exact version this entry covers, or the localized "all versions" for a
        /// whole-package entry.</summary>
        public string VersionText => _version ?? LocalizationService.Instance["IgnoreListAllVersions"];

        public IgnoreListEntryViewModel(string key, string name, string? version, bool isKnownPackage, Action<string> onRemove)
        {
            Key = key;
            Name = name;
            _version = version;
            IsKnownPackage = isKnownPackage;
            _onRemove = onRemove;
        }

        [RelayCommand]
        private void Remove() => _onRemove(Key);
    }
}
