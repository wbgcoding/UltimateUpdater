namespace UltimateUpdater.ViewModels.Common;

/// <summary>Joins a label and its value the way the current language writes it (French puts a space before the colon, Japanese and Chinese use the full-width one), so a line built in code reads like the labels in the resource files.</summary>
internal static class LabelValue
{
    public static string Join(string label, string? value) =>
        string.Format(LocalizationService.Instance["LabelValue"], label, value);

    /// <summary>The separator between the items of a list in the current language.</summary>
    public static string ListSeparator => LocalizationService.Instance["ListSeparator"];
}
