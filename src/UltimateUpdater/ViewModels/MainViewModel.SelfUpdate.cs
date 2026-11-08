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
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.ViewModels;

// The app updating itself: the periodic and interactive checks, and installing the found update.
public partial class MainViewModel
{
    [ObservableProperty]
    private bool selfUpdateAvailable;

    [ObservableProperty]
    private string? selfUpdateVersion;

    [ObservableProperty]
    private bool selfUpdateInProgress;

    /// <summary>True while a verified download waits for running work to end before it is applied.</summary>
    [ObservableProperty]
    private bool selfUpdateWaitingForWork;

    partial void OnSelfUpdateInProgressChanged(bool value) => InstallSelfUpdateCommand.NotifyCanExecuteChanged();

    /// <summary>What the banner says while the update runs: downloading with a percentage, verifying or
    /// installing. Empty when nothing is running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelfUpdateShowsProgress))]
    private string selfUpdatePhaseText = string.Empty;

    /// <summary>0 to 100; only meaningful while <see cref="SelfUpdateProgressIndeterminate"/> is false.</summary>
    [ObservableProperty]
    private double selfUpdatePercent;

    /// <summary>True while the size is unknown or the phase has no measurable length (verifying, installing).</summary>
    [ObservableProperty]
    private bool selfUpdateProgressIndeterminate;

    /// <summary>"84.0 MB of 199.4 MB · 12.4 MB/s" during the download, empty otherwise.</summary>
    [ObservableProperty]
    private string selfUpdateDetail = string.Empty;

    /// <summary>Cancelling is possible while bytes are still arriving; once the file is being checked or
    /// started it is too late to take back.</summary>
    [ObservableProperty]
    private bool selfUpdateCanCancel;

    public bool SelfUpdateShowsProgress => SelfUpdatePhaseText.Length > 0;

    private Services.SelfUpdate.DownloadRateMeter _selfUpdateRate = new();
    private System.Diagnostics.Stopwatch _selfUpdateClock = System.Diagnostics.Stopwatch.StartNew();

    [RelayCommand]
    private void CancelSelfUpdate()
    {
        if (SelfUpdateCanCancel)
            _selfUpdateCts?.Cancel();
    }

    private void ClearSelfUpdateProgress()
    {
        SelfUpdatePhaseText = string.Empty;
        SelfUpdateDetail = string.Empty;
        SelfUpdatePercent = 0;
        SelfUpdateProgressIndeterminate = false;
        SelfUpdateCanCancel = false;
    }

    /// <summary>Shows one reading in the banner. The percentage only ever rises within a run, so a late or
    /// repeated reading never makes the bar step back.</summary>
    internal void ShowSelfUpdateProgress(Services.SelfUpdate.SelfUpdateProgress reading)
    {
        switch (reading.Phase)
        {
            case Services.SelfUpdate.SelfUpdatePhase.Downloading:
                var known = reading.Total is > 0;
                var percent = known ? Math.Min(100, (int)(reading.Read * 100 / reading.Total!.Value)) : 0;
                if (known && percent < SelfUpdatePercent)
                    percent = (int)SelfUpdatePercent;

                SelfUpdateCanCancel = true;
                SelfUpdateProgressIndeterminate = !known;
                SelfUpdatePercent = percent;
                SelfUpdatePhaseText = known
                    ? string.Format(Loc["SelfUpdatePhaseDownloading"], percent)
                    : Loc["SelfUpdatePhaseDownloadingUnknown"];

                var rate = _selfUpdateRate.Add(reading.Read, _selfUpdateClock.Elapsed);
                SelfUpdateDetail = reading.Read <= 0 || rate <= 0
                    ? string.Empty
                    : known
                        ? string.Format(Loc["SelfUpdateDetail"], ByteSize.Format(reading.Read), ByteSize.Format(reading.Total!.Value), ByteSize.Format((long)rate))
                        : string.Format(Loc["SelfUpdateDetailUnknownTotal"], ByteSize.Format(reading.Read), ByteSize.Format((long)rate));
                break;

            case Services.SelfUpdate.SelfUpdatePhase.Verifying:
                SelfUpdateCanCancel = false;
                SelfUpdateProgressIndeterminate = true;
                SelfUpdateDetail = string.Empty;
                SelfUpdatePhaseText = Loc["SelfUpdatePhaseVerifying"];
                break;
        }
    }

    private void ShowSelfUpdateInstalling()
    {
        SelfUpdateCanCancel = false;
        SelfUpdateProgressIndeterminate = true;
        SelfUpdateDetail = string.Empty;
        SelfUpdatePhaseText = Loc["SelfUpdatePhaseInstalling"];
    }

