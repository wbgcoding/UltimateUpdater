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
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels.Main;

// The per-row actions (IRowActions): install, start, uninstall and interactive retry, plus ignore,
// snooze and persistent-failure state.
public partial class MainViewModel
{
    /// <summary>Package keys with an uninstall in flight. <see cref="InstallItemStatus"/> carries no action, so <see cref="TrackPersistentFailure"/> uses this to tell an uninstall's terminal status from an install's; otherwise three failed removals would flag the package NeedsManualWork and block its install, and a removal would clear a real install-failure streak. Populated only by <see cref="UninstallRowAsync"/>, for the span of its single-item <c>_installQueue.RunAsync</c> call.</summary>
    private readonly HashSet<string> _uninstallingKeys = new(StringComparer.Ordinal);

    /// <summary>Ignoring an update means "stop telling me about it", so it stops being counted too. A snoozed row is excluded the same way.</summary>
    private IEnumerable<UpdateRowViewModel> NotIgnored => Updates.Where(u => !IsIgnored(u.Item) && !IsSnoozed(u.Item));

    /// <summary>An update is ignored by package (all versions) or by the exact available version. The version key keeps a winget "phantom" upgrade (a version that reappears after it installs) gone until a really newer one shows up.</summary>
    private bool IsIgnored(UpdateItem item) =>
        _settings.Current.Excludes.Contains(PackageKey(item))
        || (!string.IsNullOrEmpty(item.AvailableVersion) && _settings.Current.Excludes.Contains(VersionKey(item)));

    /// <summary>The same package-or-version key shape as <see cref="IsIgnored"/>, over the <c>Snoozed</c> dictionary instead of <c>Excludes</c>.</summary>
    private bool IsSnoozed(UpdateItem item) => SnoozedUntilFor(item) is not null;

    private DateTimeOffset? SnoozedUntilFor(UpdateItem item)
    {
        if (TryGetActiveSnooze(PackageKey(item), out var packageUntil))
            return packageUntil;

        if (!string.IsNullOrEmpty(item.AvailableVersion) && TryGetActiveSnooze(VersionKey(item), out var versionUntil))
            return versionUntil;

        return null;
    }

    /// <summary>Passive expiry, like <see cref="EnrichmentCache.TryGet"/>: an entry past its due date is dropped as soon as anything looks at it, so a snooze reappears without an app restart. <see cref="SettingsService"/> sweeps on load; this covers the app staying open across the due date.</summary>
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

    /// <summary>Applies the persisted snooze state to a row and its children (Steam's game rows), at the level <c>IsIgnored</c> is applied in <see cref="DrainPendingItems"/>. It also makes an expired snooze reappear on the next scan even when the "unchanged, keep the row" fast path would skip it.</summary>
    private void ApplySnoozeState(UpdateRowViewModel row)
    {
        var until = SnoozedUntilFor(row.Item);
        row.IsSnoozed = until is not null;
        row.SnoozedUntil = until;

        foreach (var child in row.Children)
            ApplySnoozeState(child);
    }

    /// <summary>Ignore this exact available version: "source:id@version".</summary>
    private static string VersionKey(UpdateItem item) => $"{item.SourceId}:{item.Id}@{item.AvailableVersion}";

    /// <summary>Ignore the package whatever its version: "source:id". Also the row-reconcile identity.</summary>
    private static string PackageKey(UpdateItem item) => $"{item.SourceId}:{item.Id}";

    /// <summary>Raised after a row action changed the ignore list (ignore, ignore all versions, un-ignore, or a snooze that replaced an ignore), so an open ignore list dialog re-reads it.</summary>
    public event Action? IgnoreMarksChanged;

