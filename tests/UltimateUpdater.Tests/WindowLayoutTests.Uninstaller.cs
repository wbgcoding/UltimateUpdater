using UltimateUpdater.Models.Settings;
using System.Windows;
using System.Windows.Controls;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using UltimateUpdater.Views.Uninstall;

namespace UltimateUpdater.Tests;

public partial class WindowLayoutTests
{
    private sealed class StaticUninstallerBackend(IReadOnlyList<UninstallListing> listings) : IUninstallerBackend
    {
        public Task<IReadOnlyList<UninstallListing>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(listings);

        public Task<IReadOnlyList<UninstallListing>?> ListUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UninstallListing>?>([]);

        public UninstallRunner CreateRunner(Func<InstalledProgramEntry, UninstallPlan> plan) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<UninstallLeftoverGroup>> FindLeftoversAsync(
            IReadOnlyList<InstalledProgramEntry> removed, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<UninstallLeftoverGroup>>([]);

        public Task<UninstallLeftoverResult> RemoveLeftoversAsync(
            IReadOnlyList<UninstallLeftoverGroup> groups, CancellationToken cancellationToken) =>
            Task.FromResult(new UninstallLeftoverResult(0, 0, 0));

        public UninstallBackupMode BackupMode => UninstallBackupMode.Off;

        public Task<UninstallRestorePointResult> CreateRestorePointAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UninstallRestorePointResult(UninstallRestorePointOutcome.Created));

        public void RecordRemoved(InstalledProgramEntry entry)
        {
        }
    }

    /// <summary>A realistic inventory: ordinary rows, long names and publishers, a Risky driver, an own-window setup, a row that cannot be removed.</summary>
    private static UninstallerViewModel CreateUninstallerViewModel()
    {
        var listings = new List<UninstallListing>
        {
            UninstallerViewModelTests.Listing("Mozilla Firefox (x64 de)", size: 280L * 1024 * 1024, installed: new DateOnly(2026, 3, 2), version: "142.0.1", publisher: "Mozilla"),
            UninstallerViewModelTests.Listing("Microsoft Visual Studio Community 2022 with every workload installed", size: 9L * 1024 * 1024 * 1024, installed: new DateOnly(2025, 11, 20), version: "17.14.36203.30", publisher: "Microsoft Corporation"),
            UninstallerViewModelTests.Listing("Some Setup With Its Own Window", CleanupRiskLevel.Considered, UninstallRiskReason.Interactive, installed: new DateOnly(2024, 6, 1)),
            UninstallerViewModelTests.Listing("Chipset Device Software", CleanupRiskLevel.Risky, UninstallRiskReason.Hardware, size: 12L * 1024 * 1024, publisher: "Intel Corporation"),
            UninstallerViewModelTests.Listing("Uninstall Itself", CleanupRiskLevel.Considered, UninstallRiskReason.Own, runnable: false, selectable: false),
            UninstallerViewModelTests.Listing("Git.Git", kind: UninstallEntryKind.Package, size: 330L * 1024 * 1024, version: "2.51.0"),
            UninstallerViewModelTests.Listing("NVIDIA Graphics Driver 581.29", CleanupRiskLevel.Risky, UninstallRiskReason.Driver, kind: UninstallEntryKind.Driver, publisher: "NVIDIA"),
            UninstallerViewModelTests.Listing("KB5063878", CleanupRiskLevel.Risky, UninstallRiskReason.Update, kind: UninstallEntryKind.WindowsUpdate),
        };

        var viewModel = new UninstallerViewModel(new StaticUninstallerBackend(listings), new LoggingService());
        viewModel.LoadAsync().GetAwaiter().GetResult();
        foreach (var section in viewModel.Sections)
            section.IsExpanded = true;

        return viewModel;
    }

