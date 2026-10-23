namespace UltimateUpdater.Models;

/// <summary>
/// What the elevated worker receives: a plain list of item references, never live
/// COM/object handles: the worker re-resolves each item from its own source.
/// </summary>
public sealed class InstallJobFile
{
    public int SchemaVersion { get; set; } = 1;
    public bool CreateRestorePoint { get; set; }

    /// <summary>Whether the worker installs or uninstalls the items. A restore point is only made for installs.</summary>
    public InstallAction Action { get; set; } = InstallAction.Install;

    /// <summary>The app's UI language, so the worker reports back in it rather than in the OS language.</summary>
    public string Language { get; set; } = "system";

    /// <summary>
    /// How many installs the worker may run at once. Set by the app from the user's setting; the worker
    /// never trusts this blindly and clamps it to 1-5 itself before use. The user-scope and admin halves
    /// are separate processes and run side by side, each with its own budget of this size.
    /// </summary>
    public int MaxParallelInstalls { get; set; } = 1;

    public List<InstallJobItem> Items { get; set; } = [];
}
