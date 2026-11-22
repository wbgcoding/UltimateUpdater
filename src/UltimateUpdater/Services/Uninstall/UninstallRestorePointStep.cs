namespace UltimateUpdater.Services.Uninstall;

/// <summary>How the restore point step ended: whether the removal may go on, and the time of the earlier restore point when one within 24 hours was reused.</summary>
public sealed record UninstallRestorePointStepResult(bool Proceed, DateTimeOffset? ReusedAt);

/// <summary>
/// The step before every uninstall run, in the window and from a row of the update table: asks the setting and the
/// kind of removal whether a restore point is wanted, makes (or reuses) it, and when none could be made asks whether
/// to go on without. Shared so both entry points follow one rule.
/// </summary>
public static class UninstallRestorePointStep
{
    /// <param name="confirmWithout">Asks whether to remove anyway after a failure; only called then.</param>
    /// <param name="onCreating">Raised just before the (slow) restore point is made, so a caller can say so.</param>
    public static async Task<UninstallRestorePointStepResult> RunAsync(
        IUninstallerBackend backend,
        bool anyElevated,
        bool anyRisky,
        Func<bool> confirmWithout,
        LoggingService logging,
        Action? onCreating,
        CancellationToken cancellationToken)
    {
        if (!UninstallRestorePointPolicy.Wanted(backend.BackupMode, anyElevated, anyRisky))
            return new UninstallRestorePointStepResult(true, null);

        onCreating?.Invoke();
        UninstallRestorePointResult result;
        try
        {
            result = await backend.CreateRestorePointAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new UninstallRestorePointStepResult(false, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logging.Warn($"Restore point before uninstalling failed: {ex.Message}");
            result = new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);
        }

        var reusedAt = result is { Outcome: UninstallRestorePointOutcome.Reused, At: { } at } ? at : (DateTimeOffset?)null;
        return new UninstallRestorePointStepResult(
            result.Outcome != UninstallRestorePointOutcome.Failed || confirmWithout(), reusedAt);
    }
}