    private static void AssertUninstallerLaidOut(FrameworkElement content, UninstallerWindow window)
    {
        foreach (var name in new[] { "UninstallButton", "SearchBox" })
        {
            var element = FindByName<FrameworkElement>(content, name);
            Assert.NotNull(element);
            Assert.True(element!.ActualWidth > 0 && element.ActualHeight > 0, $"{name} was not laid out");
            var origin = element.TransformToAncestor(content).Transform(new Point(0, 0));
            Assert.True(origin.X >= 0 && origin.X + element.ActualWidth <= content.ActualWidth + 0.5, $"{name} leaves the window horizontally");
            Assert.True(origin.Y >= 0 && origin.Y + element.ActualHeight <= content.ActualHeight + 0.5, $"{name} leaves the window vertically");
        }
    }

    [Theory]
    [InlineData("de", "dark")]
    [InlineData("de", "light")]
    [InlineData("en", "dark")]
    [InlineData("en", "light")]
    public void Uninstaller_window_renders_with_all_sections(string language, string theme)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme(theme);

            var window = new UninstallerWindow(CreateUninstallerViewModel());
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"uninstaller-{theme}-{language}", (int)window.Width, (int)window.Height);
            AssertUninstallerLaidOut(content, window);
        });

        AssertRendered(result);
    }

    /// <summary>The window's own MinWidth/MinHeight, where the eight columns and the German footer have the least room.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Uninstaller_window_at_its_minimum_size_renders(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme("dark");

            var viewModel = CreateUninstallerViewModel();
            viewModel.AllRows.First(row => row.Selectable && !row.IsRisky).IsSelected = true;
            var window = new UninstallerWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"uninstaller-narrow-{language}", (int)window.MinWidth, (int)window.MinHeight);
            AssertUninstallerLaidOut(content, window);
        });

        AssertRendered(result);
    }

    /// <summary>The restore point step's line above the footer, in both languages, running and reused.</summary>
    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void Uninstaller_window_shows_the_restore_point_line(string language)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme("dark");

            var viewModel = CreateUninstallerViewModel();
            viewModel.RestorePointText = string.Format(
                LocalizationService.Instance["UninstallerRestorePointReused"], new DateTime(2026, 3, 2, 14, 30, 0).ToString("g", System.Globalization.CultureInfo.CurrentUICulture));
            var window = new UninstallerWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, $"uninstaller-restorepoint-{language}", (int)window.MinWidth, (int)window.MinHeight);
            var line = FindByName<FrameworkElement>(content, "RestorePointLine");
            Assert.NotNull(line);
            Assert.True(line!.ActualWidth > 0 && line.ActualHeight > 0, "the restore point line was not laid out");
        });

        AssertRendered(result);
    }

    /// <summary>Rows in every state a run can leave them in, plus a selection with a Risky row.</summary>
    [Fact]
    public void Uninstaller_window_shows_every_row_state()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var viewModel = CreateUninstallerViewModel();
            var states = new[]
            {
                UninstallRowState.Removed, UninstallRowState.Failed, UninstallRowState.NeedsRestart, UninstallRowState.NotStarted,
                UninstallRowState.Running, UninstallRowState.Waiting,
            };
            var index = 0;
            foreach (var row in viewModel.AllRows.Where(row => row.Selectable).Take(states.Length))
            {
                row.State = states[index++];
                if (row.State == UninstallRowState.Failed)
                    row.Reason = "Das Programm hat den Fehlercode 1603 gemeldet.";
            }

            var window = new UninstallerWindow(viewModel);
            result = WpfHarness.RenderWindow(window, "uninstaller-states-de", (int)window.Width, (int)window.Height);
        });

        AssertRendered(result);
    }

    /// <summary>After a run: the summary line and the leftovers card with a long path, a registry key and an unticked item, at the default and the minimum size.</summary>
    [Theory]
    [InlineData("de", "dark", false)]
    [InlineData("de", "light", false)]
    [InlineData("en", "dark", false)]
    [InlineData("de", "dark", true)]
    [InlineData("en", "light", true)]
    public void Uninstaller_window_after_a_run_shows_the_summary_and_the_leftovers(string language, string theme, bool narrow)
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply(language);
            ApplyTheme(theme);

            var viewModel = CreateUninstallerViewModel();
            var rows = viewModel.AllRows.Where(row => row.Selectable && !row.IsRisky).Take(3).ToList();
            rows[0].State = UninstallRowState.Removed;
            rows[1].State = UninstallRowState.Failed;
            rows[1].Reason = "Das Programm hat den Fehlercode 1603 gemeldet.";
            rows[2].State = UninstallRowState.NeedsRestart;
            viewModel.LeftoversRemoved = 2;
            viewModel.RunFinished = true;
            viewModel.LeftoverGroups.Add(new UninstallLeftoverGroupViewModel(rows[0].Entry,
            [
                new UninstallLeftoverItemViewModel(new LeftoverItem(LeftoverKind.Folder, LeftoverLane.User,
                    @"C:\Users\someone\AppData\Roaming\Mozilla\Firefox\Profiles\a-very-long-profile-folder-name.default-release\storage\permanent", "profile", true, 48L * 1024 * 1024), true, null),
                new UninstallLeftoverItemViewModel(new LeftoverItem(LeftoverKind.RegistryKey, LeftoverLane.User,
                    @"HKCU\Software\Mozilla\Firefox", "Firefox", false, 0), false, null),
                new UninstallLeftoverItemViewModel(new LeftoverItem(LeftoverKind.Shortcut, LeftoverLane.Machine,
                    @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Mozilla Firefox.lnk", "Firefox", true, 2048), true, null),
            ]));
            viewModel.UpdateLeftoverState();

            var window = new UninstallerWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            var width = narrow ? (int)window.MinWidth : (int)window.Width;
            var height = narrow ? (int)window.MinHeight : (int)window.Height;
            result = WpfHarness.RenderWindow(window, $"uninstaller-finished-{theme}-{language}{(narrow ? "-narrow" : string.Empty)}", width, height);
            AssertUninstallerLaidOut(content, window);
            foreach (var name in new[] { "SummaryLine", "LeftoversCard", "RemoveLeftoversButton" })
            {
                var element = FindByName<FrameworkElement>(content, name);
                Assert.NotNull(element);
                Assert.True(element!.ActualWidth > 0 && element.ActualHeight > 0, $"{name} was not laid out");
                var origin = element.TransformToAncestor(content).Transform(new Point(0, 0));
                Assert.True(origin.X >= 0 && origin.X + element.ActualWidth <= content.ActualWidth + 0.5, $"{name} leaves the window horizontally");
                Assert.True(origin.Y + element.ActualHeight <= content.ActualHeight + 0.5, $"{name} leaves the window vertically");
            }
        });

        AssertRendered(result);
    }

    /// <summary>The confirmation of a Risky selection:the acknowledge checkbox shows and the confirm button waits for it.</summary>
    [Fact]
    public void Confirmation_with_an_acknowledge_checkbox_renders_and_keeps_its_buttons_inside()
    {
        RenderResult? result = null;

        WpfHarness.OnUiThread(() =>
        {
            LocalizationService.Instance.Apply("de");
            ApplyTheme("dark");

            var loc = LocalizationService.Instance;
            var viewModel = new MessageDialogViewModel(
                DialogKind.Warning, loc["AppTitle"], string.Format(loc["UninstallerConfirm"], 3),
                "Chipset Device Software: " + loc["UninstallRiskHardware"] + Environment.NewLine + "NVIDIA Graphics Driver: " + loc["UninstallRiskDriver"],
                loc["UninstallerButton"], loc["DialogCancel"], destructive: true, acknowledgeText: loc["UninstallerAcknowledge"]);
            var window = new MessageDialogWindow(viewModel);
            var content = (FrameworkElement)window.Content;
            result = WpfHarness.RenderWindow(window, "uninstaller-confirm-de", 520, 400);

            Assert.False(window.PrimaryButton.IsEnabled);
            viewModel.Acknowledged = true;
            Assert.True(viewModel.CanConfirm);
            var row = window.ButtonRow;
            var bottom = row.TransformToAncestor(content).Transform(new Point(0, 0)).Y + row.ActualHeight;
            Assert.True(bottom <= content.ActualHeight + 0.5, "the button row is clipped below the window");
        });

        AssertRendered(result);
    }
}
