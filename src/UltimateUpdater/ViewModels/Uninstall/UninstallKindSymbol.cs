using UltimateUpdater.Services.Uninstall;
using Wpf.Ui.Controls;

namespace UltimateUpdater.ViewModels.Uninstall;

/// <summary>The Fluent symbol a row shows while its icon is not resolved yet, or when it has none: one per kind of entry.</summary>
internal static class UninstallKindSymbol
{
    internal static SymbolRegular For(UninstallEntryKind kind) => kind switch
    {
        UninstallEntryKind.Package => SymbolRegular.Box24,
        UninstallEntryKind.Driver => SymbolRegular.DeveloperBoard24,
        UninstallEntryKind.WindowsUpdate => SymbolRegular.ArrowSync24,
        _ => SymbolRegular.AppGeneric24,
    };
}
