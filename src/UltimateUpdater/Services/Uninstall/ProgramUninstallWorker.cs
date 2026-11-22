using System.IO;
using System.Text.Json;
using UltimateUpdater.Models;
using UltimateUpdater.Services.Elevation;
using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>What the app asks the elevated worker to remove: which entry, and by which method. Nothing here is
/// a command; the worker reads the command from the machine hive itself.</summary>
public sealed class UninstallProgramJob
{
    /// <summary>The registry key of the entry, under one of the two machine Uninstall roots.</summary>
    public string? Key { get; set; }

    /// <summary>The <see cref="UninstallMethod"/> the app planned; the worker plans again and refuses a
    /// different answer.</summary>
    public string? Method { get; set; }

    /// <summary>The winget id the app found for method Winget (it is not stored in the registry). It is only compared
    /// with the id the worker derives itself and never reaches a command line.</summary>
    public string? WingetId { get; set; }
}

/// <summary>
/// The "uninstall-program" job inside the elevated worker. The pipe is authenticated, but the payload is still
/// treated as a request, never an instruction: the entry is re-read from HKLM (a machine key an unelevated
/// process cannot write), planned again with the same rules as the app, and refused when the plan differs from
/// the one asked for. A key outside the machine roots (a per-user entry, anything else) is refused before any
/// read. No command text ever arrives over the pipe.
/// </summary>
internal static class ProgramUninstallWorker
{
    /// <summary>Registry key names stop at 255 characters.</summary>
    private const int MaxLeafLength = 255;

    private const int MaxLogLines = 200;
    private const int MaxLogLineLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>The plan the worker would run, or the reason it will not.</summary>
    internal sealed record Decision(UninstallPlan? Plan, string? Refusal);

