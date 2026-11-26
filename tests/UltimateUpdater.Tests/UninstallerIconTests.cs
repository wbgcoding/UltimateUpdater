using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using Wpf.Ui.Controls;

namespace UltimateUpdater.Tests;

/// <summary>The row icons: the symbol each kind falls back to, and that the icon is read off the calling thread.</summary>
[Collection("UiLanguage")]
public class UninstallerIconTests
{
    private static BitmapSource Pixel()
    {
        var icon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 120, 212, 255 }, 4);
        icon.Freeze();
        return icon;
    }

    [Theory]
    [InlineData(UninstallEntryKind.Program, SymbolRegular.AppGeneric24)]
    [InlineData(UninstallEntryKind.StoreApp, SymbolRegular.AppGeneric24)]
    [InlineData(UninstallEntryKind.Package, SymbolRegular.Box24)]
    [InlineData(UninstallEntryKind.Driver, SymbolRegular.DeveloperBoard24)]
    [InlineData(UninstallEntryKind.WindowsUpdate, SymbolRegular.ArrowSync24)]
    public void Every_kind_falls_back_to_its_own_symbol(UninstallEntryKind kind, SymbolRegular expected) =>
        Assert.Equal(expected, UninstallKindSymbol.For(kind));

    [Fact]
    public async Task The_icon_is_read_off_the_calling_thread_and_handed_to_the_row_through_the_poster()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var readOn = new System.Collections.Concurrent.ConcurrentBag<int>();
        var backend = new UninstallerViewModelTests.FakeBackend(UninstallerViewModelTests.Listing("Alpha"))
        {
            Icon = _ =>
            {
                readOn.Add(Environment.CurrentManagedThreadId);
                return Pixel();
            },
        };
        var viewModel = new UninstallerViewModel(backend, new LoggingService());
        var posted = new List<int>();
        viewModel.PostToUi = action =>
        {
            posted.Add(Environment.CurrentManagedThreadId);
            action();
        };

        viewModel.SetListings(backend.Current);
        await viewModel.IconsLoaded;

        var row = viewModel.Sections.SelectMany(section => section.Rows).Single();
        Assert.True(row.HasIcon);
        Assert.NotNull(row.Icon);
        Assert.Single(readOn);
        Assert.DoesNotContain(callerThread, readOn);
        Assert.Single(posted);
    }

    [Fact]
    public async Task A_row_without_an_icon_keeps_its_symbol_and_the_others_still_load()
    {
        var backend = new UninstallerViewModelTests.FakeBackend(
            UninstallerViewModelTests.Listing("Alpha"),
            UninstallerViewModelTests.Listing("Beta"))
        {
            Icon = entry => entry.Name == "Beta" ? null : Pixel(),
        };
        var viewModel = new UninstallerViewModel(backend, new LoggingService()) { PostToUi = action => action() };

        viewModel.SetListings(backend.Current);
        await viewModel.IconsLoaded;

        var rows = viewModel.Sections.SelectMany(section => section.Rows).ToDictionary(row => row.Name);
        Assert.True(rows["Alpha"].HasIcon);
        Assert.False(rows["Beta"].HasIcon);
    }

    [Fact]
    public async Task A_failing_icon_read_is_logged_and_does_not_stop_the_other_rows()
    {
        var lines = new List<string>();
        var backend = new UninstallerViewModelTests.FakeBackend(
            UninstallerViewModelTests.Listing("Alpha"),
            UninstallerViewModelTests.Listing("Beta"))
        {
            Icon = entry => entry.Name == "Alpha" ? throw new IOException("locked") : Pixel(),
        };
        var viewModel = new UninstallerViewModel(backend, new LoggingService(lines.Add)) { PostToUi = action => action() };

        viewModel.SetListings(backend.Current);
        await viewModel.IconsLoaded;

        var rows = viewModel.Sections.SelectMany(section => section.Rows).ToDictionary(row => row.Name);
        Assert.False(rows["Alpha"].HasIcon);
        Assert.True(rows["Beta"].HasIcon);
        Assert.Contains(lines, line => line.Contains("Alpha") && line.Contains("locked"));
    }

    [Fact]
    public async Task Only_programs_and_store_apps_are_read_and_each_row_only_once()
    {
        var reads = new System.Collections.Concurrent.ConcurrentBag<string>();
        var backend = new UninstallerViewModelTests.FakeBackend(
            UninstallerViewModelTests.Listing("Program"),
            UninstallerViewModelTests.Listing("Package", kind: UninstallEntryKind.Package),
            UninstallerViewModelTests.Listing("Driver", kind: UninstallEntryKind.Driver),
            UninstallerViewModelTests.Listing("Update", kind: UninstallEntryKind.WindowsUpdate))
        {
            Icon = entry =>
            {
                reads.Add(entry.Name);
                return Pixel();
            },
        };
        var viewModel = new UninstallerViewModel(backend, new LoggingService()) { PostToUi = action => action() };

        viewModel.SetListings(backend.Current);
        await viewModel.IconsLoaded;
        viewModel.ResolveIcons();
        await viewModel.IconsLoaded;

        Assert.Equal(["Program"], reads.ToArray());
    }
}
