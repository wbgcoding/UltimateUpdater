using System.Reflection;
using System.Text.RegularExpressions;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// WPF resolves binding paths at runtime and says nothing when one does not exist, the control just
/// stays empty. Renaming a view-model property and missing one XAML reference therefore ships a blank
/// button with a green build. Every binding in every window is checked against the real types here.
/// </summary>
public class XamlBindingTests
{
    private static readonly string ViewsDirectory = FindViewsDirectory();

    /// <summary>Supplied by WPF itself on group headers, not by any view model.</summary>
    private static readonly string[] ProvidedByWpf = ["Name", "ItemCount", "Items"];

    [Fact]
    public void MainWindow_bindings_all_exist() =>
        AssertBindingsResolve(
            "MainWindow.xaml",
            typeof(MainViewModel),
            typeof(UpdateRowViewModel),
            typeof(ProviderStatusViewModel));

    [Fact]
    public void SettingsWindow_bindings_all_exist() =>
        AssertBindingsResolve(
            "SettingsWindow.xaml",
            typeof(SettingsViewModel),
            typeof(SettingsViewModel.SourceToggle),
            typeof(SettingsViewModel.ExcludedPathRow),
            typeof(SettingsViewModel.NetworkTarget));

    [Fact]
    public void MessageDialogWindow_bindings_all_exist() =>
        AssertBindingsResolve("MessageDialogWindow.xaml", typeof(MessageDialogViewModel));

    [Fact]
    public void CleanupWindow_bindings_all_exist() =>
        AssertBindingsResolve(
            "CleanupWindow.xaml",
            typeof(CleanupViewModel),
            typeof(CleanupGroupViewModel),
            typeof(CleanupItemViewModel),
            typeof(LeftoverCandidateViewModel),
            typeof(DriverDeviceCandidateViewModel),
            typeof(DriverPackageCandidateViewModel),
            typeof(PythonVersionCandidateViewModel),
            typeof(CleanupEntryViewModel),
            typeof(CleanupTopAreaViewModel),
            typeof(CleanupHistoryRowViewModel),
            typeof(CleanupHistoryAreaViewModel));

    [Fact]
    public void CleanupHistoryWindow_bindings_all_exist() =>
        AssertBindingsResolve(
            "CleanupHistoryWindow.xaml",
            typeof(CleanupViewModel),
            typeof(CleanupHistoryRowViewModel),
            typeof(CleanupHistoryAreaViewModel),
            typeof(CleanupHistoryTopCategoryViewModel));

    [Fact]
    public void HistoryWindow_bindings_all_exist() =>
        AssertBindingsResolve(
            "HistoryWindow.xaml",
            typeof(HistoryViewModel),
            typeof(HistoryRowViewModel),
            typeof(HistorySourceFilter));

    private static void AssertBindingsResolve(string fileName, params Type[] candidates)
    {
        var xaml = File.ReadAllText(Path.Combine(ViewsDirectory, fileName));

        var known = candidates
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(property => property.Name)
            .Concat(ProvidedByWpf)
            .ToHashSet(StringComparer.Ordinal);

        // The first positional segment of each binding's path, checked against the view models. Skipped:
        // "{Binding Loc[Key]}" (localization tests cover it); bindings with a RelativeSource or
        // ElementName (they target a control, a ControlTemplate's TemplatedParent, an ancestor ComboBox,
        // a named element, so their path is a control property like SelectionBoxItem/IsDropDownOpen, not
        // a view-model member); and named binding properties (Converter=, Mode=…), which are not paths.
        var bound = new List<string>();
        foreach (Match match in Regex.Matches(xaml, @"\{Binding\s+((?:\{[^}]*\}|[^{}])*)\}"))
        {
            var content = match.Groups[1].Value;
            if (content.Contains("RelativeSource") || content.Contains("ElementName"))
                continue;

            var firstArg = content.Split(',')[0].Trim();
            if (firstArg.StartsWith("Path=", StringComparison.Ordinal))
                firstArg = firstArg[5..].Trim();

            // a named property ("Converter={…}") or an empty path is not a member reference
            if (firstArg.Length == 0 || firstArg.Contains('='))
                continue;

            var name = firstArg.Split('.', '[')[0].Trim();
            if (name != "Loc" && Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                bound.Add(name);
        }

        bound = bound.Distinct().ToList();

        Assert.NotEmpty(bound);

        var missing = bound.Where(name => !known.Contains(name)).Order().ToList();

        Assert.True(
            missing.Count == 0,
            $"{fileName} binds to members that no view model defines: {string.Join(", ", missing)}");
    }

    private static string FindViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "UltimateUpdater", "Views");
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Views directory from the test output path.");
    }
}
