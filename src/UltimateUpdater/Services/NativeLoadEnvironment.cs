namespace UltimateUpdater.Services;

/// <summary>
/// Environment variables that make the .NET runtime load a native library or assembly named by the user
/// (profilers, a standalone GC, startup hooks) or write a trace or dump to a user-chosen path (event pipe,
/// minidump) before any managed code of the process runs. The elevated
/// helper builds its environment from places the unelevated user can write, and the runtime reads these
/// before the helper can look at anything, so they are removed from this process before the helper is
/// started and the helper refuses to work when it finds any of them set anyway.
/// </summary>
internal static class NativeLoadEnvironment
{
    internal static readonly string[] Names =
    [
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
        "DOTNET_GCName", "DOTNET_GCPath", "COMPlus_GCName", "COMPlus_GCPath",
        "DOTNET_STARTUP_HOOKS",
        // Not a load, but the runtime writes a trace or a crash dump to the path the variable names. In the
        // elevated helper that is a file written as administrator wherever the user points it.
        "DOTNET_EnableEventPipe", "DOTNET_EventPipeConfig", "DOTNET_EventPipeOutputPath",
        "DOTNET_EventPipeOutputStreaming", "COMPlus_EnableEventPipe", "COMPlus_EventPipeConfig",
        "COMPlus_EventPipeOutputPath", "COMPlus_EventPipeOutputStreaming",
        "DOTNET_DbgEnableMiniDump", "DOTNET_DbgMiniDumpName", "DOTNET_CreateDumpDiagnostics",
        "DOTNET_CreateDumpVerboseDiagnostics", "DOTNET_CreateDumpLogToFile",
        "COMPlus_DbgEnableMiniDump", "COMPlus_DbgMiniDumpName", "COMPlus_CreateDumpDiagnostics",
        "COMPlus_CreateDumpVerboseDiagnostics", "COMPlus_CreateDumpLogToFile",
    ];

    /// <summary>The variables from <see cref="Names"/> that currently hold a non-empty value.</summary>
    internal static IReadOnlyList<string> FindSet(Func<string, string?>? read = null)
    {
        read ??= name => Environment.GetEnvironmentVariable(name);
        return Names.Where(name => !string.IsNullOrEmpty(read(name))).ToList();
    }

    /// <summary>Removes every variable from <see cref="Names"/> from this process's own environment and
    /// returns the names that were set.</summary>
    internal static IReadOnlyList<string> Scrub(Func<string, string?>? read = null, Action<string>? remove = null)
    {
        remove ??= name => Environment.SetEnvironmentVariable(name, null);
        var found = FindSet(read);
        foreach (var name in Names)
            remove(name);

        return found;
    }
}
