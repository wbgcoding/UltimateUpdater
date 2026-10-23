using System.Diagnostics;
using System.IO;
using System.Text;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit.Abstractions;

namespace UltimateUpdater.Tests;

/// <summary>
/// The source prüfstand: runs EVERY registered source's real availability probe and real scan on this
/// machine and reports what each one did, detected or not, how long it took, how many rows, and the exact
/// error if it failed.
/// Off by default and skipped in the normal suite: it starts external tools, reaches the network, and takes
/// minutes (pip alone measures 15-19 s, winget about 16 s, `sdkmanager --list` fetches a remote repository).
/// Turn it on deliberately:
/// WU_PROBE=1 dotnet test --filter FullyQualifiedName~SourceProbeTests
/// It never fails the build on a source that is simply absent, "not installed here" is a normal answer and
/// most of these tools are not on any one machine. It DOES fail when a source that reports itself available
/// then throws, because that is a real defect: the table would show a red error pill.
/// </summary>
[Collection("UiLanguage")]
public class SourceProbeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan PerSourceCap = TimeSpan.FromMinutes(3);

    private sealed record Probe(
        string Id,
        SourceTier Tier,
        bool Available,
        int Rows,
        long Milliseconds,
        string? Error,
        string Sample);

    [Fact]
    public async Task Every_source_probes_and_scans()
    {
        if (Environment.GetEnvironmentVariable("WU_PROBE") != "1")
        {
            output.WriteLine("skipped: set WU_PROBE=1 to run the source prüfstand.");
            return;
        }

        var logging = new LoggingService();
        LocalizationService.Instance.Apply("en");

        var sources = SourceRegistry.CreateAll(logging);
        output.WriteLine($"{sources.Count} sources registered.");

        // Parallel, exactly as the app scans: a source that blocks must not add its wait to every other's.
        var probes = await Task.WhenAll(sources.Select(source => ProbeAsync(source)));

        var report = Report(probes);
        output.WriteLine(report);

        var path = Path.Combine(Path.GetTempPath(), "ultimateupdater-source-probe.md");
        await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));
        output.WriteLine($"written to {path}");

        // The one thing that IS a defect: a source that says it is here and then cannot be scanned.
        var broken = probes.Where(p => p is { Available: true, Error: not null }).ToList();
        Assert.True(
            broken.Count == 0,
            "Sources reported themselves available and then failed:" + Environment.NewLine
            + string.Join(Environment.NewLine, broken.Select(p => $"  {p.Id}: {p.Error}")));
    }

    /// <summary>
    /// What one tool ACTUALLY prints, through the same ProcessRunner the sources use. A parser written from
    /// documentation is a guess until this has been read once, and a source that quietly reports "no
    /// updates" because its tool failed looks identical to one that works.
    /// WU_PROBE_EXE=winget WU_PROBE_ARGS="upgrade --include-unknown" dotnet test --filter SourceProbeTests
    /// </summary>
    [Fact]
    public async Task One_tool_shows_its_raw_output()
    {
        if (Environment.GetEnvironmentVariable("WU_PROBE_EXE") is not { Length: > 0 } exe)
        {
            output.WriteLine("skipped: set WU_PROBE_EXE (and optionally WU_PROBE_ARGS) to dump a tool's output.");
            return;
        }

        var arguments = (Environment.GetEnvironmentVariable("WU_PROBE_ARGS") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var result = await ProcessRunner.RunAsync(exe, arguments, PerSourceCap, CancellationToken.None);

        output.WriteLine($"exe      : {exe}");
        output.WriteLine($"args     : {string.Join(" ", arguments)}");
        output.WriteLine($"exit     : {result.ExitCode}   timedOut: {result.TimedOut}");
        output.WriteLine($"stdout   : {result.StandardOutput.Length} chars");
        output.WriteLine($"stderr   : {result.StandardError.Length} chars");
        output.WriteLine("--- stdout (first 2000) ---");
        output.WriteLine(Head(result.StandardOutput, 2000));
        output.WriteLine("--- stderr (first 1000) ---");
        output.WriteLine(Head(result.StandardError, 1000));
    }

    private static string Head(string text, int chars) =>
        text.Length <= chars ? text : text[..chars] + "…";

    private static async Task<Probe> ProbeAsync(IUpdateSource source)
    {
        var stopwatch = Stopwatch.StartNew();
        var available = false;
        var rows = 0;
        string? error = null;
        var sample = string.Empty;

        using var timeout = new CancellationTokenSource(PerSourceCap);

        try
        {
            available = await source.IsAvailableAsync(timeout.Token);

            if (available)
            {
                var names = new List<string>();
                await foreach (var item in source.ScanAsync(timeout.Token))
                {
                    rows++;
                    if (names.Count < 3)
                        names.Add($"{item.Name} {item.InstalledVersion}->{item.AvailableVersion}");
                }

                sample = Flatten(string.Join(" / ", names));
            }
        }
        catch (OperationCanceledException)
        {
            error = $"timed out after {PerSourceCap.TotalSeconds:0} s";
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Flatten(ex.Message)}";
        }

        stopwatch.Stop();
        return new Probe(source.Id, source.Tier, available, rows, stopwatch.ElapsedMilliseconds, error, sample);
    }

    /// <summary>One line per source, a tool's error text is routinely several paragraphs of its own.</summary>
    private static string Flatten(string text)
    {
        var single = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()));

        // The report is a markdown table; a stray pipe would split a cell.
        single = single.Replace('|', '/');

        return single.Length > 240 ? single[..240] + "…" : single;
    }

    private static string Report(IReadOnlyList<Probe> probes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Source probe");
        builder.AppendLine();
        builder.AppendLine($"Machine: {Environment.MachineName}, {probes.Count} sources.");
        builder.AppendLine();
        builder.AppendLine("| Source | Tier | Detected | Rows | ms | Note |");
        builder.AppendLine("|---|---|---|---|---|---|");

        foreach (var probe in probes.OrderBy(p => p.Tier).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            var note = probe.Error is { } error ? "ERROR " + error
                : !probe.Available ? "not installed here"
                : probe.Sample.Length > 0 ? probe.Sample
                : "no updates";

            builder.AppendLine(
                $"| {probe.Id} | {probe.Tier} | {(probe.Available ? "yes" : "no")} | {probe.Rows} | {probe.Milliseconds} | {note} |");
        }

        return builder.ToString();
    }
}
