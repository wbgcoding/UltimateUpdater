#if DEBUG
using System.Windows;
using UltimateUpdater.ViewModels.History;
using UltimateUpdater.ViewModels.Popups;
using UltimateUpdater.Views.History;
using UltimateUpdater.Views.Popups;

namespace UltimateUpdater.Review;

internal static partial class ReviewCatalog
{
    private const string DialogArea = "dialog";

    private const string LongBody =
        "The installer reports that the program is still running in the background and holds several files open. "
        + "Close it from the tray, wait a few seconds and try again. If the message comes back, restart the PC "
        + "first and run the update again before anything else is opened. A very long package name such as "
        + "Microsoft.VisualStudio.2022.BuildTools.Preview.WithAllWorkloadsAndComponents must wrap, not overflow.";

    private static void AddDialogs(List<ReviewSurface> list)
    {
        void Message(string id, string description, DialogKind kind, Func<ReviewSession, MessageDialogViewModel> viewModel) =>
            Add(list, id, DialogArea, description, s => Task.FromResult(
                new ReviewScene { Window = new MessageDialogWindow(viewModel(s)) }));

        Message("dialog.message.info", "Message box, information", DialogKind.Info, s => new(
            DialogKind.Info, "Ultimate Updater", "Nothing to do", "All sources are up to date.", "OK"));
        Message("dialog.message.question", "Message box, question with two buttons", DialogKind.Question, s => new(
            DialogKind.Question, "Ultimate Updater", "Install the selected updates?", "Five updates will be installed. This can take a few minutes.",
            "Install", "Cancel"));
        Message("dialog.message.warning", "Message box, warning with details", DialogKind.Warning, s => new(
            DialogKind.Warning, "Ultimate Updater", "A restart is needed", LongBody, "Restart now", "Later",
            details: "Exit code 3010\nPending file rename operations: 4"));
        Message("dialog.message.error", "Message box, error with a long body", DialogKind.Error, s => new(
            DialogKind.Error, "Ultimate Updater", "The update could not be installed", LongBody, "Close", "Show log",
            details: string.Join(Environment.NewLine, Enumerable.Range(1, 14).Select(i => $"Line {i}: the installer returned a non-zero exit code and printed a long diagnostic message here"))));
        Message("dialog.message.success", "Message box, success", DialogKind.Success, s => new(
            DialogKind.Success, "Ultimate Updater", "Done", "Everything was installed.", "OK"));
        Message("dialog.message.destructive", "Message box, destructive confirmation with acknowledge box", DialogKind.Warning, s => new(
            DialogKind.Warning, "Ultimate Updater", "Remove these files for good?", LongBody, "Delete", "Cancel",
            destructive: true, acknowledgeText: "I understand that this cannot be undone"));
        Message("dialog.message.threeway", "Message box with three buttons", DialogKind.Question, s => new(
            DialogKind.Question, "Ultimate Updater", "Keep the changes?", "The settings were changed and not saved.", "Save", "Discard",
            tertiaryText: "Keep editing"));

        Add(list, "dialog.preflight", DialogArea, "Install pre-flight with a critical line and an action", s =>
        {
            var lines = new List<InstallPreflightLineViewModel>
            {
                new("The battery is at 14 percent and the PC is not plugged in.", isCritical: true, actionText: "Check again", action: () => { }),
                new("Microsoft Edge is running and will be closed during its update."),
                new("A restart will be needed after the driver update."),
            };
            return Task.FromResult(new ReviewScene
            {
                Window = new InstallPreflightWindow(new InstallPreflightViewModel(lines, s.Logging, restorePointRelevant: true)),
            });
        });

        Add(list, "dialog.errordetail", DialogArea, "Error detail of a failed row, with explanation and retry", s =>
            Task.FromResult(new ReviewScene
            {
                Window = new ErrorDetailWindow(new ErrorDetailViewModel(
                    "Microsoft Visual Studio Build Tools 2022", "winget", "17.8.3 → 17.10.5", LongBody + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, Enumerable.Range(1, 18).Select(i => $"[{i:00}] installer output line with a path C:\\Program Files\\Microsoft Visual Studio\\2022\\BuildTools\\Common7\\IDE\\file{i}.dll")),
                    s.Logging, explanation: "The installer could not replace a file that another program holds open.", canRetryInteractive: true,
                    detailSourceKey: "winget", reinstall: () => { })),
            }));

