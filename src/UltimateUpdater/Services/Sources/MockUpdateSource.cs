using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Deterministic fake sources for offline UI work and VM testing, enabled with the
/// --mock-sources start argument. Covers every tier, a critical row, an empty source and a failing
/// one, so the table, the status chips and the error path can all be exercised without a network.
/// </summary>
public sealed class MockUpdateSource : IUpdateSource
{
    private readonly IReadOnlyList<UpdateItem> _items;
    private readonly bool _fails;
    private readonly TimeSpan _delay;
    private readonly bool _installRebootRequired;
    private readonly bool _installFails;

    public string Id { get; }

    public UpdateCategory Category { get; }

    public SourceTier Tier { get; }

    private MockUpdateSource(
        string id,
        UpdateCategory category,
        SourceTier tier,
        IReadOnlyList<UpdateItem> items,
        bool fails = false,
        TimeSpan delay = default,
        bool installRebootRequired = false,
        bool installFails = false)
    {
        Id = id;
        Category = category;
        Tier = tier;
        _items = items;
        _fails = fails;
        _delay = delay;
        _installRebootRequired = installRebootRequired;
        _installFails = installFails;
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken);

        if (_fails)
            throw new InvalidOperationException("Mock source failure (expected).");

        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>
    /// Simulates a real download instead of jumping straight to 100%: a one-shot report made every
    /// value in the progress window (throughput, MB transferred) look frozen when tested offline via
    /// --mock-sources, since there was nothing to sample between 0 and done.
    /// </summary>
    public async Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (Tier == SourceTier.C)
            throw new NotSupportedException($"{Id} cannot install.");

        // A stable-but-varied fake size per item, so different rows do not all look identical.
        var total = 15_000_000L + (Math.Abs(item.Name.GetHashCode()) % 60_000_000L);
        const int steps = 12;

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(90, cancellationToken);

            // One deterministic source (pip's, flagged by _installFails) fails partway through, so the
            // failure path can be driven end to end through --mock-sources, not only the restart prompt.
            if (_installFails && step == steps / 2)
                throw new InvalidOperationException($"Mock install failure for '{item.Name}' (expected).");

