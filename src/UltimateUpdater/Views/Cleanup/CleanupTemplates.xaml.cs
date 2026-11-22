using System.Windows;

namespace UltimateUpdater.Views.Cleanup;

/// <summary>The cleanup window's styles, converters and item templates. The handlers its templates name live here, since a template's event handlers resolve against the dictionary that holds it.</summary>
public partial class CleanupTemplates
{
    public CleanupTemplates()
    {
        InitializeComponent();
    }

    /// <summary>The window's log, handed over by <see cref="CleanupWindow"/> right after it loads this dictionary.</summary>
    internal LoggingService Logging { get; set; } = null!;

    /// <summary>A detail row's right-click "open folder". Goes through <see cref="UriLauncher.LaunchDetached"/>: ShellExecute blocks until the shell takes over, which froze this dialog for seconds with a cold Explorer. The helper also refuses a path that does not exist.</summary>
    private void OnEntryOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CleanupEntryViewModel { OpenPath.Length: > 0 } entry)
            return;

        UriLauncher.LaunchDetached(entry.OpenPath, Logging);
    }

    /// <summary>Same clipboard pattern as <see cref="MainWindow.OnErrorPillRightClick"/>: a locked clipboard is transient and not worth surfacing.</summary>
    private void OnEntryCopyPathClick(object sender, RoutedEventArgs e)
    {
        // A recycled file's original path: the $R name inside the bin is of no use to anyone.
        if ((sender as FrameworkElement)?.DataContext is not CleanupEntryViewModel { PathText.Length: > 0 } entry)
            return;

        try
        {
            System.Windows.Clipboard.SetText(entry.PathText);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Logging.Info("Cleanup entry copy-path: clipboard was locked by another process.");
        }
    }
}
