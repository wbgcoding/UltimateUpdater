namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// The outcome of one target's cleanup. <paramref name="SkippedBytes"/> is what could NOT be removed
/// because something else had the file open, on a live Windows that is a normal, sizeable share of a temp
/// folder, so it is reported rather than quietly missing from the total.
/// <paramref name="WasStopped"/> is true only for a target that swallows its own cancellation instead of
/// letting it propagate (Disk Cleanup, it has to kill cleanmgr cleanly before returning); the caller uses
/// it to keep the row from reading "done" for a target that never actually finished.
/// <paramref name="DriverBackupFolderName"/> is set only by driver cleanup: the timestamped folder name the
/// elevated worker exported packages into under its own ProgramData staging root, so the unelevated caller
/// can best-effort copy that folder into the data folder afterwards. <paramref name="TimedOut"/> is true
/// when Disk Cleanup was ended because it ran past its time limit: not the user's Stop, and not a finished
/// run either, so the row has to say it failed.
/// <paramref name="ProtectedFiles"/> and <paramref name="ProtectedBytes"/> are the part of the skipped
/// figures that was refused (no access, or the safety check said no) rather than in use: without admin
/// rights that is most of a system folder, and calling it "in use" would send the user hunting for a
/// program that holds nothing.
/// </summary>
public readonly record struct CleanupOutcome(
    long FreedBytes, int DeletedFiles, int SkippedFiles, long SkippedBytes = 0, bool WasStopped = false,
    string? DriverBackupFolderName = null, bool TimedOut = false, int ProtectedFiles = 0, long ProtectedBytes = 0);