    /// <summary>Applying the update ends the process; an install batch or a cleanup run still in flight would
    /// be killed half way, so the command waits until neither is running.</summary>
    private bool CanInstallSelfUpdate => !IsInstalling && !IsCleanupRunning && !SelfUpdateInProgress;

    private Services.SelfUpdate.AvailableUpdate? _pendingSelfUpdate;

    /// <summary>Checked in the background at start; never interrupts, only offers.</summary>
    public async Task CheckForSelfUpdateAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Current.SelfUpdate.Enabled || !_settings.Current.SelfUpdate.CheckOnStart)
            return;

        await RunSelfUpdateCheckAsync(cancellationToken);
    }

    /// <summary>The interval scan also asks about a new app version, so a long-running copy keeps current.</summary>
    public async Task CheckForSelfUpdatePeriodicAsync(CancellationToken cancellationToken)
    {
        // already offering one, or the user switched self-update off: nothing to do
        if (!_settings.Current.SelfUpdate.Enabled || SelfUpdateAvailable)
            return;

        await RunSelfUpdateCheckAsync(cancellationToken);
    }

    /// <summary>
    /// The settings window's "Check now": the user asked, so it runs regardless of the on-start/interval
    /// preferences and reports back whether anything was found, nothing was, or the check could not be
    /// completed. A found update still surfaces as the banner on the main window.
    /// </summary>
    public async Task<Services.SelfUpdate.SelfUpdateCheckOutcome> CheckForUpdatesInteractiveAsync()
    {
        // Its own token rather than None: pressing "check now" again supersedes a check that is
        // still waiting on the network instead of leaving it running with nothing able to stop it.
        // A token of its own, not the download's: sharing one meant "Check now" in Settings silently cancelled
        // an update download already running from the banner, and the update never installed.
        _selfUpdateCheckCts?.Cancel();
        _selfUpdateCheckCts?.Dispose();
        _selfUpdateCheckCts = new CancellationTokenSource();

        var outcome = await RunSelfUpdateCheckAsync(_selfUpdateCheckCts.Token);

        // An update already on offer stays the answer even when this later check could not reach the feed.
        return SelfUpdateAvailable ? Services.SelfUpdate.SelfUpdateCheckOutcome.Found : outcome;
    }

    private CancellationTokenSource? _selfUpdateCts;
    private CancellationTokenSource? _selfUpdateCheckCts;

    private async Task<Services.SelfUpdate.SelfUpdateCheckOutcome> RunSelfUpdateCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Only a Found result replaces the offer: a check that found nothing or could not finish (a
            // timed-out request, a channel that briefly went unreachable) must never wipe an update already
            // on offer, which would leave the banner up with nothing for Install or the changelog link.
            var result = await _selfUpdate.CheckAsync(cancellationToken);
            if (result.Update is { } update)
            {
                // A newer release makes a held verified file of an older one useless; one already being
                // applied (or waiting for running work) is left alone.
                if (!SelfUpdateInProgress && !_selfUpdateApplying)
                    _selfUpdate.DiscardVerifiedExcept(update.Version);

                _pendingSelfUpdate = update;
                SelfUpdateVersion = update.Version;
                SelfUpdateAvailable = true;
            }

            return result.Outcome;
        }
        catch (OperationCanceledException)
        {
            return Services.SelfUpdate.SelfUpdateCheckOutcome.Failed;
        }
        catch (Exception ex)
        {
            // a failed update check must never disturb normal use
            _logging.Warn($"Self-update check failed: {ex.Message}");
            return Services.SelfUpdate.SelfUpdateCheckOutcome.Failed;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallSelfUpdate))]
    private async Task InstallSelfUpdateAsync()
    {
        if (_pendingSelfUpdate is null || !CanInstallSelfUpdate)
            return;

        SelfUpdateInProgress = true;
        var keepInProgress = false;
        try
        {
            _selfUpdateCts?.Cancel();
            _selfUpdateCts?.Dispose();
            _selfUpdateCts = new CancellationTokenSource();

            // Created here on the dispatcher, so readings from the download thread are posted back to it
            // and the window never waits on the network.
            // A fresh meter with the fresh clock: the old one would hold the last run's readings and
            // report a stale or zero speed until the new clock passed the old run's time.
            _selfUpdateRate = new Services.SelfUpdate.DownloadRateMeter();
            _selfUpdateClock = System.Diagnostics.Stopwatch.StartNew();
            ShowSelfUpdateProgress(new Services.SelfUpdate.SelfUpdateProgress(Services.SelfUpdate.SelfUpdatePhase.Downloading));
            var progress = new Progress<Services.SelfUpdate.SelfUpdateProgress>(ShowSelfUpdateProgress);

            // A verified file that survived a declined elevation prompt is applied again as it is: downloading
            // it a second time would cost the whole transfer for nothing.
            var held = SelfUpdateHeldPathForTest is { } heldForTest
                ? heldForTest(_pendingSelfUpdate.Version)
                : _selfUpdate.VerifiedPathFor(_pendingSelfUpdate.Version);
            var download = held is not null
                ? new Services.SelfUpdate.SelfUpdateDownloadResult(Services.SelfUpdate.SelfUpdateDownloadOutcome.Downloaded, held)
                : await (SelfUpdateDownloadForTest?.Invoke(_pendingSelfUpdate, _selfUpdateCts.Token)
                    ?? _selfUpdate.DownloadVerifiedAsync(_pendingSelfUpdate, _selfUpdateCts.Token, progress));
            if (download.Path is not { } verifiedPath)
            {
                if (download.Outcome == Services.SelfUpdate.SelfUpdateDownloadOutcome.SignatureInvalid)
                {
                    OfferReleasePageAfterBadSignature();
                    return;
                }

                // Network and verification failures read differently: one is worth a retry, the other says
                // the file was refused.
                var message = download.Outcome == Services.SelfUpdate.SelfUpdateDownloadOutcome.NetworkFailed
                    ? Loc["SelfUpdateDownloadFailed"]
                    : Loc["SelfUpdateFailed"];
                Dialogs.Warn(message);
                return;
            }

            // Applying ends the process, so an install, a cleanup or a drive optimisation that is still
            // running would be killed half way. The verified file is kept and applied by itself the moment
            // the last of them ends; the update stays "in progress" meanwhile so it cannot be started twice.
            if (WorkIsRunning)
            {
                _verifiedSelfUpdatePath = verifiedPath;
                SelfUpdateWaitingForWork = true;
                keepInProgress = true;
                DefragRunState.RunEnded += OnDefragRunEnded;

                // The run may have ended between the check above and the subscription.
                ApplyWaitingSelfUpdateWhenIdle();
                return;
            }

            await ApplyVerifiedSelfUpdateAsync(verifiedPath);
        }
        catch (OperationCanceledException)
        {
            // the user started something that replaced this download (a new check): nothing to report
        }
        catch (Exception ex)
        {
            // A failure must not escape the command: it would reach the dispatcher and the crash handler
            // would end the app, while this action has to fail closed and quietly.
            _logging.Error("Self-update failed", ex);
            Dialogs.Warn(Loc["SelfUpdateApplyFailed"]);
        }
        finally
        {
            ClearSelfUpdateProgress();
            if (!keepInProgress)
                SelfUpdateInProgress = false;
        }
    }

    internal const string ReleasePageUrl = SettingsViewModel.RepositoryUrl + "/releases/latest";

    /// <summary>Test-only: replaces the dialog that tells the user the update was not validly signed.</summary>
    internal Func<bool>? SignatureRefusedPromptForTest { get; set; }

    /// <summary>Nothing was installed. The user can still fetch the release by hand, so the notice offers
    /// the release page next to its plain close button.</summary>
    private void OfferReleasePageAfterBadSignature()
    {
        var open = SignatureRefusedPromptForTest?.Invoke()
            ?? Dialogs.Confirm(Loc["SelfUpdateSignatureInvalid"], string.Empty, Loc["SelfUpdateOpenReleasePage"], Loc["DialogOk"]);
        if (open)
            UriLauncher.LaunchDetached(ReleasePageUrl, _logging);
    }

    /// <summary>Install work counts from the moment it owns the install machinery or has rows waiting for
    /// it, not only while <see cref="IsInstalling"/> is set: a solo activity clears that flag a moment before
    /// it hands rows queued meanwhile to a new batch.</summary>
    internal bool WorkIsRunning =>
        IsInstalling || _installActivityRunning || _installPending.Count > 0 || _installPreflightsOpen > 0
        || IsCleanupRunning || DefragRunState.IsRunning;

    /// <summary>How many pre-flight dialogs of an install request are open right now. The rows are not in
    /// the queue yet while one is up, but the request is already work that applying would cut off.</summary>
    private int _installPreflightsOpen;

    private string? _verifiedSelfUpdatePath;

    /// <summary>Set from the moment a verified update starts being applied until applying failed. Applying
    /// ends the process (the installer closes it, or the portable swap starts the new copy), so no install
    /// may start meanwhile; see <see cref="RefusedWhileSelfUpdateApplies"/>.</summary>
    private bool _selfUpdateApplying;

    // Off the UI thread: applying a portable update re-hashes a ~100 MB file through the handle it
    // pinned, and doing that on the dispatcher froze the window for as long as the disk took.
    private async Task ApplyVerifiedSelfUpdateAsync(string verifiedPath)
    {
        // Set on the UI thread in the same step as the caller's own "no work running" check, so no install
        // can start between that check and the process ending.
        _selfUpdateApplying = true;
        var result = Services.SelfUpdate.SelfUpdateApplyResult.Failed;
        ShowSelfUpdateInstalling();
        try
        {
            var apply = SelfUpdateApplyForTest ?? _selfUpdate.Apply;
            result = await Task.Run(() => apply(verifiedPath));
        }
        finally
        {
            if (result != Services.SelfUpdate.SelfUpdateApplyResult.Started)
            {
                _selfUpdateApplying = false;
                ClearSelfUpdateProgress();
            }
        }

        // A declined elevation prompt is the user's own answer: no failure dialog, the banner just offers
        // the install again.
        if (result == Services.SelfUpdate.SelfUpdateApplyResult.Started)
            (SelfUpdateExitForTest ?? ExitForSelfUpdate)();
        else if (result == Services.SelfUpdate.SelfUpdateApplyResult.Failed)
            Dialogs.Warn(Loc["SelfUpdateApplyFailed"]);
    }

    /// <summary>True, with a log line, when an install, uninstall, retry or rollback must not start because
    /// the app is applying its own update and about to end.</summary>
    private bool RefusedWhileSelfUpdateApplies(string what, bool tellUser = false)
    {
        if (!_selfUpdateApplying)
            return false;

        _logging.Info($"{what} not started: the app is applying its own update and is about to restart.");
        if (tellUser)
            Dialogs.Info(Loc["SelfUpdateApplyingNoInstall"]);

        return true;
    }

    /// <summary>Called whenever one of the three kinds of running work ends: applies the waiting update once
    /// none of them is left. Driven by change notifications, never by polling.</summary>
    private void ApplyWaitingSelfUpdateWhenIdle()
    {
        if (!SelfUpdateWaitingForWork || WorkIsRunning || _verifiedSelfUpdatePath is not { } path)
            return;

        SelfUpdateWaitingForWork = false;
        _verifiedSelfUpdatePath = null;
        DefragRunState.RunEnded -= OnDefragRunEnded;

        _ = Background.Run(ApplyWaitingSelfUpdateAsync(path), _logging, "apply self update");
    }

    private async Task ApplyWaitingSelfUpdateAsync(string path)
    {
        try
        {
            await ApplyVerifiedSelfUpdateAsync(path);
        }
        catch (Exception ex)
        {
            _logging.Error("Self-update failed", ex);
            Dialogs.Warn(Loc["SelfUpdateApplyFailed"]);
        }
        finally
        {
            SelfUpdateInProgress = false;
        }
    }

    // A drive optimisation ends on a pool thread; the view model state is touched on the dispatcher only.
    private void OnDefragRunEnded() => _dispatcher.InvokeAsync(ApplyWaitingSelfUpdateWhenIdle);

    // Through the shared exit path, not a bare Shutdown, so the tray and window close handling agree
    // that this is a real exit and not a close to the tray.
    private static void ExitForSelfUpdate() => (Application.Current as App)?.ExitApplication();

    /// <summary>At app exit: a verified download the user never started (a declined elevation prompt kept it)
    /// is removed instead of waiting for the next start's sweep.</summary>
    internal void DiscardUnstartedSelfUpdate()
    {
        if (!_selfUpdateApplying && !SelfUpdateWaitingForWork)
            _selfUpdate.DiscardUnstartedVerified();
    }

    internal Func<Services.SelfUpdate.AvailableUpdate, CancellationToken, Task<Services.SelfUpdate.SelfUpdateDownloadResult>>? SelfUpdateDownloadForTest { get; set; }

    internal Func<string, Services.SelfUpdate.SelfUpdateApplyResult>? SelfUpdateApplyForTest { get; set; }

    /// <summary>Test-only: answers which verified file is held for a version, in place of the service.</summary>
    internal Func<string, string?>? SelfUpdateHeldPathForTest { get; set; }

    internal Action? SelfUpdateExitForTest { get; set; }

    internal void OfferSelfUpdateForTest(Services.SelfUpdate.AvailableUpdate update)
    {
        _pendingSelfUpdate = update;
        SelfUpdateVersion = update.Version;
        SelfUpdateAvailable = true;
    }

    [RelayCommand]
    private void DismissSelfUpdate() => SelfUpdateAvailable = false;

    /// <summary>
    /// The banner's third button: shows the release notes of the offered version through the existing
    /// changelog window, reusing the release the check already resolved. Never touches the install path;
    /// installing stays its own separate, deliberate click.
    /// </summary>
    [RelayCommand]
    private void ShowSelfUpdateChangelog()
    {
        if (_pendingSelfUpdate is null)
            return;

        ShowOwned(new Views.ChangelogWindow(
            new ChangelogViewModel(Loc["AppTitle"], _pendingSelfUpdate.Version, _pendingSelfUpdate.ReleaseNotes, null, _logging)));
    }
}
