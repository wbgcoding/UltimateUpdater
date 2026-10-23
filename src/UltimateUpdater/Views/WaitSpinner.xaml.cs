namespace UltimateUpdater.Views;

/// <summary>
/// A small spinning ring for "queued, not started yet". Used by the update table's waiting rows and by the
/// cleanup dialog's targets that are still ahead of the run.
/// </summary>
public partial class WaitSpinner
{
    public WaitSpinner() => InitializeComponent();
}
