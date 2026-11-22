using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UltimateUpdater.Services.Formatting;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>One leftover of a removed program (a folder, a registry key or a shortcut) with its checkbox.</summary>
public sealed partial class UninstallLeftoverItemViewModel(LeftoverItem item, bool isSelected, Action? selectionChanged) : ObservableObject
{
    public LeftoverItem Item { get; } = item;

    /// <summary>The identity a rescan matches a ticked or unticked item by.</summary>
    internal string Key => $"{Item.Kind}|{Item.Path}";

    public string Path => Item.Path;

    public string SizeText => Item.SizeBytes > 0 ? ByteSize.Format(Item.SizeBytes) : string.Empty;

    public string KindText => LocalizationService.Instance[Item.Kind switch
    {
        LeftoverKind.RegistryKey => "UninstallerLeftoverRegistry",
        LeftoverKind.Shortcut => "UninstallerLeftoverShortcut",
        _ => "UninstallerLeftoverFolder",
    }];

    [ObservableProperty]
    private bool isSelected = isSelected;

    partial void OnIsSelectedChanged(bool value) => selectionChanged?.Invoke();

    public void RefreshText() => OnPropertyChanged(nameof(KindText));
}

/// <summary>The leftovers of one removed program, listed under its name.</summary>
public sealed class UninstallLeftoverGroupViewModel(InstalledProgramEntry entry, IEnumerable<UninstallLeftoverItemViewModel> items)
{
    public InstalledProgramEntry Entry { get; } = entry;

    public string Name => Entry.Name;

    public ObservableCollection<UninstallLeftoverItemViewModel> Items { get; } = [.. items];
}
