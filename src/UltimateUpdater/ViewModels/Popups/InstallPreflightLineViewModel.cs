using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UltimateUpdater.ViewModels.Popups;

/// <summary>One line in <see cref="InstallPreflightViewModel"/>, in two shapes sharing one class because a batch can mix both and the window renders them from one list: a plain warning (disk space, metered connection, battery) with an optional side action that fires at once, and a blocker line (one per group of rows blocked by the same running program, see <see cref="FromBlockedGroups"/>) that carries no buttons, since the window asks once, at the bottom, how to handle every blocked group.</summary>
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

    /// <summary>Set for a Store app: only processes running from this folder are closed.</summary>
    internal string? BlockedOnlyInsideFolder { get; private init; }

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

    /// <summary>One line per group of rows blocked by the same running program (see <see cref="Services.Install.InstallBlockers.GroupBlockedRows{TRow}"/>), each answered together by the window's buttons. Pure with respect to WPF, so a test can call it with hand-built rows.</summary>
    public static IReadOnlyList<InstallPreflightLineViewModel> FromBlockedGroups(
        IEnumerable<(UpdateRowViewModel Row, Services.Install.BlockedInstall Blocker)> blocked)
    {
        var loc = LocalizationService.Instance;

        return Services.Install.InstallBlockers.GroupBlockedRows(blocked)
            .Select(group =>
            {
                // Every package the group stands for, then the program blocking them: one line per group, not one dialog.
                var packageNames = string.Join(", ", group.Rows.Select(r => r.Name).Distinct());
                var text = string.Format(loc["InstallBlockersLine"], packageNames, group.Blocker.DisplayProgramName);

                return new InstallPreflightLineViewModel(text)
                {
                    BlockedRows = group.Rows,
                    BlockedProcessNames = group.Blocker.ProcessNames,
                    BlockedOnlyInsideFolder = group.Blocker.OnlyInsideFolder,
                    BlockedProgramName = group.Blocker.DisplayProgramName,
                };
            })
            .ToList();
    }
}