    /// <summary>Toggle: ignore this version of an update, or show an ignored one again. The row leaves the table at once unless "show ignored" is on (then it stays, marked); either way only the flag, the persisted list and the counts change, the row object is never removed. Un-ignoring clears both key forms.</summary>
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
            // A manual-open row (launcher, store, even Steam with build ids) has no per-version ignore and is ignored as a whole package; real update rows ignore the current version.
            var wholePackage = string.IsNullOrEmpty(row.Item.AvailableVersion) || row.IsManualOnly;
            var key = wholePackage ? PackageKey(row.Item) : VersionKey(row.Item);
            if (!_settings.Current.Excludes.Contains(key))
                _settings.Current.Excludes.Add(key);
            row.IsIgnored = true;
            row.IsIgnoredAllVersions = wholePackage;

            // Ignore and Snooze are mutually exclusive, or a row could be marked both "never tell me" and "tell me on a date".
            ClearSnooze(row);
        }

        ReseatInVisible(row);
        Persist();
        IgnoreMarksChanged?.Invoke();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>Ignores every version of this package (the row menu's "Ignore all versions").</summary>
    void IRowActions.IgnoreAllVersions(UpdateRowViewModel row)
    {
        // The package-wide key supersedes any single-version one.
        _settings.Current.Excludes.Remove(VersionKey(row.Item));
        var key = PackageKey(row.Item);
        if (!_settings.Current.Excludes.Contains(key))
            _settings.Current.Excludes.Add(key);

        row.IsIgnored = true;
        row.IsIgnoredAllVersions = true;
        ClearSnooze(row);

        ReseatInVisible(row);
        Persist();
        IgnoreMarksChanged?.Invoke();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>"Remind me later" for a fixed duration (day, week, month). Clears any Ignore on the row first: the two are mutually exclusive.</summary>
    void IRowActions.Snooze(UpdateRowViewModel row, string duration)
    {
        _settings.Current.Excludes.Remove(PackageKey(row.Item));
        _settings.Current.Excludes.Remove(VersionKey(row.Item));
        row.IsIgnored = false;
        row.IsIgnoredAllVersions = false;

        // The same package-or-version split as Ignore: a manual-open row snoozes as a whole package, a real update row just the version on offer.
        var wholePackage = string.IsNullOrEmpty(row.Item.AvailableVersion) || row.IsManualOnly;
        var key = wholePackage ? PackageKey(row.Item) : VersionKey(row.Item);
        // Clear the other key form too, so a package-wide snooze leaves no stale per-version one that would keep matching after it expires.
        _settings.Current.Snoozed.Remove(wholePackage ? VersionKey(row.Item) : PackageKey(row.Item));

        var until = DateTimeOffset.UtcNow + SnoozeDuration(duration);
        _settings.Current.Snoozed[key] = until;
        row.IsSnoozed = true;
        row.SnoozedUntil = until;

        Persist();
        IgnoreMarksChanged?.Invoke();
        ApplyFilter();
        RefreshSelectionState();
        RefreshHeader();
    }

    /// <summary>"Show again now" clears a snooze early.</summary>
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

    /// <summary>Flat day counts rather than calendar arithmetic, like the app's other day-based settings (HistoryRetentionDays, LogSettings.RetentionDays, the enrichment cache TTL): a snooze due date needs no calendar precision.</summary>
    private static TimeSpan SnoozeDuration(string duration) => duration switch
    {
        "week" => TimeSpan.FromDays(7),
        "month" => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1),
    };

    /// <summary>"Try normally again": the only way back from <see cref="RowInstallState.NeedsManualWork"/>; clears the row's flag and the persisted tally.</summary>
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

    /// <summary>Called after every terminal install or uninstall result. A success clears the package's tally. A Failed or Stuck result counts only when <see cref="InstallErrorExplainer"/> can classify it: an unclassified failure has no signature to compare, and raw text never matches twice (paths, timestamps and versions change). Three matching signatures in a row promote the live row to NeedsManualWork; anything else records the new tally and leaves the row's state as ApplyInstallStatus set it.</summary>
    private void TrackPersistentFailure(UpdateRowViewModel row)
    {
        var key = PackageKey(row.Item);

        // An uninstall's outcome must not feed or clear the install tally for the same package (see _uninstallingKeys).
        if (_uninstallingKeys.Contains(key))
            return;

        if (row.InstallState == RowInstallState.Succeeded)
        {
            // An install whose version stayed the same counts like a failure of one reason: the same from-to pair repeating means more retries will not change it. Never ignored on its own.
            if (row.VersionUnchanged)
            {
                // A Store app waiting for its window to close is not a vendor quirk to ignore the package for.
                if (!row.IsPackagedApp)
                    TrackRepeatedUnchanged(row, key);
                return;
            }

            if (_settings.Current.PersistentFailures.Remove(key))
                Persist();
            return;
        }

        if (row.InstallState is not (RowInstallState.Failed or RowInstallState.Stuck))
            return;

        var explanation = InstallErrorExplainer.Explain(row.SourceId, row.InstallErrorDetail);
        if (explanation is not { CountsAsFailure: true })
            return;

        var signature = $"{explanation.WhatHappenedKey}:{string.Join(",", explanation.WhatHappenedArgs)}";

        // Capped and redacted like HistoryService.Append's Detail field: this is raw tool output of up to 10 M chars, which would sit unbounded in settings.json (an oversized file falls back to defaults on load) and could carry a token or profile path onto disk.
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

    /// <summary>Signature prefix of a tally that counts "installed, version unchanged" results.</summary>
    internal const string UnchangedSignaturePrefix = "unchanged:";

    private void TrackRepeatedUnchanged(UpdateRowViewModel row, string key)
    {
        var signature = $"{UnchangedSignaturePrefix}{row.Item.InstalledVersion}->{row.Item.AvailableVersion}";

        if (_settings.Current.PersistentFailures.TryGetValue(key, out var record) && record.Signature == signature)
        {
            record.Count++;
            record.LastAt = DateTimeOffset.UtcNow;
        }
        else
        {
            record = new PersistentFailureRecord
            {
                Signature = signature,
                Count = 1,
                LastAt = DateTimeOffset.UtcNow,
            };
            _settings.Current.PersistentFailures[key] = record;
        }

        if (record.Count >= 3)
            row.InstallState = RowInstallState.NeedsManualWork;

        Persist();
    }

    /// <summary>Applies a persisted 3-strike flag to a freshly scanned row, at the level <see cref="ApplySnoozeState"/> applies a snooze, so the flag survives a restart.</summary>
    private void ApplyPersistentFailureState(UpdateRowViewModel row)
    {
        if (_settings.Current.PersistentFailures.TryGetValue(PackageKey(row.Item), out var record) && record.Count >= 3)
        {
            row.InstallState = RowInstallState.NeedsManualWork;
            row.InstallErrorDetail = record.LastErrorText;
            row.PersistentTallyIsUnchanged = record.Signature.StartsWith(UnchangedSignaturePrefix, StringComparison.Ordinal);
        }

        foreach (var child in row.Children)
            ApplyPersistentFailureState(child);
    }

    /// <summary>Whether this row can offer "back to the previous version", and which one. Only a source whose <see cref="Services.Sources.IUpdateSource.SupportsVersionPinning"/> is true is offered one. Applied at the same level as <see cref="ApplyPersistentFailureState"/>, so the row reads as rollback-capable the moment it appears.</summary>
    private void ApplyDowngradeState(UpdateRowViewModel row, IReadOnlyDictionary<string, HistoryEntry> lastSuccessfulInstalls)
    {
        // A target equal to what is installed now (already rolled back, or reinstalled at that version) would offer a no-op.
        row.DowngradeTargetVersion =
            SourceFor(row.Item)?.SupportsVersionPinning == true
            && lastSuccessfulInstalls.TryGetValue(PackageKey(row.Item), out var entry)
            && !string.Equals(entry.FromVersion, row.Item.InstalledVersion, StringComparison.OrdinalIgnoreCase)
                ? entry.FromVersion
                : null;

        foreach (var child in row.Children)
            ApplyDowngradeState(child, lastSuccessfulInstalls);
    }

    void IRowActions.RetryDowngrade(UpdateRowViewModel row, string targetVersion) =>
        _ = Background.Run(DowngradeAsync(row, targetVersion), _logging, "downgrade");

    // The row menu's "back to the previous version" entry.
    void IRowActions.Downgrade(UpdateRowViewModel row)
    {
        if (row.DowngradeTargetVersion is not { Length: > 0 } targetVersion)
            return;

        _ = Background.Run(DowngradeAsync(row, targetVersion), _logging, "downgrade");
    }

    /// <summary>A second entry point for downgrade: the History window's button, offered for a package that may have no live row (it is fully current, so no source lists it). Reuses a live row for the same package rather than creating a second one that would race it for status updates (<see cref="FindRow"/> matches by source and id, never by row instance).</summary>
    internal void DowngradeFromHistory(string sourceId, string itemId, string name, string? installedVersion, string targetVersion)
    {
        var existing = FindRow(sourceId, itemId);
        var row = existing ?? CreateTransientRow(sourceId, itemId, name, installedVersion);
        _ = Background.Run(DowngradeAsync(row, targetVersion, removeRowIfNotStarted: existing is null), _logging, "downgrade");
    }

    /// <summary>Takes back a row built only for a downgrade that never started, so a declined or refused downgrade leaves no ghost entry.</summary>
    private void RemoveUnusedTransientRow(UpdateRowViewModel row)
    {
        var key = PackageKey(row.Item);
        if (_rowsByKey.TryGetValue(key, out var held) && ReferenceEquals(held, row))
            _rowsByKey.Remove(key);

        Updates.Remove(row);
    }

    /// <summary>A row for a package the table has no reason to show (fully up to date), built only so the History window's downgrade button has something to run and report progress on. Tracked in <see cref="_rowsByKey"/> and <see cref="Updates"/> so the next scan reconciles or removes it like a finished row.</summary>
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
        ResolveIconsAsync([row]);
        return row;
    }

    /// <summary>"Back to {version}". Confirmed first, with a warning that the newer version will be offered again on the next scan, then run like an uninstall (one item, the shared elevation, progress and history plumbing) with the target item's <see cref="UpdateItem.AvailableVersion"/> and <see cref="UpdateItem.PinnedVersion"/> pinned to the older version by <see cref="UpdateItem.WithPinnedVersion"/>. A solo activity like uninstall and interactive retry (see <see cref="_installActivityRunning"/>): it owns the shared progress and elevation state and cannot run beside a batch or another solo activity.</summary>
    private async Task DowngradeAsync(UpdateRowViewModel row, string targetVersion, bool removeRowIfNotStarted = false)
    {
        if (_installActivityRunning)
        {
            Dialogs.Warn(Loc["DowngradeBlockedByActivity"]);
            if (removeRowIfNotStarted)
                RemoveUnusedTransientRow(row);
            return;
        }

        // A plain hyphen for a row with no known installed version, kept out of the dialog's argument list so the hardwired-text scan does not take it for an untranslated word (HardwiredTextTests.cs).
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

        // Dialogs.Confirm pumps messages; re-check as UninstallRowAsync does after its confirmation.
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
        row.IsSelected = false;
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
            // A user-triggered handler: whatever the source throws must end this row's run, never the app.
            _logging.Error("Downgrade failed", ex);
            if (row.InstallState is RowInstallState.Queued or RowInstallState.Running)
                row.MarkNotInstalled(Loc["InstallFailed"]);
        }
        finally
        {
            var released = EndInstallActivity();
            ReportRebootsFor([row]);
            if (released)
                HandOffPendingInstalls();
        }
    }

    // The per-row "Install" button queues just that row.
    void IRowActions.InstallSingle(UpdateRowViewModel row) => _ = Background.Run(EnqueueInstallAsync([row]), _logging, "install single");

    // The per-row "stop this one" button; the rest of the queue keeps running.
    void IRowActions.CancelSingle(UpdateRowViewModel row) => _installQueue.CancelItem(row.Item.SourceId, row.Item.Id);

    // The Start and Uninstall entries on a row's context menu.

    bool IRowActions.CanStart(UpdateItem item) => SourceFor(item)?.LaunchTarget(item) is not null;

    bool IRowActions.CanUninstall(UpdateItem item) => SourceFor(item)?.CanUninstall(item) ?? false;

    void IRowActions.Start(UpdateRowViewModel row)
    {
        // The source's own target first (a launcher URI, a Steam game); otherwise the executable resolved from the uninstall registry, which gives an ordinary package a Start at all.
        var target = SourceFor(row.Item)?.LaunchTarget(row.Item) ?? row.LaunchTargetPath;
        if (string.IsNullOrEmpty(target))
            return;

        // A Store app has no exe to run; its application id is started through explorer, and nothing else starting with "shell:" gets that far (the URI launcher refuses the scheme).
        if (PackagedAppLauncher.IsPackagedAppTarget(target))
            PackagedAppLauncher.LaunchDetached(target, _logging);
        else
            UriLauncher.LaunchDetached(target, _logging);
    }

    void IRowActions.Uninstall(UpdateRowViewModel row) => _ = Background.Run(UninstallRowAsync(row), _logging, "uninstall");

    void IRowActions.RetryInteractive(UpdateRowViewModel row) => _ = Background.Run(RetryInteractiveAsync(row), _logging, "retry interactive");

    /// <summary>Test seam, set only by the tests: replaces the call to <see cref="WingetSource.InstallInteractiveAsync"/> so a test can hold the interactive retry "running". The gate in <see cref="RetryInteractiveAsync"/> (the row must resolve to a real <see cref="WingetSource"/>) stays.</summary>
    internal Func<UpdateItem, IProgress<Models.InstallProgress>, CancellationToken, Task>? RetryInteractiveOverride { get; set; }

    /// <summary>The one narrow exception to "never --interactive", with every boundary enforced here rather than by the UI hiding the entry:
    /// 1. reachable only from a click on this row's context menu (<see cref="ShowRetryInteractiveButton"/> is never read by a batch or automatic path);
    /// 2. refuses when <see cref="App.IsUnattended"/>;
    /// 3. never goes through the elevated worker: it calls <see cref="WingetSource.InstallInteractiveAsync"/> in-process, bypassing <see cref="InstallQueue"/> and <see cref="InstallQueue.NeedsElevation"/>. A per-machine install needing administrator rights is winget's own concern (it can raise its own UAC prompt) and may not succeed from here, an accepted trade-off for a last-resort retry;
    /// 4. exactly one attempt: no loop, no re-arming; the row returns to the normal Failed or Succeeded path.</summary>
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

        if (SourceFor(row.Item) is not WingetSource source)
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
        row.IsSelected = false;
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
            // This path bypasses ReportInstallStatus (Install.cs), the batch loop's status dispatch: an interactive retry runs standalone on the source. Every other terminal status calls this from there, so this entry point must call it itself.
            TrackPersistentFailure(row);
        }
        catch (OperationCanceledException)
        {
            row.MarkNotInstalled(Loc["InstallCancelled"]);
        }
        catch (Exception ex)
        {
            // A user-triggered handler: winget's exception surface is unbounded (it runs out of process through the CLI or module) and must end in a failed row, never an unhandled dispatcher exception.
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
            var released = EndInstallActivity();
            ReportRebootsFor([row]);
            if (released)
                HandOffPendingInstalls();
        }
    }

    private void AppendInteractiveRetryHistory(UpdateRowViewModel row, bool success, string? error, string? detail = null) =>
        _history.Append(HistoryEntry.ForItem(row.Item, InstallAction.Install, success, error, detail: detail));

    // A row's sub-list (Steam's games) collapses like a category group and is remembered the same way: the row is rebuilt whenever a scan streams in results, so the state cannot live on it.
    bool IRowActions.IsSubListExpanded(string key) => IsGroupExpanded(key);

    void IRowActions.SetSubListExpanded(string key, bool expanded) => SetGroupExpanded(key, expanded);

    private Services.Sources.IUpdateSource? SourceFor(UpdateItem item) =>
        _sources.FirstOrDefault(s => s.Id == item.SourceId);

    /// <summary>Test seam (null in production): answers the uninstall confirmation, which the headless dialog layer would decline.</summary>
    internal Func<bool>? UninstallConfirmOverride { get; set; }

    /// <summary>Removing a package is destructive, so it is confirmed first and then runs through the same queue and progress window as an install: one item, action Uninstall. Admin sources use the elevated worker (one UAC prompt), user-scope ones run in-process. With nothing else running it owns the shared install state like any solo activity; while a batch or another activity runs it is a side run: its own concurrent queue run and token, the row appended to the progress list, no flags touched. An admin uninstall then waits for the elevated worker's exclusive slot and shows as queued. Only a row that is already queued or running is refused.</summary>
    internal async Task UninstallRowAsync(UpdateRowViewModel row)
    {
        // A scan or a running install batch does not matter: removing something is independent of looking for updates and the queue takes concurrent runs. Only this row's own work blocks it.
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

        // Dialogs.Confirm pumps messages: an interactive retry's non-modal window (ErrorDetailWindow, via ShowOwned) can finish and hand off to a fresh loop meanwhile, or the row can be queued. Re-check, or the row would run twice at once.
        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["UninstallBlockedByActivity"]);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Uninstall"))
            return;

        await RunSingleRowActionAsync(row, InstallAction.Uninstall);
    }

    /// <summary>The run itself, after the caller confirmed and checked its gates: one row through the install queue as <paramref name="action"/> (Uninstall or FreshInstall), as a side run when another activity is going. The row's state carries the outcome. Shared so the reinstall flow can confirm once and still use the same paths as the single actions.</summary>
    private async Task RunSingleRowActionAsync(UpdateRowViewModel row, InstallAction action, UpdateItem? runItem = null)
    {
        var uninstall = action == InstallAction.Uninstall;
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
            row.IsSelected = false;
            // So the finished row reads "Uninstalled" in red rather than a green "done".
            row.WasUninstalled = uninstall;
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
            row.IsSelected = false;
            row.WasUninstalled = uninstall;
            StartInstallProgress([row]);
        }

        var key = PackageKey(row.Item);
        if (uninstall)
            _uninstallingKeys.Add(key);

        try
        {
            await _installQueue.RunAsync([runItem ?? row.Item], token, action);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A user-triggered handler, like RetryInteractiveAsync: whatever the source throws must end this row's uninstall, never the app. Logging alone would leave the row showing BeginInstall's running state for ever. _installQueue.RunAsync normally reports failure through ReportInstallStatus, but an exception that escapes it never reaches the row unless this catch tells it.
            _logging.Error($"{action} failed", ex);
            // InstallQueue.RunAsync's own finally or FailRemaining usually already gave the row a terminal status. Fall back to Idle only if nothing did, or a correct result would be overwritten.
            if (row.InstallState is RowInstallState.Queued or RowInstallState.Running)
                row.MarkNotInstalled(Loc[uninstall ? "UninstallFailed" : "InstallFailed"]);
        }
        finally
        {
            if (uninstall)
                _uninstallingKeys.Remove(key);
            if (sideCts is not null)
            {
                EndSideUninstall(sideCts);
                ReportRebootsFor([row]);
            }
            else
            {
                var released = EndInstallActivity();
                ReportRebootsFor([row]);
                if (released)
                    HandOffPendingInstalls();
            }
        }
    }

    /// <summary>Test seam (null in production): answers the reinstall confirmation.</summary>
    internal Func<bool>? ReinstallConfirmOverride { get; set; }

    /// <summary>Packages whose reinstall is between its confirmation and its end. Their row is the only way back to the program once the old version is gone, so a scan neither drops nor replaces it (<see cref="HeldByReinstall"/>).</summary>
    private readonly HashSet<string> _reinstallingKeys = new(StringComparer.Ordinal);

    /// <summary>A row a scan must leave alone: a reinstall is running on it, or its old version is removed and only the install is left (the row carries that install's retry).</summary>
    private bool HeldByReinstall(UpdateRowViewModel row) =>
        row.PendingFreshInstall || _reinstallingKeys.Contains(PackageKey(row.Item));

    void IRowActions.Reinstall(UpdateRowViewModel row) => _ = Background.Run(ReinstallRowAsync(row), _logging, "reinstall");

    void IRowActions.RetryFreshInstall(UpdateRowViewModel row) => _ = Background.Run(RetryFreshInstallAsync(row), _logging, "retry fresh install");

    /// <summary>Removes the old version and installs the new one, for winget's different-install-technology refusal. Each step runs only after the one before succeeded:
    /// 1. the source must be able to fetch the new version (<see cref="IFreshInstallSource.CanFetchAsync"/>), else nothing is touched;
    /// 2. the user confirms once, with the package named;
    /// 3. the removal runs through the row's own uninstall path;
    /// 4. the fresh install (<see cref="InstallAction.FreshInstall"/>) runs through the same queue into the scope the program had (read before the removal; unknown means no scope named), so a user-scope package is reinstalled in this process and a machine-wide one machine-wide.
    /// A failure of step 3 flags the row <see cref="UpdateRowViewModel.ReinstallRemovalFailed"/>, so its retry runs the whole flow again. A failure of step 4 flags it <see cref="UpdateRowViewModel.PendingFreshInstall"/>, so its retry repeats step 4 only (<see cref="RetryFreshInstallAsync"/>).</summary>
    internal async Task ReinstallRowAsync(UpdateRowViewModel row)
    {
        // Every gate is enforced here, not only by hiding the menu entry.
        if (!(row.ShowReinstallButton || row.ReinstallRemovalFailed) || SourceFor(row.Item) is not IFreshInstallSource source)
            return;

        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["ReinstallBlockedByActivity"]);
            return;
        }

        // Held from here, before the first await: the lookup, the confirm dialog and the scope read all leave the row looking Failed, so a second reinstall, an uninstall or a Retry would pass its own gate and work on the same package.
        var key = PackageKey(row.Item);
        _reinstallingKeys.Add(key);
        try
        {
            await ReinstallHeldRowAsync(row, source);
        }
        finally
        {
            _reinstallingKeys.Remove(key);
        }
    }

    private async Task ReinstallHeldRowAsync(UpdateRowViewModel row, IFreshInstallSource source)
    {
        _logging.Info($"Reinstall of '{row.Item.Id}': checking that the new version can be fetched.");
        bool fetchable;
        try
        {
            fetchable = await source.CanFetchAsync(row.Item, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logging.Warn($"Reinstall of '{row.Item.Id}': the lookup failed: {ex.Message}");
            fetchable = false;
        }

        if (!fetchable)
        {
            _logging.Warn($"Reinstall of '{row.Item.Id}': the new version cannot be fetched; nothing was changed.");
            Dialogs.Warn(Loc["ReinstallUnavailable"]);
            return;
        }

        var confirmed = ReinstallConfirmOverride?.Invoke() ?? Dialogs.Confirm(
            string.Format(Loc["ReinstallConfirmTitle"], row.Name),
            string.Format(Loc["ReinstallConfirmBody"], row.Name),
            confirmText: Loc["ReinstallButton"],
            destructive: true);
        if (!confirmed)
        {
            _logging.Info($"Reinstall of '{row.Item.Id}': declined; nothing was changed.");
            return;
        }

        // The confirm dialog pumps messages: the row can have been queued or changed meanwhile. This reinstall's own hold is not "activity" (UninstallBlockedForRow would refuse it).
        if (ReinstallRowGone(row))
        {
            Dialogs.Warn(Loc["ReinstallBlockedByActivity"]);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Reinstall"))
            return;

        // Before the removal: afterwards there is nothing to read the scope from.
        var scope = await ReadInstalledScopeAsync(row, source);

        // The scope read takes seconds with the row still Failed: a Retry clicked meanwhile has queued it.
        if (ReinstallRowGone(row))
        {
            Dialogs.Warn(Loc["ReinstallBlockedByActivity"]);
            return;
        }

        row.PendingFreshInstallScope = scope;
        _logging.Info($"Reinstall of '{row.Item.Id}': removing the old version.");
        await RunSingleRowActionAsync(row, InstallAction.Uninstall);
        if (row.InstallState != RowInstallState.Succeeded)
        {
            // The row carries the failed removal; its Retry reruns the whole reinstall, since a bare removal that succeeded would leave the program gone with nothing installed.
            row.ReinstallRemovalFailed = true;
            _logging.Warn($"Reinstall of '{row.Item.Id}': the removal did not finish; the new version was not installed.");
            return;
        }

        row.PendingFreshInstall = true;
        await RunFreshInstallAsync(row);
    }

    /// <summary>The reinstall's post-wait gate: the row no longer offers it, or other work took the row.</summary>
    private bool ReinstallRowGone(UpdateRowViewModel row) =>
        !(row.ShowReinstallButton || row.ReinstallRemovalFailed) || RowBusyForUninstall(row);

    private async Task<string?> ReadInstalledScopeAsync(UpdateRowViewModel row, IFreshInstallSource source)
    {
        string? scope;
        try
        {
            scope = InstallScopes.Normalize(await source.GetInstalledScopeAsync(row.Item, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logging.Warn($"Reinstall of '{row.Item.Id}': the install scope could not be read: {ex.Message}");
            scope = null;
        }

        _logging.Info($"Reinstall of '{row.Item.Id}': installed scope {scope ?? "unknown (none will be named)"}.");
        return scope;
    }

    /// <summary>The row's retry after a reinstall already removed the old version: the install alone.</summary>
    internal async Task RetryFreshInstallAsync(UpdateRowViewModel row)
    {
        if (!row.PendingFreshInstall || SourceFor(row.Item) is not IFreshInstallSource)
            return;

        if (UninstallBlockedForRow(row))
        {
            Dialogs.Warn(Loc["ReinstallBlockedByActivity"]);
            return;
        }

        if (RefusedWhileSelfUpdateApplies("Reinstall"))
            return;

        await RunFreshInstallAsync(row);
    }

    private async Task RunFreshInstallAsync(UpdateRowViewModel row)
    {
        _logging.Info($"Reinstall of '{row.Item.Id}': installing the new version.");
        await RunSingleRowActionAsync(row, InstallAction.FreshInstall, row.Item.WithInstallScope(row.PendingFreshInstallScope));

        if (row.InstallState == RowInstallState.Succeeded)
        {
            row.PendingFreshInstall = false;
            _logging.Info($"Reinstall of '{row.Item.Id}': done.");
        }
        else
        {
            _logging.Warn($"Reinstall of '{row.Item.Id}': the new version did not install; a retry repeats the install only.");
        }
    }

    /// <summary>The lane (package manager) an interactive retry is using. The retry runs outside the install queue, so a side uninstall in the same lane would run beside it and hit the manager's single-operation lock.</summary>
    private string? _interactiveRetryLane;

    /// <summary>The cases an uninstall is refused: this row is already queued or running (batch install, another uninstall, interactive retry), or an interactive retry holds its manager.</summary>
    internal bool UninstallBlockedForRow(UpdateRowViewModel row) =>
        _reinstallingKeys.Contains(PackageKey(row.Item)) || RowBusyForUninstall(row);

    /// <summary>The row's own activity, without a reinstall's hold: what a running reinstall re-checks after each wait.</summary>
    private bool RowBusyForUninstall(UpdateRowViewModel row) =>
        row.InstallState is RowInstallState.Queued or RowInstallState.Running
        || row.IsRunningInteractively
        || (_interactiveRetryLane is { } lane
            && string.Equals(lane, InstallQueue.LaneKey(row.Item), StringComparison.OrdinalIgnoreCase))
        || _uninstallingKeys.Contains(PackageKey(row.Item))
        || _installPending.Contains(row);
}
