using CommunityToolkit.Mvvm.ComponentModel;

namespace UltimateUpdater.ViewModels;

// The "show all categories" expander, revealing the targets whose CleanupTarget.DefaultVisible
// is false. A NEW partial file rather than an addition to CleanupViewModel.cs, so this
// file's own state never touches a file being edited elsewhere at the same time.
public partial class CleanupViewModel
{
    /// <summary>
    /// Off by default: a fresh dialog only shows the categories worth looking at. Deliberately NOT
    /// settings-backed: this does not ask itself to survive a
    /// restart, and every hidden category is one this app itself judged not worth a default look, not a
    /// per-user preference.
    /// </summary>
    [ObservableProperty]
    private bool showAllCategories;

    /// <summary>
    /// How many categories <c>CleanupTarget.DefaultVisible = false</c> hides right now. <see cref="Items"/>
    /// starts empty and is filled once the catalogue has been built in the background, never reshuffled
    /// after that, so <see cref="OnHiddenCategoriesChanged"/> re-raises this when it is set.
    /// </summary>
    public int HiddenCategoriesCount => Items.Count(i => !i.Target.DefaultVisible);

    /// <summary>The expander only makes sense to show once there is at least one hidden category.</summary>
    public bool HasHiddenCategories => HiddenCategoriesCount > 0;

    /// <summary>"Show all categories (N hidden)": built as text rather than bound as two pieces, same
    /// reasoning <see cref="CleanupItemViewModel.RefreshLocalizedText"/> already documents for every other
    /// formatted string in this dialog.</summary>
    public string ShowAllCategoriesText => string.Format(Loc["CleanupShowAllCategories"], HiddenCategoriesCount);

    /// <summary>Everything above is computed from <see cref="Items"/>; raised whenever it is replaced.</summary>
    private void OnHiddenCategoriesChanged()
    {
        OnPropertyChanged(nameof(HiddenCategoriesCount));
        OnPropertyChanged(nameof(HasHiddenCategories));
        OnPropertyChanged(nameof(ShowAllCategoriesText));
    }
}
