using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Views;

/// <summary>
/// A modern Fluent glyph for each table category group, chosen from the group's language-independent
/// <see cref="ViewModels.UpdateRowViewModel.CategoryKey"/> (the manual groups' own keys plus each
/// <see cref="Models.UpdateCategory"/> name), so a rename of a group's displayed text never changes its icon.
/// </summary>
public sealed class CategoryIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "Software" => SymbolRegular.Apps24,
            "DeveloperTools" => SymbolRegular.Code24,
            "SystemAndDrivers" => SymbolRegular.Desktop24,
            "Games" => SymbolRegular.Games24,
            "Store" => SymbolRegular.StoreMicrosoft24,
            "ManualUpdate" or "GitHubManual" => SymbolRegular.WindowArrowUp20,
            _ => SymbolRegular.Box24,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
