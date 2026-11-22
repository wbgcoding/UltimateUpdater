using UltimateUpdater.Models;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.ViewModels.Main;

// "Uninstall" for a row whose source has no removal of its own (a program found through its GitHub repository, a launcher
// client): the row stands for one installed program, and the uninstaller removes that. Offered only when exactly one
// installed entry fits (RowUninstallMapper), never guessed.
public partial class MainViewModel
{
    private RowUninstallInventory? _rowUninstallInventory;

    /// <summary>The installed entries the rows map to: read on the first context-menu open of a fitting row, kept until the next scan completes.</summary>
    internal RowUninstallInventory RowUninstallEntries => _rowUninstallInventory ??= new RowUninstallInventory(
        UninstallerBackendOverride?.Invoke() ?? new UninstallerBackend(_logging, _elevated, _settings, _history), _logging);

    /// <summary>Removals of mapped rows in flight, each with the token its row's stop button cancels.</summary>
    private readonly Dictionary<UpdateRowViewModel, CancellationTokenSource> _mappedUninstalls = [];

    /// <summary>Whether the row's removal runs through the uninstaller: its source has none of its own and it may map to an installed program.</summary>
    private bool UsesMappedUninstall(UpdateItem item) =>
        RowUninstallMapper.IsEligible(item) && !(SourceFor(item)?.CanUninstall(item) ?? false);

    private UninstallListing? MappedListing(UpdateRowViewModel row) =>
        UsesMappedUninstall(row.Item) && RowUninstallEntries.Cached is { } listings
            ? RowUninstallMapper.Match(row.Item, publisher: null, listings)
            : null;

    bool IRowActions.CanUninstallMapped(UpdateRowViewModel row) => MappedListing(row) is not null;

    void IRowActions.LoadUninstallMapping(UpdateRowViewModel row)
    {
        if (UsesMappedUninstall(row.Item))
            _ = Background.Run(LoadUninstallMappingAsync(row), _logging, "uninstall mapping");
    }

    /// <summary>Reads the installed entries (once per scan), then asks every row that could map to one again, so the entry is there when the menu is shown.</summary>
    internal async Task LoadUninstallMappingAsync(UpdateRowViewModel row)
    {
        if (!UsesMappedUninstall(row.Item) || RowUninstallEntries.Cached is not null)
            return;

        await RowUninstallEntries.EnsureLoadedAsync();
        row.RefreshUninstallAvailability();
    }

    /// <summary>Stops a mapped removal that has not started its setup yet; false when the row has none running.</summary>
    private bool CancelMappedUninstall(UpdateRowViewModel row)
    {
        if (!_mappedUninstalls.TryGetValue(row, out var cts))
            return false;

        cts.Cancel();
        return true;
    }

    /// <summary>
    /// The removal of a mapped row: confirm (the reason too for a risky entry), the restore point the backup setting
    /// asks for, then the uninstaller's own runner for that one entry. Standalone like the interactive retry: it does
    /// not use the install queue, and it counts as running work while it lasts.
    /// </summary>
    internal async Task UninstallMappedRowAsync(UpdateRowViewModel row)
    {
        // A retry after a scan (or after the first removal) finds the entries dropped: read them again.
        if (UsesMappedUninstall(row.Item))
            await RowUninstallEntries.EnsureLoadedAsync();

        var listing = MappedListing(row);
        if (listing is null)
            return;

        var risky = listing.Risk.Level == CleanupRiskLevel.Risky;
        var body = risky
            ? Loc["UninstallConfirmBody"] + Environment.NewLine + Environment.NewLine + Loc[listing.Risk.ReasonKey]
            : Loc["UninstallConfirmBody"];
        var confirmed = UninstallConfirmOverride?.Invoke() ?? Dialogs.Confirm(
            string.Format(Loc["UninstallConfirmHeadline"], row.Name),
            body,
            confirmText: Loc["UninstallButton"],
            destructive: true,
            acknowledgeText: risky ? Loc["UninstallerAcknowledge"] : null);
        if (!confirmed)
            return;

        // The confirmation pumps messages: the row can have been queued meanwhile.
        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["UninstallBlockedByActivity"]);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Uninstall"))
            return;

        using var cts = new CancellationTokenSource();
        var key = PackageKey(row.Item);
        _mappedUninstalls[row] = cts;
        _uninstallingKeys.Add(key);
        row.BeginInstall();
        row.IsSelected = false;
        row.WasUninstalled = true;
        row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, null, null, null));
        var backend = RowUninstallEntries.Backend;
        try
        {
            var step = await UninstallRestorePointStep.RunAsync(
                backend,
                anyElevated: listing.Plan is { IsRunnable: true, Elevated: true },
                anyRisky: risky,
                confirmWithout: () => UninstallRestorePointFailedOverride?.Invoke() ?? Dialogs.Confirm(
                    Loc["UninstallerRestorePointFailed"], string.Empty, Loc["UninstallerRestorePointFailedButton"], destructive: true),
                _logging,
                onCreating: () => row.ApplyInstallStatus(
                    new InstallItemStatus(row.SourceId, row.Item.Id, 0, Loc["InstallRestorePointStep"], null, null)),
                cts.Token);
            if (!step.Proceed)
            {
                row.MarkNotInstalled(Loc["InstallCancelled"]);
                return;
            }

            row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, null, null, null));
            var runner = backend.CreateRunner(_ => listing.Plan);
            var results = await Task.Run(() => runner.RunAsync([listing.Entry], onUpdate: null, cts.Token));
            ApplyMappedResult(row, listing, results.Count > 0 ? results[0] : new UninstallEntryResult(listing.Entry, UninstallOutcome.NotStarted));
        }
        catch (OperationCanceledException)
        {
            row.MarkNotInstalled(Loc["InstallCancelled"]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logging.Error("Uninstall of a table row failed", ex);
            row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, null, false, Loc["UninstallFailed"]) { Detail = ex.Message });
        }
        finally
        {
            _mappedUninstalls.Remove(row);
            _uninstallingKeys.Remove(key);
            ReportRebootsFor([row]);
        }
    }

    private void ApplyMappedResult(UpdateRowViewModel row, UninstallListing listing, UninstallEntryResult result)
    {
        switch (result.Outcome)
        {
            case UninstallOutcome.Removed or UninstallOutcome.NeedsRestart:
                row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 100, null, true, null)
                {
                    RebootRequired = result.Outcome == UninstallOutcome.NeedsRestart,
                });
                RowUninstallEntries.Backend.RecordRemoved(listing.Entry);
                // What was read no longer holds: the entry is gone.
                RowUninstallEntries.Invalidate();
                break;
            case UninstallOutcome.NotStarted:
                row.MarkNotInstalled(Loc["UninstallNotStarted"]);
                break;
            default:
                row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, null, false, result.Reason ?? Loc["UninstallFailed"])
                {
                    Detail = result.Detail,
                });
                break;
        }
    }

    /// <summary>Test seam (null in production): answers "remove without a restore point?" for a mapped row.</summary>
    internal Func<bool>? UninstallRestorePointFailedOverride { get; set; }
}
