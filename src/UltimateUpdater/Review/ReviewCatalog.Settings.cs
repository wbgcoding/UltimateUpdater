#if DEBUG
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateUpdater.Views.Settings;

namespace UltimateUpdater.Review;

internal static partial class ReviewCatalog
{
    private const string SettingsArea = "settings";

    /// <summary>Navigation entry tag, and the id suffix a surface for that section gets.</summary>
    private static readonly (string Tag, string Id)[] SettingsSections =
    [
        ("ScanHeader", "scan"),
        ("InstallHeader", "install"),
        ("CleanupHeader", "cleanup"),
        ("DataHeader", "data"),
        ("NotifyHeader", "notify"),
        ("AppearanceHeader", "appearance"),
        ("StartupHeader", "startup"),
        ("UpdatesHeader", "updates"),
        ("AdvancedHeader", "advanced"),
        ("AboutExpander", "about"),
    ];

    private static void AddSettings(List<ReviewSurface> list)
    {
        Add(list, "settings.top", SettingsArea, "Settings window as it opens", s => Task.FromResult(SettingsScene(s, null)));

        foreach (var (tag, id) in SettingsSections)
            Add(list, $"settings.{id}", SettingsArea, $"Settings, section {id}, every card opened", s => Task.FromResult(SettingsScene(s, tag)));
    }

    private static ReviewScene SettingsScene(ReviewSession session, string? sectionTag)
    {
        var viewModel = session.NewSettingsViewModel(out _);
        var window = new SettingsWindow(viewModel);
        var scene = new ReviewScene { Window = window, IsSettings = true };
        if (sectionTag is null)
            return scene;

        scene.AfterShown = async () =>
        {
            foreach (var expander in Descendants<Expander>(window).ToList())
                expander.IsExpanded = true;

            await ReviewPump.SettleAsync(900);
            var entry = window.SettingsNav.Items.OfType<ListBoxItem>().First(i => (string)i.Tag == sectionTag);
            window.ScrollToSection(entry);
            await ReviewPump.SettleAsync(900);
        };
        return scene;
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var nested in Descendants<T>(child))
                yield return nested;
        }
    }
}
#endif
