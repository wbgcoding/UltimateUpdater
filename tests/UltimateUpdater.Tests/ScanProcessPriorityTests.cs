using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// An update scan starts many tool processes at once; they must yield to whatever the user is doing. The
/// scan scope marks "this work is a scan", and only inside it do launches start at below-normal priority.
/// Installs never enter it, so they keep the normal priority they wait on.
/// </summary>
public class ScanProcessPriorityTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    // The delay first: the managed route sets the class right after the start, and a child that spawned its
    // own child within that instant would hand on the old class. Real tools take far longer than this.
    private static readonly string[] PriorityProbe =
    [
        "/c",
        "ping -n 2 127.0.0.1 >nul & powershell -NoProfile -Command (Get-Process -Id $PID).PriorityClass",
    ];

    [Fact]
    public async Task The_scope_is_off_by_default_on_inside_and_flows_into_started_work_but_not_back_out()
    {
        Assert.False(ScanScope.IsActive);

        using (ScanScope.Enter())
        {
            Assert.True(ScanScope.IsActive);
            Assert.True(await Task.Run(() => ScanScope.IsActive));
            await Task.Delay(1);
            Assert.True(ScanScope.IsActive);
        }

        Assert.False(ScanScope.IsActive);
    }

    [Fact]
    public async Task A_scope_entered_inside_an_async_method_does_not_leak_to_its_caller()
    {
        await EnterAndAwaitAsync();

        Assert.False(ScanScope.IsActive);
    }

    private static async Task EnterAndAwaitAsync()
    {
        using var scope = ScanScope.Enter();
        await Task.Delay(1);
        Assert.True(ScanScope.IsActive);
    }

    [Fact]
    public void Creation_flags_carry_below_normal_only_when_asked_for()
    {
        const uint belowNormal = 0x00004000;

        Assert.Equal(0u, SuspendedChildProcess.CreationFlags(lowPriority: false) & belowNormal);
        Assert.Equal(belowNormal, SuspendedChildProcess.CreationFlags(lowPriority: true) & belowNormal);

        // The launch flags the verification depends on are the same either way.
        const uint suspended = 0x00000004;
        Assert.NotEqual(0u, SuspendedChildProcess.CreationFlags(lowPriority: false) & suspended);
        Assert.NotEqual(0u, SuspendedChildProcess.CreationFlags(lowPriority: true) & suspended);
    }

    [Fact]
    public async Task The_orchestrator_runs_every_source_probe_and_scan_inside_the_scope()
    {
        var source = new ProbeSource();
        var orchestrator = new ScanOrchestrator([source], new LoggingService());

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.True(source.ProbeInScope);
        Assert.True(source.ScanInScope);
        Assert.False(ScanScope.IsActive);
    }

    [Fact]
    public async Task A_managed_child_started_inside_the_scope_runs_below_normal()
    {
        using var executable = new ResolvedExecutable(Cmd, null);

        string inside;
        using (ScanScope.Enter())
            inside = await ClassOfChildAsync(executable, elevated: false);
        var outside = await ClassOfChildAsync(executable, elevated: false);

        Assert.Equal("BelowNormal", inside);
        Assert.Equal(Process.GetCurrentProcess().PriorityClass.ToString(), outside);
    }

    [Fact]
    public async Task A_suspended_alias_child_started_inside_the_scope_runs_below_normal()
    {
        ProcessRunner.UnpackagedImageForTest.Value = true;
        using var targetPin = new FileStream(Cmd, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var aliasStandIn = File.OpenHandle(Cmd);
        using var executable = new ResolvedExecutable(Cmd, null, targetPin, aliasStandIn);

        string inside;
        using (ScanScope.Enter())
            inside = await ClassOfChildAsync(executable, elevated: true);
        var outside = await ClassOfChildAsync(executable, elevated: true);

        Assert.Equal("BelowNormal", inside);
        Assert.Equal(Process.GetCurrentProcess().PriorityClass.ToString(), outside);
    }

    private static async Task<string> ClassOfChildAsync(ResolvedExecutable executable, bool elevated)
    {
        var result = await ProcessRunner.RunResolvedAsync(
            executable, PriorityProbe, TimeSpan.FromSeconds(60), CancellationToken.None, null, null, null, null, elevated);
        Assert.Equal(0, result.ExitCode);
        return result.StandardOutput.Trim();
    }

    private sealed class ProbeSource : IUpdateSource
    {
        public bool ProbeInScope { get; private set; }

        public bool ScanInScope { get; private set; }

        public string Id => "probe";

        public UpdateCategory Category => UpdateCategory.Software;

        public SourceTier Tier => SourceTier.A;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            ProbeInScope = ScanScope.IsActive;
            return Task.FromResult(true);
        }

        public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            ScanInScope = ScanScope.IsActive;
            yield break;
        }

        public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
