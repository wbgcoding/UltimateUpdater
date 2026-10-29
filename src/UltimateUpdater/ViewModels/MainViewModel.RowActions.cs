using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

// The per-row actions (IRowActions): install, start, uninstall and interactive retry, plus ignore,
// snooze and persistent-failure state.
public partial class MainViewModel
{
    /// <summary>
    /// Package keys with an uninstall currently in flight. <see cref="TrackPersistentFailure"/> checks this
    /// because <see cref="InstallItemStatus"/> carries no action, so the shared status dispatch
    /// (<see cref="OnInstallStatusChanged"/>) cannot tell an install's terminal status from an uninstall's.
    /// Without it, a failed removal would count toward the SAME three-strikes tally as a failed install
    /// (three failed uninstalls would flag the package NeedsManualWork and refuse to let it be INSTALLED),
    /// and a successful removal would clear a real install-failure streak. Populated only by
    /// <see cref="UninstallRowAsync"/>, the only call site that passes <see cref="InstallAction.Uninstall"/>,
    /// for exactly the span of its single-item, synchronously dispatched <c>_installQueue.RunAsync</c> call.
    /// </summary>
    private readonly HashSet<string> _uninstallingKeys = new(StringComparer.Ordinal);

    /// <summary>Ignoring an update means "stop telling me about it", so it stops being counted too. A
    /// deferred (snoozed) row is excluded the same way: "not now" is not "I want to hear
    /// about it again in this count until the date I actually asked for" either.</summary>
    private IEnumerable<UpdateRowViewModel> NotIgnored => Updates.Where(u => !IsIgnored(u.Item) && !IsSnoozed(u.Item));

    /// <summary>
    /// An update is ignored either by package (all versions) or by the exact available version. The
    /// version key is what makes a winget "phantom" upgrade (a version that reappears after it installs)
    /// stay gone until a genuinely newer one shows up.
    /// </summary>
    private bool IsIgnored(UpdateItem item) =>
        _settings.Current.Excludes.Contains(PackageKey(item))
        || (!string.IsNullOrEmpty(item.AvailableVersion) && _settings.Current.Excludes.Contains(VersionKey(item)));

    /// <summary>Same package-vs-version key shape as <see cref="IsIgnored"/>, over the <c>Snoozed</c>
    /// dictionary instead of <c>Excludes</c>.</summary>
    private bool IsSnoozed(UpdateItem item) => SnoozedUntilFor(item) is not null;

    private DateTimeOffset? SnoozedUntilFor(UpdateItem item)
    {
        if (TryGetActiveSnooze(PackageKey(item), out var packageUntil))
            return packageUntil;

        if (!string.IsNullOrEmpty(item.AvailableVersion) && TryGetActiveSnooze(VersionKey(item), out var versionUntil))
            return versionUntil;

        return null;
    }

    /// <summary>Passive expiry, same pattern as <see cref="EnrichmentCache.TryGet"/>: an entry whose due
    /// date has passed is dropped the moment anything looks at it again, so a snooze reappears on its own
    /// without waiting for the next app start. <see cref="SettingsService"/> already sweeps on load, this
    /// covers the far more common case of the app staying open across the due date.</summary>
    private bool TryGetActiveSnooze(string key, out DateTimeOffset until)
    {
        if (_settings.Current.Snoozed.TryGetValue(key, out until))
        {
            if (until > DateTimeOffset.UtcNow)
                return true;

            _settings.Current.Snoozed.Remove(key);
        }

        until = default;
        return false;
    }

    /// <summary>Applies the persisted snooze state to a row and its children (Steam's own game rows) .
    /// the same level <c>IsIgnored</c> is already applied at in <see cref="DrainPendingItems"/>. Also
    /// what makes an expired snooze reappear on the very next scan even when nothing else about the row
    /// changed (the common case, where the "unchanged, keep the existing row" fast path would otherwise
    /// never re-check it).</summary>
    private void ApplySnoozeState(UpdateRowViewModel row)
    {
        var until = SnoozedUntilFor(row.Item);
        row.IsSnoozed = until is not null;
        row.SnoozedUntil = until;

        foreach (var child in row.Children)
            ApplySnoozeState(child);
    }

