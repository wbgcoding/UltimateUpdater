using CommunityToolkit.Mvvm.ComponentModel;

namespace UltimateUpdater.ViewModels.Cleanup;

// The "show all categories" expander, revealing the targets whose CleanupTarget.DefaultVisible is false.
public partial class CleanupViewModel
{
    /// <summary>Off by default, so a fresh dialog shows only the categories worth a look. Not settings-backed: a hidden category is one the app judged not worth a default look, not a user preference.</summary>
    [ObservableProperty]
    private bool showAllCategories;

    /// <summary>How many categories <c>CleanupTarget.DefaultVisible = false</c> hides. <see cref="Items"/> is filled once and never reshuffled, so <see cref="OnHiddenCategoriesChanged"/> re-raises this when it is set.</summary>
    public int HiddenCategoriesCount => Items.Count(i => !i.Target.DefaultVisible);

    /// <summary>The expander only makes sense to show once there is at least one hidden category.</summary>
    public bool HasHiddenCategories => HiddenCategoriesCount > 0;

    /// <summary>"Show all categories (N hidden)", built as one text rather than two bound pieces (see <see cref="CleanupItemViewModel.RefreshLocalizedText"/>).</summary>
    public string ShowAllCategoriesText => string.Format(Loc["CleanupShowAllCategories"], HiddenCategoriesCount);

    /// <summary>Everything above is computed from <see cref="Items"/>; raised whenever it is replaced.</summary>
    private void OnHiddenCategoriesChanged()
    {
        OnPropertyChanged(nameof(HiddenCategoriesCount));
        OnPropertyChanged(nameof(HasHiddenCategories));
        OnPropertyChanged(nameof(ShowAllCategoriesText));
    }
}
