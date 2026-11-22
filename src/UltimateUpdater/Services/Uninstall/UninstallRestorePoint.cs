using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Models.Settings;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Install;

namespace UltimateUpdater.Services.Uninstall;

public enum UninstallRestorePointOutcome
{
    /// <summary>A new restore point was created.</summary>
    Created,

    /// <summary>One made within the last 24 hours (or one being made right now) covers this run.</summary>
    Reused,

    /// <summary>None could be created: Windows refused, System Restore is off, or the helper was declined.</summary>
    Failed,
}

/// <param name="At">When the point in use was made; null when only a pending one exists (another batch is creating it).</param>
public sealed record UninstallRestorePointResult(UninstallRestorePointOutcome Outcome, DateTimeOffset? At = null);

/// <summary>Whether a run warrants a restore point. Pure.</summary>
public static class UninstallRestorePointPolicy
{
    /// <summary>
    /// Automatic asks when a program that needs administrator rights is removed (such a setup can change the system)
    /// or a row rated Risky is among them; Always asks for every run; Off never.
    /// </summary>
    public static bool Wanted(UninstallBackupMode mode, bool anyElevated, bool anyRisky) => mode switch
    {
        UninstallBackupMode.Always => true,
        UninstallBackupMode.Automatic => anyElevated || anyRisky,
        _ => false,
    };
}

/// <summary>
/// The restore point before an uninstall run. It follows the install queue's rules: at most one per 24 hours across
/// the whole app (<see cref="RestorePointThrottle"/>, with <see cref="RestorePointReservation"/> closing the gap
/// while one is still being made), made by the elevated helper only, and the helper reports it over the pipe while
/// this side writes the timestamp. It is its own small elevated job (no entry attached), because the run may have no
/// machine removal that could carry the flag, and a failure must be answerable before anything is removed.
/// </summary>
public sealed class UninstallRestorePointService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LoggingService _logging;
    private readonly Func<CancellationToken, Task<DateTimeOffset?>> _create;
    private readonly Func<DateTimeOffset?> _readLast;
    private readonly Action<DateTimeOffset> _writeLast;
    private readonly RestorePointReservation _reservation;
    private readonly Func<DateTimeOffset> _now;

    public UninstallRestorePointService(LoggingService logging, ElevatedHost? host)
        : this(
            logging,
            cancellationToken => RunHelperJobAsync(host, logging, cancellationToken),
            () => RestorePointThrottle.TryReadLastCreated(logging),
            at => RestorePointThrottle.TryWriteCreated(logging, at),
            RestorePointReservation.Shared,
            () => DateTimeOffset.UtcNow)
    {
    }

    /// <param name="create">Makes the restore point; the time it was made, null when it could not be.</param>
    internal UninstallRestorePointService(
        LoggingService logging,
        Func<CancellationToken, Task<DateTimeOffset?>> create,
        Func<DateTimeOffset?> readLast,
        Action<DateTimeOffset> writeLast,
        RestorePointReservation reservation,
        Func<DateTimeOffset> now)
    {
        _logging = logging;
        _create = create;
        _readLast = readLast;
        _writeLast = writeLast;
        _reservation = reservation;
        _now = now;
    }

    public async Task<UninstallRestorePointResult> EnsureAsync(CancellationToken cancellationToken)
    {
        // Decided and reserved in one step, handed back however this ends (see the install queue).
        using var lease = _reservation.TryReserve(_readLast, _now());
        if (lease is null)
        {
            _logging.Info("Restore point before uninstalling skipped: one was already created within the last 24 hours.");
            return new UninstallRestorePointResult(UninstallRestorePointOutcome.Reused, _readLast());
        }

        var created = await _create(cancellationToken);
        if (created is not { } at)
            return new UninstallRestorePointResult(UninstallRestorePointOutcome.Failed);

        _writeLast(at);
        return new UninstallRestorePointResult(UninstallRestorePointOutcome.Created, at);
    }

    private static async Task<DateTimeOffset?> RunHelperJobAsync(
        ElevatedHost? host, LoggingService logging, CancellationToken cancellationToken)
    {
        if (host is null)
            return null;

        DateTimeOffset? created = null;
        var reachedDone = false;

        bool OnLine(string line)
        {
            InstallProgressMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<InstallProgressMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // a malformed worker line is ignored, never fatal
            }

            switch (message?.Type)
            {
                case "done":
                    reachedDone = true;
                    return false;
                case "log":
                    logging.Info($"worker: {message.Status}");
                    break;
                case "restorePointCreated":
                    created = message.RestorePointCreatedAt ?? DateTimeOffset.UtcNow;
                    break;
            }

            return true;
        }

        var job = new UninstallProgramJob { CreateRestorePoint = true, StopIfRestorePointFails = true };
        var ok = await host.RunAsync("uninstall-program", job, OnLine, cancellationToken);
        return ok && reachedDone ? created : null;
    }
}
