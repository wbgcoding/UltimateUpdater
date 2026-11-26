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
        bool selectable = true,
        string? key = null,
        string location = "",
        bool folderEmpty = false,
        bool interactive = false) =>
        new(
            new InstalledProgramEntry
            {
                Key = key ?? (kind == UninstallEntryKind.StoreApp
                    ? $"appx:{name}"
                    : $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}"),
                InstallLocation = location,
                InstallFolderEmpty = folderEmpty,
                Kind = kind,
                Name = name,
                Publisher = publisher,
                Version = version,
                SizeBytes = size,
                InstallDate = new DateOnly(2026, 3, 14),
            },
            runnable
                ? new UninstallPlan { Method = interactive ? UninstallMethod.Interactive : UninstallMethod.QuietString, Executable = @"C:\Program Files\Acme\unins000.exe", Elevated = true }
                : new UninstallPlan { Method = UninstallMethod.Refused, Refusal = UninstallRefusal.NoCommand },
            new UninstallRisk(level, reason, selectable));

    private static IReadOnlyList<UninstallListing> UninstallerInventory() =>
    [
        Listing("7-Zip", size: 5_600_000, version: "24.09", publisher: "Igor Pavlov"),
        Listing("Discord", size: 310_000_000, version: "1.0.9248", publisher: "Discord Inc."),
        Listing("Visual Studio Build Tools 2022 with a very long display name that cannot fit", size: 4_200_000_000, version: "17.10.5", publisher: "Microsoft Corporation", level: CleanupRiskLevel.Considered, reason: UninstallRiskReason.DevRuntime),
        Listing("NVIDIA Graphics Driver 560.94", size: 880_000_000, version: "560.94", publisher: "NVIDIA Corporation", level: CleanupRiskLevel.Risky, reason: UninstallRiskReason.Driver),
        Listing("Old Setup Suite", size: 64_000_000, version: "3.1", publisher: "Contoso", interactive: true),
        Listing("Legacy Tool", size: null, version: "0.9", publisher: string.Empty, runnable: false, level: CleanupRiskLevel.Considered, reason: UninstallRiskReason.Incomplete),
        Listing("Microsoft.WindowsCalculator", UninstallEntryKind.StoreApp, size: 12_000_000, version: "11.2405.1.0", publisher: "Microsoft Corporation"),
        Listing("left-pad", UninstallEntryKind.Package, size: 4_000, version: "1.3.0", publisher: "npm"),
        Listing("Intel Wireless Bluetooth", UninstallEntryKind.Driver, size: 40_000_000, version: "23.40.0.2", publisher: "Intel Corporation", level: CleanupRiskLevel.Risky, reason: UninstallRiskReason.Hardware),
        Listing("KB5039212", UninstallEntryKind.WindowsUpdate, size: null, version: string.Empty, publisher: "Microsoft Corporation"),
    ];

    private static IReadOnlyList<UninstallListing> UninstallerLookalikes() =>
    [
        Listing("Apex Legends", size: 72_000_000_000, key: @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1172470", location: @"D:\Steam\steamapps\common\Apex Legends", publisher: "Respawn"),
        Listing("Apex Legends", size: 104_000_000_000, key: @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{D7FBF176-1111-2222-3333-444455556666}", location: @"D:\EA Games\Apex", publisher: "Electronic Arts"),
        Listing("skate.", size: 0, key: @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 1222670", location: @"D:\Steam\steamapps\common\skate", folderEmpty: true, publisher: "Electronic Arts"),
        Listing("7-Zip", size: 5_600_000, version: "24.09", publisher: "Igor Pavlov"),
    ];

    private static void AddUninstaller(List<ReviewSurface> list)
    {
        Add(list, "uninstaller.lookalike", UninstallerArea, "Uninstaller with two same-named games from different launchers and a Steam game with an empty folder", s =>
        {
            var viewModel = s.NewUninstallerViewModel(UninstallerLookalikes(), expandAll: true);
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });

        Add(list, "uninstaller.suggestions", UninstallerArea, "Uninstaller with the suggestions card: a duplicate install, an entry without files and a large program unused for over a year", s =>
        {
            IReadOnlyList<UninstallListing> listings =
            [
                .. UninstallerLookalikes(),
                Listing("Old Game Collection", size: 38_000_000_000, version: "2.0", publisher: "Contoso Games"),
            ];
            var viewModel = s.NewUninstallerViewModel(
                listings,
                expandAll: false,
                (entries, _) => entries.Where(entry => entry.Name == "Old Game Collection").ToDictionary(entry => entry.Key, _ => DateTime.UtcNow.AddDays(-420)));
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });

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

        Add(list, "uninstaller.nomatch", UninstallerArea, "Uninstaller after a search that matches nothing", s =>
        {
            var viewModel = s.NewUninstallerViewModel(UninstallerInventory(), expandAll: true);
            const string noMatchQuery = "no such program";
            viewModel.SearchText = noMatchQuery;
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });

        Add(list, "uninstaller.empty", UninstallerArea, "Uninstaller with nothing installed", s =>
        {
            var viewModel = s.NewUninstallerViewModel([], expandAll: false);
            return Task.FromResult(new ReviewScene { Window = new UninstallerWindow(viewModel) });
        });
    }
}
#endif
