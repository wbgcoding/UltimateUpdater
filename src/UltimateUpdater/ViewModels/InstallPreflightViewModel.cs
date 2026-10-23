using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// Backs the one dialog shown before an install batch starts, when there is anything at all to ask or
/// warn about (see <see cref="MainViewModel.Install"/>'s candidate resolution): a disk-space or
/// metered-connection or low-battery warning, and/or one line per group of rows blocked by
/// a running program. Opened only when <see cref="Lines"/> is non-empty; a batch with nothing
/// to say never shows this window at all, so the normal case costs no extra click.
/// </summary>
public sealed partial class InstallPreflightViewModel : ObservableObject
{
    private readonly LoggingService _logging;
    private readonly bool _restorePointRelevant;

    public LocalizationService Loc => LocalizationService.Instance;

    public ObservableCollection<InstallPreflightLineViewModel> Lines { get; }

    /// <summary>True once one of the start buttons closed the window; false for Cancel or for closing it
    /// any other way (title bar, Escape), the safe/no-op reading, same convention as every other dialog here.</summary>
    public bool Started { get; private set; }

    /// <summary>At least one line is a blocker line. Then the window asks ONCE, with its own bottom
    /// buttons, how to handle every blocked group (close the programs, skip them, try anyway), and each
    /// of those buttons starts the batch right away; without blockers it shows the plain Start.</summary>
    public bool HasBlockers { get; }

    /// <summary>How every blocker line is answered; set by whichever start button closed the window.
    /// Null after plain Start (no blockers) or Cancel; every caller reads null as Skip.</summary>
    public ThreeWayChoice? BlockerChoice { get; private set; }

    /// <summary>Raised once, when either button is pressed; the window subscribes and closes itself.</summary>
    public event Action? RequestClose;

    /// <summary>"Last restore point: {date}", or the "none yet" line when this program has never
    /// created one. Always shown here, independent of <see cref="Lines"/> (which only gates whether this
    /// whole window opens at all): once the window IS open for some other reason, this is worth knowing
    /// too.</summary>
    public string RestorePointText => RestorePointThrottle.DescribeLastCreated(_logging);

    /// <summary>Gates <c>RestorePointRow</c>'s visibility: only a batch that can really create a restore
    /// point (<c>restorePointRelevant</c>: an admin row is in it and the setting is on) mentions one, and
    /// the row stays hidden when <see cref="RestorePointText"/> has nothing to say.</summary>
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

    /// <summary>Opens the same "Create a restore point" dialog Windows itself ships
    /// (System32\rstrui.exe, full path so this never resolves through %PATH%); this app cannot show that
    /// information any other way; the window it opens is the user's to use, this app does not drive it.</summary>
    [RelayCommand]
    private void OpenRestorePointDialog()
    {
        if (OpenRestorePointDialogOverride is not null)
        {
            OpenRestorePointDialogOverride();
            return;
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "rstrui.exe");
        UriLauncher.LaunchDetached(path, _logging);
    }
}