    /// <summary>The leaf of <paramref name="key"/> when it is a key directly under a machine Uninstall root, else null.</summary>
    internal static string? MachineLeaf(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;

        foreach (var root in WindowsRegistryRoots.Uninstall.Where(r => r.StartsWith(@"HKLM\", StringComparison.Ordinal)))
        {
            if (!key.StartsWith(root + @"\", StringComparison.OrdinalIgnoreCase))
                continue;

            var leaf = key[(root.Length + 1)..];
            return leaf.Length is > 0 and <= MaxLeafLength
                   && leaf.IndexOfAny(['\\', '/']) < 0
                   && !leaf.Any(char.IsControl)
                ? leaf
                : null;
        }

        return null;
    }

    /// <summary>
    /// Re-resolves a job against the machine hive and the file system. The key must be one only administrators
    /// can write (<paramref name="keyGuard"/>), and the winget id is derived from <paramref name="wingetRows"/>
    /// (the worker's own `winget list` run) for the re-read entry; the id on the wire only has to agree with it.
    /// </summary>
    internal static Decision Resolve(
        UninstallProgramJob? job, IRegistryView registry, IUninstallFileProbe probe, IUninstallKeyGuard keyGuard,
        IReadOnlyList<WingetListRow>? wingetRows = null)
    {
        if (job is null || MachineLeaf(job.Key) is not { } leaf)
            return Refuse("the entry key is not a machine uninstall key");

        if (!Enum.TryParse(job.Method, ignoreCase: false, out UninstallMethod method) || !Enum.IsDefined(method)
            || method is UninstallMethod.Refused or UninstallMethod.StoreApp)
            return Refuse("the method is not one the worker runs");

        var root = job.Key![..^(leaf.Length + 1)];
        if (!registry.SubKeyNames(root).Contains(leaf, StringComparer.OrdinalIgnoreCase))
            return Refuse("the entry is not installed");

        // The registry's own spelling of the key, not the wire's.
        var key = $@"{root}\{registry.SubKeyNames(root).First(n => string.Equals(n, leaf, StringComparison.OrdinalIgnoreCase))}";
        if (UninstallRiskClassifier.IsOwnEntry(key))
            return Refuse("the entry is this app's own");

        // Before a single value is read: the content of a key a user could write is not content to act on.
        if (!keyGuard.IsAdminOnly(key))
            return Refuse("the entry's registry key can be changed by users");

        var entry = InstalledProgramInventory.ReadEntry(registry, key, UninstallScope.Machine);
        if (entry is null)
            return Refuse("the entry is not a removable program");

        if (method == UninstallMethod.Winget)
        {
            var derived = WingetListParser.AttachIds([entry], wingetRows ?? [])[0].WingetId;
            if (string.IsNullOrWhiteSpace(derived))
                return Refuse("winget does not list this entry under exactly one id");

            if (!string.Equals(job.WingetId, derived, StringComparison.OrdinalIgnoreCase))
                return Refuse("the winget id asked for is not the one winget lists for this entry");

            entry = entry with { WingetId = derived };
        }

        var plan = UninstallPlanner.Plan(entry, probe);
        if (!plan.IsRunnable)
            return Refuse($"the entry cannot be removed automatically ({plan.Refusal})");

        if (plan.Method != method)
            return Refuse($"the entry's own data plans {plan.Method}, not the requested {method}");

        if (!plan.Elevated)
            return Refuse("a machine entry must plan as elevated");

        return new Decision(plan, null);
    }

    private static Decision Refuse(string reason) => new(null, reason);

    public static async Task RunJobAsync(
        JsonElement payload, StreamWriter writer, string jobId, LoggingService logging, CancellationToken cancellationToken)
    {
        UninstallProgramJob? job;
        try
        {
            job = payload.Deserialize<UninstallProgramJob>(JsonOptions);
        }
        catch (JsonException)
        {
            job = null;
        }

        IReadOnlyList<WingetListRow>? wingetRows = null;
        if (job?.Method == nameof(UninstallMethod.Winget)
            && await new WingetListReader().ListAsync(cancellationToken) is { } listing)
            wingetRows = WingetListParser.Parse(listing);

        var decision = Resolve(
            job, WindowsRegistryView.Instance, new FileSystemUninstallProbe(), new RegistryUninstallKeyGuard(), wingetRows);
        if (decision.Plan is not { } plan)
        {
            logging.Warn($"Elevated uninstall refused: {decision.Refusal}");
            Finish(writer, jobId, job?.Key, success: false, exitCode: null, error: decision.Refusal, detail: null);
            return;
        }

        Send(writer, jobId, new InstallProgressMessage
        {
            Type = "progress", SourceId = "program", ItemId = job!.Key, Status = plan.Method.ToString(),
        });

        var lines = 0;
        void OnOutput(string line)
        {
            if (Interlocked.Increment(ref lines) > MaxLogLines)
                return;

            var text = line.Trim();
            if (text.Length == 0)
                return;

            Send(writer, jobId, new InstallProgressMessage
            {
                Type = "log", Status = text.Length > MaxLogLineLength ? text[..MaxLogLineLength] : text,
            });
        }

        try
        {
            var result = await UninstallExecution.RunAsync(plan, OnOutput, cancellationToken);
            var summary = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            logging.Info($"Elevated uninstall of an entry finished with exit code {result.ExitCode}.");
            Finish(writer, jobId, job.Key, result.ExitCode is 0 or 3010 or 1641, result.ExitCode, error: null, detail: summary,
                result.TimedOut);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UntrustedToolException or Security.DeniedCommandException
                                       or System.ComponentModel.Win32Exception or IOException
                                       or InvalidOperationException or TimeoutException)
        {
            logging.Warn($"Elevated uninstall could not run: {ex.Message}");
            Finish(writer, jobId, job.Key, success: false, exitCode: null, error: ex.Message, detail: null);
        }
    }

    private static void Finish(
        StreamWriter writer, string jobId, string? key, bool success, int? exitCode, string? error, string? detail,
        bool timedOut = false)
    {
        Send(writer, jobId, new InstallProgressMessage
        {
            Type = "result",
            SourceId = "program",
            ItemId = key,
            Success = success,
            ExitCode = exitCode,
            RebootRequired = exitCode is 3010 or 1641,
            Error = timedOut ? "timed out" : error,
            Detail = detail,
        });
        Send(writer, jobId, new InstallProgressMessage { Type = "done" });
    }

    private static void Send(StreamWriter writer, string jobId, InstallProgressMessage message) =>
        ElevatedWorker.Send(writer, jobId, message);
}