    /// <summary>Ignore this exact available version. "source:id@version".</summary>
    private static string VersionKey(UpdateItem item) => $"{item.SourceId}:{item.Id}@{item.AvailableVersion}";

    /// <summary>Ignore the package whatever its version. "source:id". Also the row-reconcile identity.</summary>
    private static string PackageKey(UpdateItem item) => $"{item.SourceId}:{item.Id}";

    /// <summary>
    /// Toggle: ignore THIS version of an update, or show an already-ignored one again. An ignored row
    /// leaves the table at once unless "show ignored" is on (then it stays, marked); either way this
    /// only flips the flag, the persisted list and the counts, the row object itself is never removed.
    /// Un-ignoring clears both key forms.
    /// </summary>
    [RelayCommand]
    private void Ignore(UpdateRowViewModel? row)
    {
        if (row is null)
            return;

        if (IsIgnored(row.Item))
        {
            _settings.Current.Excludes.Remove(PackageKey(row.Item));
            _settings.Current.Excludes.Remove(VersionKey(row.Item));
            row.IsIgnored = false;
            row.IsIgnoredAllVersions = false;
        }
        else
        {
            // A manual-open row (a launcher/store, even Steam with build ids) has no per-version ignore .
            // it is ignored as a whole package. Real update rows ignore just the current version.
            var wholePackage = string.IsNullOrEmpty(row.Item.AvailableVersion) || row.IsManualOnly;
            var key = wholePackage ? PackageKey(row.Item) : VersionKey(row.Item);
            if (!_settings.Current.Excludes.Contains(key))
                _settings.Current.Excludes.Add(key);
            row.IsIgnored = true;
            row.IsIgnoredAllVersions = wholePackage;

            // Ignore and Snooze are mutually exclusive: setting one clears the other, or a row could end
            // up marked both "never tell me" and "tell me again on a date" at once.
            ClearSnooze(row);
        }

        Persist();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>Ignores EVERY version of just this one package (the row menu's "Ignore all versions").</summary>
    void IRowActions.IgnoreAllVersions(UpdateRowViewModel row)
    {
        // the package-wide key supersedes any single-version one already set for it
        _settings.Current.Excludes.Remove(VersionKey(row.Item));
        var key = PackageKey(row.Item);
        if (!_settings.Current.Excludes.Contains(key))
            _settings.Current.Excludes.Add(key);

        row.IsIgnored = true;
        row.IsIgnoredAllVersions = true;
        ClearSnooze(row);

        Persist();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>"Remind me later" for a fixed duration ("day"/"week"/"month"). Clears any Ignore on the
    /// same row first: the two are mutually exclusive.</summary>
    void IRowActions.Snooze(UpdateRowViewModel row, string duration)
    {
        _settings.Current.Excludes.Remove(PackageKey(row.Item));
        _settings.Current.Excludes.Remove(VersionKey(row.Item));
        row.IsIgnored = false;
        row.IsIgnoredAllVersions = false;

        // Same package-vs-version split as Ignore: a manual-open row snoozes as a whole package, a real
        // update row snoozes just the version currently on offer.
        var wholePackage = string.IsNullOrEmpty(row.Item.AvailableVersion) || row.IsManualOnly;
        var key = wholePackage ? PackageKey(row.Item) : VersionKey(row.Item);
        // clear the other key form too, so a package-wide snooze does not leave a stale per-version one
        // behind that would otherwise keep matching after this one expires
        _settings.Current.Snoozed.Remove(wholePackage ? VersionKey(row.Item) : PackageKey(row.Item));

        var until = DateTimeOffset.UtcNow + SnoozeDuration(duration);
        _settings.Current.Snoozed[key] = until;
        row.IsSnoozed = true;
        row.SnoozedUntil = until;

        Persist();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>"Show again now". clears a snooze early, without waiting for it to expire.</summary>
    void IRowActions.Unsnooze(UpdateRowViewModel row)
    {
        ClearSnooze(row);

        Persist();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    private void ClearSnooze(UpdateRowViewModel row)
    {
        _settings.Current.Snoozed.Remove(PackageKey(row.Item));
        _settings.Current.Snoozed.Remove(VersionKey(row.Item));
        row.IsSnoozed = false;
        row.SnoozedUntil = null;
    }

    /// <summary>Flat day counts rather than calendar arithmetic (AddMonths): same pragmatic choice the
    /// rest of the app's day-based settings already make (HistoryRetentionDays, LogSettings.RetentionDays,
    /// EnrichmentCache's 7-day TTL): simpler, and a snooze due date does not need calendar precision.</summary>
    private static TimeSpan SnoozeDuration(string duration) => duration switch
    {
        "week" => TimeSpan.FromDays(7),
        "month" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>"Try normally again": the only way back from <see cref="RowInstallState.NeedsManualWork"/>,
    /// clearing both the row's own flag and the persisted tally behind it.</summary>
    void IRowActions.ResetPersistentFailure(UpdateRowViewModel row)
    {
        _settings.Current.PersistentFailures.Remove(PackageKey(row.Item));
        row.InstallState = RowInstallState.Idle;
        row.InstallErrorDetail = null;
        row.InstallStatusText = null;

        Persist();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>
    /// Called after every terminal install/uninstall result. A success clears any tally for the package
    /// immediately: an update that once failed three times but now succeeds is not still broken. A Failed
    /// or Stuck result is counted only when <see cref="InstallErrorExplainer"/> can classify it: an
    /// unclassified failure has no signature to compare against a later one, and raw text alone would never
    /// see two failures as "the same" (changing paths/timestamps/versions). Three matching signatures in a
    /// row promote the live row to NeedsManualWork; anything else (first occurrence, or a changed
    /// signature) records the new tally and leaves the row's Failed/Stuck state as ApplyInstallStatus set it.
    /// </summary>
    private void TrackPersistentFailure(UpdateRowViewModel row)
    {
        var key = PackageKey(row.Item);

        // See _uninstallingKeys' own doc comment: an uninstall's outcome must never feed or clear the
        // INSTALL three-strikes tally for the same package.
        if (_uninstallingKeys.Contains(key))
            return;

        if (row.InstallState == RowInstallState.Succeeded)
        {
            if (_settings.Current.PersistentFailures.Remove(key))
                Persist();
            return;
        }

        if (row.InstallState is not (RowInstallState.Failed or RowInstallState.Stuck))
            return;

        var explanation = InstallErrorExplainer.Explain(row.SourceId, row.InstallErrorDetail);
        if (explanation is null)
            return;

        var signature = $"{explanation.WhatHappenedKey}:{string.Join(",", explanation.WhatHappenedArgs)}";

        // Capped and redacted the same way HistoryService.Append holds its own Detail field: this is raw
        // tool output, up to 10 M chars, otherwise stored unbounded in settings.json where an
        // oversized file falls back to defaults on next load, and an unredacted detail could carry a
        // token or a profile path onto disk.
        var cappedErrorText = HistoryService.CapDetail(row.InstallErrorDetail);

        if (_settings.Current.PersistentFailures.TryGetValue(key, out var record) && record.Signature == signature)
        {
            record.Count++;
            record.LastAt = DateTimeOffset.UtcNow;
            record.LastErrorText = cappedErrorText;
        }
        else
        {
            record = new PersistentFailureRecord
            {
                Signature = signature,
                Count = 1,
                LastAt = DateTimeOffset.UtcNow,
                LastErrorText = cappedErrorText,
            };
            _settings.Current.PersistentFailures[key] = record;
        }

        if (record.Count >= 3)
            row.InstallState = RowInstallState.NeedsManualWork;

        Persist();
    }

    /// <summary>Applies a persisted 3-strike flag to a freshly scanned row: same level <see
    /// cref="ApplySnoozeState"/> already applies Snooze at, and for the same reason: the flag must survive
    /// a restart, not just the live session the 3rd failure happened in.</summary>
    private void ApplyPersistentFailureState(UpdateRowViewModel row)
    {
        if (_settings.Current.PersistentFailures.TryGetValue(PackageKey(row.Item), out var record) && record.Count >= 3)
        {
            row.InstallState = RowInstallState.NeedsManualWork;
            row.InstallErrorDetail = record.LastErrorText;
        }

        foreach (var child in row.Children)
            ApplyPersistentFailureState(child);
    }

    /// <summary>
    /// Whether this row can offer "back to the previous version", and if so, which one. Only a
    /// source whose <see cref="Services.Sources.IUpdateSource.SupportsVersionPinning"/> is true is ever
    /// offered one; everyone else's rows simply never carry a target. Same level <see
    /// cref="ApplyPersistentFailureState"/> is applied at, for the same reason: the row must read as
    /// rollback-capable the moment it appears, not only after the user opens its menu once already.
    /// </summary>
    private void ApplyDowngradeState(UpdateRowViewModel row, IReadOnlyDictionary<string, HistoryEntry> lastSuccessfulInstalls)
    {
        row.DowngradeTargetVersion =
            SourceFor(row.Item)?.SupportsVersionPinning == true
            && lastSuccessfulInstalls.TryGetValue(PackageKey(row.Item), out var entry)
                ? entry.FromVersion
                : null;

        foreach (var child in row.Children)
            ApplyDowngradeState(child, lastSuccessfulInstalls);
    }

    void IRowActions.RetryDowngrade(UpdateRowViewModel row, string targetVersion) =>
        _ = Background.Run(DowngradeAsync(row, targetVersion), _logging, "downgrade");

    // IRowActions: the row menu's "back to the previous version" entry.
    void IRowActions.Downgrade(UpdateRowViewModel row)
    {
        if (row.DowngradeTargetVersion is not { Length: > 0 } targetVersion)
            return;

        _ = Background.Run(DowngradeAsync(row, targetVersion), _logging, "downgrade");
    }

    /// <summary>
    /// A second entry point for downgrade: the History window's own downgrade button, offered on an entry
    /// for a package that may have no live row on screen at all (it is fully current, so no source lists it
    /// as an update). Reuses a live row when the table has one for the exact same package instead of
    /// creating a second, ambiguous one that would race it for the same status updates
    /// (<see cref="FindRow"/> matches by source+id alone, never by row instance).
    /// </summary>
    internal void DowngradeFromHistory(string sourceId, string itemId, string name, string? installedVersion, string targetVersion)
    {
        var existing = FindRow(sourceId, itemId);
        var row = existing ?? CreateTransientRow(sourceId, itemId, name, installedVersion);
        _ = Background.Run(DowngradeAsync(row, targetVersion, removeRowIfNotStarted: existing is null), _logging, "downgrade");
    }

    /// <summary>Takes back a row built only for a downgrade that never started, so a declined or refused
    /// downgrade leaves no ghost entry in the table.</summary>
    private void RemoveUnusedTransientRow(UpdateRowViewModel row)
    {
        var key = PackageKey(row.Item);
        if (_rowsByKey.TryGetValue(key, out var held) && ReferenceEquals(held, row))
            _rowsByKey.Remove(key);

        Updates.Remove(row);
    }

    /// <summary>A row for a package the table currently has no reason to show (fully up to date), built
    /// only so the History window's downgrade button has something to run and report progress on. Tracked
    /// in <see cref="_rowsByKey"/>/<see cref="Updates"/> like any other row so the next real scan reconciles
    /// or removes it exactly like it would a normal finished row, rather than leaving a permanent ghost.</summary>
    internal UpdateRowViewModel CreateTransientRow(string sourceId, string itemId, string name, string? installedVersion)
    {
        var item = new UpdateItem
        {
            Id = itemId,
            SourceId = sourceId,
            Category = UpdateCategory.Software,
            Name = name,
            InstalledVersion = installedVersion,
        };

        var row = new UpdateRowViewModel(item, _logging, Ignore, this, ShowErrorDetail, ShowChangelogAsync);
        TrackSelection(row);
        _rowsByKey[PackageKey(item)] = row;
        Updates.Add(row);
        return row;
    }

    /// <summary>
    /// "back to {version}". Confirmed first, with an explicit warning that the newer version will be offered
    /// again on the next scan, then run through the same machinery as an uninstall (one item, the shared
    /// elevation/progress/history plumbing); only the target item differs, its
    /// <see cref="UpdateItem.AvailableVersion"/>/<see cref="UpdateItem.PinnedVersion"/> pinned to the older
    /// version by <see cref="UpdateItem.WithPinnedVersion"/> rather than whatever the source would resolve
    /// as the latest upgrade. A solo activity like uninstall and interactive retry (see
    /// <see cref="_installActivityRunning"/>): it owns the same shared progress/elevation state, so it
    /// cannot run alongside a batch install or another solo activity.
    /// </summary>
    private async Task DowngradeAsync(UpdateRowViewModel row, string targetVersion, bool removeRowIfNotStarted = false)
    {
        if (_installActivityRunning)
        {
            Dialogs.Warn(Loc["DowngradeBlockedByActivity"]);
            if (removeRowIfNotStarted)
                RemoveUnusedTransientRow(row);
            return;
        }

        // A plain hyphen for the rare row with no known installed version (same fallback
        // HistoryRowViewModel.FromVersionText uses), pulled out of the confirmation dialog's own argument
        // list so the hardwired-text scan does not mistake a punctuation placeholder for an untranslated
        // word (HardwiredTextTests.cs).
        var installedVersionText = row.Item.InstalledVersion ?? "-";
        var confirmBody = string.Format(Loc["DowngradeConfirmBody"], row.Name, installedVersionText, targetVersion);
        var confirmText = string.Format(Loc["RowDowngrade"], targetVersion);
        var confirmed = Dialogs.Confirm(
            Loc["DowngradeConfirmTitle"], confirmBody, confirmText: confirmText, destructive: true);
        if (!confirmed)
        {
            if (removeRowIfNotStarted)
                RemoveUnusedTransientRow(row);
            return;
        }

        // Dialogs.Confirm pumps messages while it is up; re-check for the same reason UninstallRowAsync
        // does right after its own confirmation.
        if (_installActivityRunning)
        {
            Dialogs.Warn(Loc["DowngradeBlockedByActivity"]);
            if (removeRowIfNotStarted)
                RemoveUnusedTransientRow(row);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Rollback"))
        {
            if (removeRowIfNotStarted)
                RemoveUnusedTransientRow(row);
            return;
        }

        _installCts?.Cancel();
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        _installActivityRunning = true;
        IsInstalling = true;

        row.BeginInstall();
        row.LastDowngradeTarget = targetVersion;
        StartInstallProgress([row]);

        var pinnedItem = row.Item.WithPinnedVersion(targetVersion);

        try
        {
            await _installQueue.RunAsync([pinnedItem], _installCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A user-triggered command handler, same reasoning as UninstallRowAsync: whatever the source
            // throws must end this row's run, never the app.
            _logging.Error("Downgrade failed", ex);
            if (row.InstallState is RowInstallState.Queued or RowInstallState.Running)
                row.MarkNotInstalled(Loc["InstallFailed"]);
        }
        finally
        {
            if (EndInstallActivity())
                HandOffPendingInstalls();
        }
    }

    // IRowActions: the per-row "Install" button queues just that one row.
    void IRowActions.InstallSingle(UpdateRowViewModel row) => _ = Background.Run(EnqueueInstallAsync([row]), _logging, "install single");

    // IRowActions: the per-row "stop this one" button; the rest of the queue keeps running.
    void IRowActions.CancelSingle(UpdateRowViewModel row) => _installQueue.CancelItem(row.Item.SourceId, row.Item.Id);

    // IRowActions: the Start and Uninstall entries on a row's context menu.

    bool IRowActions.CanStart(UpdateItem item) => SourceFor(item)?.LaunchTarget(item) is not null;

    bool IRowActions.CanUninstall(UpdateItem item) => SourceFor(item)?.CanUninstall(item) ?? false;

    void IRowActions.Start(UpdateRowViewModel row)
    {
        // The source's own target first (a launcher URI, a Steam game); otherwise the executable resolved
        // for the row from the uninstall registry, which is what gives an ordinary package a Start at all.
        var target = SourceFor(row.Item)?.LaunchTarget(row.Item) ?? row.LaunchTargetPath;
        if (!string.IsNullOrEmpty(target))
            UriLauncher.LaunchDetached(target, _logging);
    }

    void IRowActions.Uninstall(UpdateRowViewModel row) => _ = Background.Run(UninstallRowAsync(row), _logging, "uninstall");

    void IRowActions.RetryInteractive(UpdateRowViewModel row) => _ = Background.Run(RetryInteractiveAsync(row), _logging, "retry interactive");

    /// <summary>
    /// Test-only seam (never set outside <c>UltimateUpdater.Tests</c>): when set, replaces the actual call
    /// to <see cref="Services.Sources.WingetSource.InstallInteractiveAsync"/> so a test can hold the
    /// interactive retry "running" on demand. The gate in <see cref="RetryInteractiveAsync"/> (the row must
    /// resolve to a real <see cref="Services.Sources.WingetSource"/>) is untouched; only the OS-level call
    /// is substitutable.
    /// </summary>
    internal Func<UpdateItem, IProgress<Models.InstallProgress>, CancellationToken, Task>? RetryInteractiveOverride { get; set; }

    /// <summary>
    /// The one deliberate, narrow exception to "never --interactive".
    /// Every hard boundary is enforced here, not only by the UI hiding the entry:
    /// 1. reachable only from a click on this one row (its context menu;
    ///    <see cref="ShowRetryInteractiveButton"/> is never read by any batch/automatic path);
    /// 2. refuses outright when <see cref="App.IsUnattended"/>;
    /// 3. never goes through the elevated worker: it calls <see cref="WingetSource.InstallInteractiveAsync"/>
    ///    directly, in-process, bypassing <see cref="InstallQueue"/>/<see cref="InstallQueue.NeedsElevation"/>
    ///    (which would route every winget job to the elevated worker). A per-machine install that needs
    ///    administrator rights is winget's own concern (it can raise its own UAC prompt) and may not
    ///    succeed from here: an accepted trade-off for a last-resort, one-shot retry;
    /// 4. exactly one attempt: no loop, no re-arming; the row returns to the normal Failed/Succeeded path
    ///    whatever the outcome.
    /// </summary>
    internal async Task RetryInteractiveAsync(UpdateRowViewModel row)
    {
        if (App.IsUnattended || !row.ShowRetryInteractiveButton)
            return;

        // The user clicked this on purpose, so a refusal says why (the uninstall path does the same).
        if (_installActivityRunning)
        {
            Dialogs.Warn(Loc["RetryBlockedByActivity"]);
            return;
        }

        if (SourceFor(row.Item) is not Services.Sources.WingetSource source)
            return;

        if (RefusedWhileSelfUpdateApplies("Interactive retry"))
            return;

        _installCts?.Cancel();
        _installCts?.Dispose();
        _installCts = new CancellationTokenSource();
        var token = _installCts.Token;
        _installActivityRunning = true;
        IsInstalling = true;
        _interactiveRetryLane = InstallQueue.LaneKey(row.Item);

        row.BeginInstall();
        row.IsRunningInteractively = true;
        StartInstallProgress([row]);

        var progress = new Progress<Models.InstallProgress>(p => row.ApplyInstallStatus(
            new InstallItemStatus(row.SourceId, row.Item.Id, p.PercentComplete, Loc["RowRunningInteractive"], null, null)
            {
                RebootRequired = p.RebootRequired,
            }));

        var started = System.Diagnostics.Stopwatch.StartNew();
        _logging.Event(ActivityLog.Install, ActivityLog.InstallStarted(row.Item, InstallAction.Install, elevated: false));

        try
        {
            if (RetryInteractiveOverride is not null)
                await RetryInteractiveOverride(row.Item, progress, token);
            else
                await source.InstallInteractiveAsync(row.Item, progress, token);

            _logging.Event(ActivityLog.Install, ActivityLog.InstallSucceeded(row.Item, InstallAction.Install, started.Elapsed));
            row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 100, null, true, null));
            AppendInteractiveRetryHistory(row, success: true, error: null);
            // This path never goes through ReportInstallStatus (Install.cs), the normal batch loop's status
            // dispatch: an interactive retry runs standalone, direct on the source. Every OTHER terminal
            // status already calls this from there; this is the one entry point that has to call it itself.
            TrackPersistentFailure(row);
        }
        catch (OperationCanceledException)
        {
            row.MarkNotInstalled(Loc["InstallCancelled"]);
        }
        catch (Exception ex)
        {
            // A user-triggered command handler: winget's own exception surface here is unbounded (it runs
            // out-of-process through the CLI/module), and it must resolve to a failed row, never an
            // unhandled exception on the dispatcher.
            _logging.EventFailed(ActivityLog.Install, ActivityLog.InstallFailed(row.Item, InstallAction.Install, started.Elapsed, ex.Message));
            _logging.Error("Interactive retry failed", ex);
            var message = Loc["InstallFailed"];
            row.ApplyInstallStatus(new InstallItemStatus(row.SourceId, row.Item.Id, 0, null, false, message) { Detail = ex.Message });
            AppendInteractiveRetryHistory(row, success: false, error: message, detail: ex.Message);
            TrackPersistentFailure(row);
        }
        finally
        {
            row.IsRunningInteractively = false;
            _interactiveRetryLane = null;
            if (EndInstallActivity())
                HandOffPendingInstalls();
        }
    }

    private void AppendInteractiveRetryHistory(UpdateRowViewModel row, bool success, string? error, string? detail = null) =>
        _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success, error, detail: detail));

    // A row's own sub-list (Steam's games) collapses like a category group and is remembered the same
    // way: the row is rebuilt whenever a scan streams in results, so the state cannot live on it.
    bool IRowActions.IsSubListExpanded(string key) => IsGroupExpanded(key);

    void IRowActions.SetSubListExpanded(string key, bool expanded) => SetGroupExpanded(key, expanded);

    private Services.Sources.IUpdateSource? SourceFor(UpdateItem item) =>
        _sources.FirstOrDefault(s => s.Id == item.SourceId);

    /// <summary>Test seam (null in production): answers the uninstall confirmation, which the headless
    /// dialog layer would otherwise always decline.</summary>
    internal Func<bool>? UninstallConfirmOverride { get; set; }

    /// <summary>
    /// Removing a package is destructive, so it is confirmed first and then run through the very same
    /// queue and progress window as an install: one item, action = Uninstall. Admin sources still take
    /// the elevated worker (one UAC prompt); user-scope ones run in-process.
    /// With nothing else running it owns the shared install state like any solo activity. While a batch
    /// (or another activity) runs it is a side run instead: its own concurrent queue run and token, the
    /// row appended to the progress list, no flags of its own touched. An admin uninstall then waits for
    /// the elevated worker's exclusive slot and shows as queued meanwhile. Only the very row that is
    /// already queued or running is refused.
    /// </summary>
    internal async Task UninstallRowAsync(UpdateRowViewModel row)
    {
        // A scan may be running; removing something is independent of looking for updates, and so is a
        // running install batch: the queue takes concurrent runs. Only this row's own work blocks it.
        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["UninstallBlockedByActivity"]);
            return;
        }

        var confirmed = UninstallConfirmOverride?.Invoke() ?? Dialogs.Confirm(
            string.Format(Loc["UninstallConfirmHeadline"], row.Name),
            Loc["UninstallConfirmBody"],
            confirmText: Loc["UninstallButton"],
            destructive: true);
        if (!confirmed)
            return;

        // Dialogs.Confirm pumps messages while it is up: an interactive retry's non-modal window
        // (ErrorDetailWindow, shown via ShowOwned) can finish and hand off to a fresh install loop in
        // that window, or the same row can be queued meanwhile. Re-check now: proceeding regardless
        // would run the row twice at once.
        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["UninstallBlockedByActivity"]);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Uninstall"))
            return;

        var side = _installActivityRunning;
        CancellationTokenSource? sideCts = null;
        CancellationToken token;
        if (side)
        {
            sideCts = new CancellationTokenSource();
            _sideUninstallCts.Add(sideCts);
            _sideUninstalls++;
            token = sideCts.Token;

            if (!InstallBatch.Contains(row))
                InstallBatch.Add(row);
            row.BeginInstall();
            // so the finished row reads "Uninstalled" in red rather than a green "done"
            row.WasUninstalled = true;
            RefreshInstallProgress();
        }
        else
        {
            _installCts?.Cancel();
            _installCts?.Dispose();
            _installCts = new CancellationTokenSource();
            token = _installCts.Token;
            _installActivityRunning = true;
            IsInstalling = true;

            row.BeginInstall();
            row.WasUninstalled = true;
            StartInstallProgress([row]);
        }

        var key = PackageKey(row.Item);
        _uninstallingKeys.Add(key);

        try
        {
            await _installQueue.RunAsync([row.Item], token, InstallAction.Uninstall);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A user-triggered command handler, same reasoning as RetryInteractiveAsync above: whatever the
            // source throws must end this row's uninstall, never the app. Logging alone would leave the
            // row itself stuck showing BeginInstall's running state for ever. _installQueue.RunAsync
            // normally reports failure through ReportInstallStatus, but an exception that escapes it
            // entirely (rather than a reported failed status) never reaches the row at all unless this
            // catch tells it too.
            _logging.Error("Uninstall failed", ex);
            // InstallQueue.RunAsync's own finally/FailRemaining usually already gave the row a real
            // terminal status (Failed/Succeeded/NeedsManualWork) before this exception ever reached us.
            // Only fall back to Idle if nothing did, else this would overwrite a correct result.
            if (row.InstallState is RowInstallState.Queued or RowInstallState.Running)
                row.MarkNotInstalled(Loc["UninstallFailed"]);
        }
        finally
        {
            _uninstallingKeys.Remove(key);
            if (sideCts is not null)
                EndSideUninstall(sideCts);
            else if (EndInstallActivity())
                HandOffPendingInstalls();
        }
    }

    /// <summary>The lane (package manager) an interactive retry is using right now. The retry runs outside
    /// the install queue, so a side uninstall in the same lane would run beside it and fail on the
    /// manager's own single-operation lock.</summary>
    private string? _interactiveRetryLane;

    /// <summary>The cases an uninstall is refused: this very row is already queued or running (a batch
    /// install, another uninstall, an interactive retry), or an interactive retry holds its manager.</summary>
    internal bool UninstallBlockedForRow(UpdateRowViewModel row) =>
        row.InstallState is RowInstallState.Queued or RowInstallState.Running
        || row.IsRunningInteractively
        || (_interactiveRetryLane is { } lane
            && string.Equals(lane, InstallQueue.LaneKey(row.Item), StringComparison.OrdinalIgnoreCase))
        || _uninstallingKeys.Contains(PackageKey(row.Item))
        || _installPending.Contains(row);
}
