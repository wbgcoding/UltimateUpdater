using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UltimateUpdater.Services;

namespace UltimateUpdater.ViewModels;

/// <summary>
/// One line in <see cref="InstallPreflightViewModel"/>. Two independent shapes share this class rather
/// than splitting into two view models, because a preflight batch can mix both kinds and the window
/// renders them from a single list:
/// - a plain warning (disk space, metered connection, battery) with an optional SIDE action that fires
///   immediately and does not gate the window's own buttons (e.g. "Open cleanup");
/// - a blocker line (one per group of rows blocked by the same running program, see
///   <see cref="FromBlockedGroups"/>). It carries no buttons of its own: the window asks once, at the
///   bottom, how to handle every blocked group together.
/// </summary>
public sealed partial class InstallPreflightLineViewModel : ObservableObject
{
    public string Text { get; }

    /// <summary>Disk-space-short line only: painted in the critical brush instead of the normal one.</summary>
    public bool IsCritical { get; }

    public string? ActionText { get; }

    public bool HasAction => ActionText is not null;

    private readonly Action? _action;

    /// <summary>True only for a blocker line; a plain warning line never carries blocked rows.</summary>
    public bool IsBlocker => BlockedRows is not null;

    /// <summary>Only set on a blocker line: the rows it stands for, and the process names to close if the
    /// user picks "close and continue". Null on a plain warning line.</summary>
    internal IReadOnlyList<UpdateRowViewModel>? BlockedRows { get; private init; }

    internal IReadOnlyList<string>? BlockedProcessNames { get; private init; }

    /// <summary>Only set on a blocker line: the blocking program's display name, for the status text of
    /// a row that was left out because that program is still open.</summary>
    internal string? BlockedProgramName { get; private init; }

    public InstallPreflightLineViewModel(
        string text,
        bool isCritical = false,
        string? actionText = null,
        Action? action = null)
    {
        Text = text;
        IsCritical = isCritical;
        ActionText = actionText;
        _action = action;
    }

    /// <summary>The list item's UI Automation name is read from this; without it a screen reader announces
    /// the type name. The line text already names the packages and the program holding them.</summary>
    public override string ToString() => Text;

    [RelayCommand]
    private void Act() => _action?.Invoke();

    /// <summary>
    /// One line per group of rows blocked by the SAME running program (never per row, see
    /// <see cref="Services.Install.InstallBlockers.GroupBlockedRows{TRow}"/>: a batch of five blocked
    /// packages, two of them held up by the identical program, reads as four lines, not five), each
    /// answered together by the window's own buttons. Pure with respect to WPF (no window involved), so a test
    /// can call this directly with hand-built rows and blockers.
    /// </summary>
    public static IReadOnlyList<InstallPreflightLineViewModel> FromBlockedGroups(
        IEnumerable<(UpdateRowViewModel Row, Services.Install.BlockedInstall Blocker)> blocked)
    {
        var loc = LocalizationService.Instance;

        return Services.Install.InstallBlockers.GroupBlockedRows(blocked)
            .Select(group =>
            {
                // The final one-line shape: every package the group stands for, then the program
                // blocking them, so a batch of several blocked packages reads as one line per group
                // instead of one dialog per group.
                var packageNames = string.Join(", ", group.Rows.Select(r => r.Name).Distinct());
                var text = string.Format(loc["InstallBlockersLine"], packageNames, group.Blocker.DisplayProgramName);

                return new InstallPreflightLineViewModel(text)
                {
                    BlockedRows = group.Rows,
                    BlockedProcessNames = group.Blocker.ProcessNames,
                    BlockedProgramName = group.Blocker.DisplayProgramName,
                };
            })
            .ToList();
    }
}