        Add(list, "dialog.changelog", DialogArea, "Changelog window with release notes", s =>
            Task.FromResult(new ReviewScene
            {
                Window = new ChangelogWindow(new ChangelogViewModel(
                    "Git", "2.54.0", "## What changed\n\n- Faster status on large repositories\n- Fixed a crash when a path contains a very long directory name that does not fit on one line at all and so has to wrap\n- New `--stat` option\n\n"
                    + string.Join("\n", Enumerable.Range(1, 20).Select(i => $"- Change number {i} of the release")),
                    "https://example.invalid/releases/2.54.0", s.Logging)),
            }));

        Add(list, "dialog.changelog.empty", DialogArea, "Changelog window with no text, link only", s =>
            Task.FromResult(new ReviewScene
            {
                Window = new ChangelogWindow(new ChangelogViewModel("Git", "2.54.0", null, "https://example.invalid/releases/2.54.0", s.Logging)),
            }));

        Add(list, "dialog.ignorelist.empty", DialogArea, "Ignore list with no entries", s =>
        {
            var settings = s.NewSettings();
            return Task.FromResult(new ReviewScene { Window = new IgnoreListWindow(new IgnoreListViewModel(settings)) });
        });

        Add(list, "dialog.ignorelist", DialogArea, "Ignore list with several entries", s =>
        {
            var settings = s.NewSettings();
            settings.Current.Excludes.AddRange(["Git.Git", "Discord.Discord", "Some.Very.Long.Package.Identifier.That.Needs.Room", "Microsoft.GameInput"]);
            var names = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Git.Git"] = "Git",
                ["Discord.Discord"] = "Discord",
            };
            return Task.FromResult(new ReviewScene { Window = new IgnoreListWindow(new IgnoreListViewModel(settings, names)) });
        });

        Add(list, "dialog.shortcuts", DialogArea, "Keyboard shortcuts list", s =>
            Task.FromResult(new ReviewScene { Window = new ShortcutsWindow() }));

        Add(list, "dialog.history.empty", DialogArea, "Update history with no entries", s =>
        {
            var viewModel = new HistoryViewModel(s.NewHistory(), null, null, s.NewSettings(), null);
            return Task.FromResult(new ReviewScene { Window = new HistoryWindow(viewModel, s.Logging) });
        });

        Add(list, "dialog.history", DialogArea, "Update history with successes, failures and an uninstall", s =>
        {
            var history = s.NewHistory();
            var now = DateTimeOffset.Now;
            HistoryEntry Entry(int minutesAgo, string source, string id, string name, string? from, string? to, bool ok, string? error = null, string action = "install") =>
                new()
                {
                    Timestamp = now.AddMinutes(-minutesAgo), SourceId = source, ItemId = id, Name = name, FromVersion = from, ToVersion = to,
                    Success = ok, Error = error, Detail = error, Action = action, SizeBytes = 42_000_000,
                };

            history.Append(Entry(5, "winget", "Git.Git", "Git", "2.53.0", "2.54.0", true));
            history.Append(Entry(9, "winget", "Discord.Discord", "Discord", "1.0.9240", "1.0.9248", false, "Exit code 1603: fatal error during installation"));
            history.Append(Entry(70, "pip", "requests", "requests", "2.31.0", "2.32.0", false, LongBody));
            history.Append(Entry(1500, "npm", "typescript", "typescript", "5.4.2", "5.5.0", true));
            history.Append(Entry(3000, "winget", "Some.Removed.App", "Some removed app with a rather long display name", "3.1", null, true, action: "uninstall"));
            var viewModel = new HistoryViewModel(history, null, null, s.NewSettings(), null);
            return Task.FromResult(new ReviewScene { Window = new HistoryWindow(viewModel, s.Logging) });
        });
    }
}
#endif
