#if DEBUG
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;
using UltimateUpdater.ViewModels.Uninstall;
using UltimateUpdater.Views.Uninstall;

namespace UltimateUpdater.Review;

internal static partial class ReviewCatalog
{
    private const string UninstallerArea = "uninstaller";

    private static UninstallListing Listing(
        string name,
        UninstallEntryKind kind = UninstallEntryKind.Program,
        CleanupRiskLevel level = CleanupRiskLevel.Safe,
        UninstallRiskReason reason = UninstallRiskReason.Ordinary,
        long? size = 120_000_000,
        string version = "1.0.0",
        string publisher = "Acme Software",
        bool runnable = true,
        bool selectable = true) =>
        new(
            new InstalledProgramEntry
            {
                Key = kind == UninstallEntryKind.StoreApp
                    ? $"appx:{name}"
                    : $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
                Kind = kind,
                Name = name,
                Publisher = publisher,
                Version = version,
                SizeBytes = size,
                InstallDate = new DateOnly(2026, 3, 14),
            },
            runnable
                ? new UninstallPlan { Method = UninstallMethod.QuietString, Executable = @"C:\Program Files\Acme\unins000.exe", Elevated = true }
                : new UninstallPlan { Method = UninstallMethod.Refused, Refusal = UninstallRefusal.NoCommand },
            new UninstallRisk(level, reason, selectable));

    private static IReadOnlyList<UninstallListing> UninstallerInventory() =>
    [
        Listing("7-Zip", size: 5_600_000, version: "24.09", publisher: "Igor Pavlov"),
        Listing("Discord", size: 310_000_000, version: "1.0.9248", publisher: "Discord Inc."),
        Listing("Visual Studio Build Tools 2022 with a very long display name that cannot fit", size: 4_200_000_000, version: "17.10.5", publisher: "Microsoft Corporation", level: CleanupRiskLevel.Considered, reason: UninstallRiskReason.DevRuntime),
        Listing("NVIDIA Graphics Driver 560.94", size: 880_000_000, version: "560.94", publisher: "NVIDIA Corporation", level: CleanupRiskLevel.Risky, reason: UninstallRiskReason.Driver),
        Listing("Legacy Tool", size: null, version: "0.9", publisher: string.Empty, runnable: false, level: CleanupRiskLevel.Considered, reason: UninstallRiskReason.Incomplete),
        Listing("Microsoft.WindowsCalculator", UninstallEntryKind.StoreApp, size: 12_000_000, version: "11.2405.1.0", publisher: "Microsoft Corporation"),
        Listing("left-pad", UninstallEntryKind.Package, size: 4_000, version: "1.3.0", publisher: "npm"),
        Listing("Intel Wireless Bluetooth", UninstallEntryKind.Driver, size: 40_000_000, version: "23.40.0.2", publisher: "Intel Corporation", level: CleanupRiskLevel.Risky, reason: UninstallRiskReason.Hardware),
        Listing("KB5039212", UninstallEntryKind.WindowsUpdate, size: null, version: string.Empty, publisher: "Microsoft Corporation"),
    ];

    private static void AddUninstaller(List<ReviewSurface> list)
    {
        Add(list, "uninstaller.default", UninstallerArea, "Uninstaller as it opens: programs section open, others collapsed", s =>
        {
            var viewModel = s.NewUninstallerViewModel(UninstallerInventory(), expandAll: false);
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });

        Add(list, "uninstaller.expanded", UninstallerArea, "Uninstaller with every section open", s =>
        {
            var viewModel = s.NewUninstallerViewModel(UninstallerInventory(), expandAll: true);
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });

        Add(list, "uninstaller.selected", UninstallerArea, "Uninstaller with rows ticked, mixed risk", s =>
        {
            var viewModel = s.NewUninstallerViewModel(UninstallerInventory(), expandAll: true);
            var window = new UninstallerWindow(viewModel);
            var scene = new ReviewScene { Window = window };
            scene.AfterShown = () =>
            {
                viewModel.SelectAllCommand.Execute(null);
                return Task.CompletedTask;
            };
            return Task.FromResult(scene);
        });

        Add(list, "uninstaller.empty", UninstallerArea, "Uninstaller with nothing installed", s =>
        {
            var viewModel = s.NewUninstallerViewModel([], expandAll: false);
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });
    }
}
#endif
