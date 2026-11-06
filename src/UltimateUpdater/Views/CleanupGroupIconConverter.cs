using System.Globalization;
using System.Windows.Data;
using UltimateUpdater.Services.Cleanup;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Views;

/// <summary>
/// The Fluent glyph that stands for a cleanup section, so the area being cleaned right now can be told
/// apart at a glance without reading its name. Keyed by the language-independent section key.
/// </summary>
public sealed class CleanupGroupIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is CleanupGroupKey key ? SymbolFor(key) : SymbolRegular.Broom24;

    internal static SymbolRegular SymbolFor(CleanupGroupKey key) => key switch
    {
        CleanupGroupKey.TempFiles => SymbolRegular.Document24,
        CleanupGroupKey.Caches => SymbolRegular.Database24,
        CleanupGroupKey.WindowsMaintenance => SymbolRegular.WindowWrench24,
        CleanupGroupKey.RecycleBin => SymbolRegular.Delete24,
        CleanupGroupKey.SystemRepair => SymbolRegular.Wrench24,
        CleanupGroupKey.Registry => SymbolRegular.Settings24,
        CleanupGroupKey.Drivers => SymbolRegular.HardDrive24,
        _ => SymbolRegular.Broom24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