            var received = total * step / steps;
            progress.Report(new Models.InstallProgress
            {
                PercentComplete = step * 100.0 / steps,
                StatusText = item.Name,
                BytesReceived = received,
                BytesTotal = total,
                // Only the final report carries it, matching how a real source's own last
                // progress line is what NeedsReboot (sticky on the row) actually reads.
                RebootRequired = step == steps && _installRebootRequired,
            });
        }
    }

    /// <param name="showcase">True for <c>--showcase</c>: the same rows without the deliberately failing
    /// source, plus a few extra rows (an NVIDIA-style driver, an npm package) so the categories a real
    /// scan would spread across are all represented, for clean screenshots of the main window.</param>
    public static IReadOnlyList<IUpdateSource> CreateAll(bool showcase = false) =>
    [
        new MockUpdateSource("winget", UpdateCategory.Software, SourceTier.A,
        [
            Item("Git.Git", "Git", "winget", UpdateCategory.Software, "2.54.0", "2.55.0.3",
                size: 63_000_000, published: new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero)),
            Item("Discord.Discord", "Discord", "winget", UpdateCategory.Software, "1.0.9240", "1.0.9248",
                size: 121_000_000, published: new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero)),
            Item("Microsoft.GameInput", "Microsoft GameInput", "winget", UpdateCategory.Software, "3.3.221.0", "3.4.218",
                size: 8_400_000, published: new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)),
            Item("GermanCoding.SyncTrayzor", "SyncTrayzor (x64)", "winget", UpdateCategory.Software, "2.1.0.0", "2.2.0",
                size: 14_200_000, published: new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)),
            // Showcase-only: two more familiar names, so the Software group is not just four rows deep
            // and a screenshot's publisher column shows more than one company.
            ..(showcase
                ? new[]
                {
                    Item("VideoLAN.VLC", "VLC media player", "winget", UpdateCategory.Software, "3.0.20", "3.0.21",
                        size: 44_500_000, published: new DateTimeOffset(2026, 8, 18, 0, 0, 0, TimeSpan.Zero)),
                    Item("7zip.7zip", "7-Zip", "winget", UpdateCategory.Software, "24.08", "24.09",
                        size: 1_700_000, published: new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero)),
                }
                : []),
        ], delay: TimeSpan.FromMilliseconds(120)),

        // The one deterministic source for the restart prompt - every other mock row
        // finishes clean, so without this the reboot path could never be judged offline via --mock-sources.
        new MockUpdateSource("windowsupdate", UpdateCategory.SystemAndDrivers, SourceTier.A,
        [
            Item("KB2267602:1", "Security Intelligence Update for Microsoft Defender Antivirus; KB2267602 (Version 1.455.219.0)",
                "windowsupdate", UpdateCategory.SystemAndDrivers, null, null, critical: true,
                size: 62_000_000, published: new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero)),
            Item("KB5034123:2", "2026-07 Cumulative Update for Windows 11 (KB5034123)",
                "windowsupdate", UpdateCategory.SystemAndDrivers, null, null, critical: true,
                size: 890_000_000, published: new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero)),
            // Showcase-only: a driver-style row, the one SystemAndDrivers case a real machine without an
            // NVIDIA card would never surface through this mock source otherwise.
            ..(showcase
                ? new[]
                {
                    Item("NVIDIA:GeForceGameReadyDriver", "NVIDIA GeForce Game Ready Driver 566.14",
                        "windowsupdate", UpdateCategory.SystemAndDrivers, "561.09", "566.14",
                        size: 712_000_000, published: new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
                        meta: new Dictionary<string, string> { ["isDriverUpdate"] = "true", ["driverManufacturer"] = "NVIDIA" }),
                }
                : []),
        ], delay: TimeSpan.FromMilliseconds(260), installRebootRequired: true),

        // The one deterministic source for a non-fatal install failure, see InstallAsync's own
        // comment.
        new MockUpdateSource("pip", UpdateCategory.DeveloperTools, SourceTier.A,
        [
            Item("requests", "requests", "pip", UpdateCategory.DeveloperTools, "2.31.0", "2.32.3",
                size: 130_000, published: new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)),
            Item("numpy", "numpy", "pip", UpdateCategory.DeveloperTools, "2.3.1", "2.3.3",
                size: 16_800_000, published: new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)),
        ], delay: TimeSpan.FromMilliseconds(180), installFails: true),

        // an empty source: must render as "checked, nothing found", not as an error. Showcase-only: one
        // npm package, so the group everyone else here has (an update actually IN it) is represented too.
        new MockUpdateSource("npm", UpdateCategory.DeveloperTools, SourceTier.A,
            showcase
                ? [Item("eslint", "eslint", "npm", UpdateCategory.DeveloperTools, "9.8.0", "9.9.1",
                    size: 2_100_000, published: new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero))]
                : []),

        // a failing source: must not kill the scan
        new MockUpdateSource("scoop", UpdateCategory.Software, SourceTier.A, [], fails: !showcase,
            delay: TimeSpan.FromMilliseconds(90)),

        // Tier C: information only, no checkbox - matches the real SteamSource. ONE launcher row with
        // its pending game updates as a collapsible sub-list.
        new MockUpdateSource("steam", UpdateCategory.Games, SourceTier.C,
        [
            Item("steam", "Steam", "steam", UpdateCategory.Games, null, null,
                tier: SourceTier.C, open: "steam://open/downloads",
                children:
                [
                    Item("440", "Team Fortress 2", "steam", UpdateCategory.Games, "12345", "12999",
                        tier: SourceTier.C, open: "steam://open/downloads"),
                    Item("730", "Counter-Strike 2", "steam", UpdateCategory.Games, "18820", "18877",
                        tier: SourceTier.C, open: "steam://open/downloads"),
                ]),
        ], delay: TimeSpan.FromMilliseconds(150)),

        new MockUpdateSource("msstore", UpdateCategory.Store, SourceTier.C,
        [
            Item("msstore", "Microsoft Store & Xbox", "msstore", UpdateCategory.Store, null, null,
                tier: SourceTier.C, open: "ms-windows-store://downloadsandupdates"),
        ]),

        new MockUpdateSource("battlenet", UpdateCategory.Games, SourceTier.C,
        [
            Item("battlenet", "Battle.net", "battlenet", UpdateCategory.Games, null, null,
                tier: SourceTier.C, open: "battlenet://"),
        ]),
    ];

    private static UpdateItem Item(
        string id, string name, string source, UpdateCategory category,
        string? installed, string? available,
        bool critical = false, SourceTier tier = SourceTier.A, string? open = null,
        IReadOnlyList<UpdateItem>? children = null, long? size = null, DateTimeOffset? published = null,
        IReadOnlyDictionary<string, string>? meta = null) => new()
    {
        Id = id,
        Name = name,
        SourceId = source,
        Category = category,
        InstalledVersion = installed,
        AvailableVersion = available,
        IsCritical = critical,
        Tier = tier,
        OpenTarget = open,
        Children = children ?? [],
        DownloadSize = size,
        PublishDate = published,
        Meta = meta ?? new Dictionary<string, string>(),
    };
}
