using System.IO;
using System.Text.Json;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.ViewModels;

namespace UltimateUpdater.Tests;

/// <summary>
/// An "analyze" command is a real DISM call, run through the same worker path as every other
/// cleanup command, but it names no catalogue target and never touches <see
/// cref="CleanupWorker.ResolveElevatedTargets"/> at all. Runs the real dism.exe (present on every Windows
/// box this app ships to); unelevated it answers "administrator rights required" in a couple of seconds,
/// which is enough to prove the dispatch without needing a live admin session.
/// </summary>
public class CleanupWorkerComponentStoreTests
{
    [Fact]
    public async Task An_analyze_command_reports_one_component_store_message_then_done()
    {
        var logging = new LoggingService();
        var command = new CleanupCommand { Command = "analyze", Language = "en" };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };

        await CleanupWorker.RunCommandAsync(command, writer, "job-1", logging, CancellationToken.None, new PauseGate());

        var types = ReadPayloadTypes(stream);

        Assert.Equal(["componentStore", "done"], types);
    }

    /// <summary>An unrecognised target id list is simply irrelevant: "analyze" never resolves targets at
    /// all, so it cannot be tricked into also scanning or cleaning something by riding along on the same
    /// command.</summary>
    [Fact]
    public async Task An_analyze_command_ignores_any_target_ids_it_is_given()
    {
        var logging = new LoggingService();
        var command = new CleanupCommand { Command = "analyze", Language = "en", TargetIds = ["user-temp"] };

        using var stream = new MemoryStream();
        var writer = new StreamWriter(stream) { AutoFlush = true };

        await CleanupWorker.RunCommandAsync(command, writer, "job-1", logging, CancellationToken.None, new PauseGate());

        var types = ReadPayloadTypes(stream);

        Assert.DoesNotContain("scan", types);
        Assert.DoesNotContain("result", types);
        Assert.DoesNotContain("progress", types);
    }

    private static List<string> ReadPayloadTypes(MemoryStream stream)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, leaveOpen: true);
        var types = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is { Length: > 0 })
        {
            using var doc = JsonDocument.Parse(line);
            types.Add(doc.RootElement.GetProperty("payload").GetProperty("type").GetString() ?? string.Empty);
        }

        return types;
    }
}

/// <summary>
/// <see cref="CleanupViewModel.ApplyComponentStoreMessage"/> is the pure mapping from one
/// "componentStore" worker message to the analysis it carries, tested directly with a fabricated message,
/// no pipe or worker needed.
/// </summary>
public class ApplyComponentStoreMessageTests
{
    [Fact]
    public void Every_field_carries_over_from_the_message()
    {
        var message = new CleanupWorkerMessage
        {
            Type = "componentStore",
            ComponentStoreActualSizeBytes = 5_000_000_000,
            ComponentStoreSharedWithWindowsBytes = 4_000_000_000,
            ComponentStoreBackupsBytes = 500_000_000,
            ComponentStoreCleanupRecommended = true,
        };

        var result = CleanupViewModel.ApplyComponentStoreMessage(message);

        Assert.Equal(5_000_000_000, result.ActualSizeBytes);
        Assert.Equal(4_000_000_000, result.SharedWithWindowsBytes);
        Assert.Equal(500_000_000, result.BackupsAndDisabledFeaturesBytes);
        Assert.True(result.CleanupRecommended);
    }

    /// <summary>A field DISM's own output did not carry stays null through the mapping, never defaulted
    /// to zero or false.</summary>
    [Fact]
    public void A_field_the_worker_never_set_stays_null_rather_than_defaulting()
    {
        var message = new CleanupWorkerMessage { Type = "componentStore" };

        var result = CleanupViewModel.ApplyComponentStoreMessage(message);

        Assert.Null(result.ActualSizeBytes);
        Assert.Null(result.SharedWithWindowsBytes);
        Assert.Null(result.BackupsAndDisabledFeaturesBytes);
        Assert.Null(result.CleanupRecommended);
    }

    /// <summary>DISM being killed after its own deadline must map to a distinguishable, non-default
    /// flag rather than silently looking like a completed run with nothing to report.</summary>
    [Fact]
    public void A_timed_out_worker_message_carries_the_flag_over()
    {
        var message = new CleanupWorkerMessage { Type = "componentStore", ComponentStoreTimedOut = true };

        var result = CleanupViewModel.ApplyComponentStoreMessage(message);

        Assert.True(result.TimedOut);
        Assert.Null(result.ActualSizeBytes);
    }
}
