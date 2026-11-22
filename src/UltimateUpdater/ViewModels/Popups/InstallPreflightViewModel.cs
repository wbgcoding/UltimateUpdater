using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UltimateUpdater.ViewModels.Popups;

/// <summary>Backs the one dialog shown before an install batch starts, when there is anything to ask or warn about: a disk-space, metered-connection or low-battery warning, and/or one line per group of rows blocked by a running program. Opened only when <see cref="Lines"/> is non-empty.</summary>
public sealed partial class InstallPreflightViewModel : ObservableObject
{
    private readonly LoggingService _logging;
    private readonly bool _restorePointRelevant;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<InstallPreflightLineViewModel> Lines { get; }

    /// <summary>True once a start button closed the window; false for Cancel or any other way of closing it.</summary>
    public bool Started { get; private set; }

    /// <summary>At least one line is a blocker line. The window then asks once, with its bottom buttons, how to handle every blocked group (close the programs, skip them, try anyway), and each button starts the batch; without blockers it shows the plain Start.</summary>
    public bool HasBlockers { get; }

    /// <summary>How every blocker line is answered; set by whichever start button closed the window.
    /// Null after plain Start (no blockers) or Cancel; every caller reads null as Skip.</summary>
    public ThreeWayChoice? BlockerChoice { get; private set; }

    /// <summary>Raised once, when either button is pressed; the window subscribes and closes itself.</summary>
    public event Action? RequestClose;

    /// <summary>"Last restore point: {date}", or the "none yet" line. Shown whenever the window is open, whatever opened it.</summary>
    public string RestorePointText => RestorePointThrottle.DescribeLastCreated(_logging);

    /// <summary>Gates the restore point row: only a batch that can create a restore point (an admin row is in it and the setting is on) mentions one, and only when <see cref="RestorePointText"/> has something to say.</summary>
    public bool HasRestorePointText => _restorePointRelevant && !string.IsNullOrEmpty(RestorePointText);

    /// <summary>Test seam: replaces the real rstrui.exe launch so a test never pops a live system dialog.</summary>
    internal Action? OpenRestorePointDialogOverride { get; set; }

    public InstallPreflightViewModel(
        IReadOnlyList<InstallPreflightLineViewModel> lines, LoggingService logging, bool restorePointRelevant)
    {
        _restorePointRelevant = restorePointRelevant;
        Lines = new ObservableCollection<InstallPreflightLineViewModel>(lines);
        HasBlockers = lines.Any(line => line.IsBlocker);
        _logging = logging;
    }

    [RelayCommand]
    private void Start() => StartWith(null);

    [RelayCommand]
    private void StartClosingPrograms() => StartWith(ThreeWayChoice.Primary);

    [RelayCommand]
    private void StartSkippingBlocked() => StartWith(ThreeWayChoice.Secondary);

    [RelayCommand]
    private void StartTryingAnyway() => StartWith(ThreeWayChoice.Tertiary);

    private void StartWith(ThreeWayChoice? blockerChoice)
    {
        BlockerChoice = blockerChoice;
        Started = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        BlockerChoice = null;
        Started = false;
        RequestClose?.Invoke();
    }

    /// <summary>Opens the "Create a restore point" dialog Windows ships (System32\rstrui.exe, full path so it never resolves through %PATH%). The window it opens is the user's to use.</summary>
    [RelayCommand]
    private void OpenRestorePointDialog()
    {
        if (OpenRestorePointDialogOverride is not null)
        {
            OpenRestorePointDialogOverride();
            return;
        }

        RestorePointService.OpenWindowsDialog(_logging);
    }
}
