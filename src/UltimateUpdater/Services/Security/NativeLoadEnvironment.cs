using System.IO;

namespace UltimateUpdater.Services.Security;

/// <summary>
/// Environment variables that make the .NET runtime load a native library or assembly named by the user
/// (profilers, a standalone GC, startup hooks) or write a trace or dump to a user-chosen path (event pipe,
/// minidump) before any managed code of the process runs. The elevated
/// helper builds its environment from places the unelevated user can write, and the runtime reads these
/// before the helper can look at anything, so they are removed from this process before the helper is
/// started and the helper refuses to work when it finds any of them set anyway. A runas child does not
/// inherit this process's block at all: Windows builds a fresh one from the machine key and the user's own
/// <c>HKCU\Environment</c>, so those user keys are checked as well before the helper is started.
/// </summary>
internal static class NativeLoadEnvironment
{
    internal static readonly string[] Names =
    [
        "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH",
        "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64",
        "CORECLR_PROFILER_PATH_ARM32", "CORECLR_PROFILER_PATH_ARM64",
        // Notification profilers load by path even when CORECLR_ENABLE_PROFILING is unset.
        "CORECLR_ENABLE_NOTIFICATION_PROFILERS", "CORECLR_NOTIFICATION_PROFILERS",
        "CORECLR_NOTIFICATION_PROFILERS_32", "CORECLR_NOTIFICATION_PROFILERS_64",
        "CORECLR_NOTIFICATION_PROFILERS_ARM32", "CORECLR_NOTIFICATION_PROFILERS_ARM64",
        "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
        // A diagnostic port makes the runtime connect at startup to a pipe the user serves, and that
        // protocol can attach a profiler or write a trace or dump before any code of the helper runs.
        "DOTNET_DiagnosticPorts", "COMPlus_DiagnosticPorts",
        "DOTNET_DefaultDiagnosticPortSuspend", "COMPlus_DefaultDiagnosticPortSuspend",
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
        "DOTNET_StressLog", "DOTNET_StressLogFilename", "COMPlus_StressLog", "COMPlus_StressLogFilename",
        "DOTNET_JitStdOutFile", "COMPlus_JitStdOutFile",
    ];

    /// <summary>Keys below HKCU whose values Windows copies into the environment of a new process. Both are
    /// writable by the unelevated user.</summary>
    internal static readonly string[] UserEnvironmentKeys = ["Environment", "Volatile Environment"];

    /// <summary>Whole families that are debugging hooks of the runtime and never legitimate for the helper.</summary>
    private static readonly string[] Prefixes = ["CORECLR_", "COR_"];

    /// <summary>True for a variable name that must not reach the elevated helper.</summary>
    internal static bool IsBlocked(string name) =>
        Names.Contains(name, StringComparer.OrdinalIgnoreCase)
        || Prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>The blocked names among <paramref name="userValues"/> (name and raw value of the user's
    /// registry environment) that hold a non-empty value. The pure core of the check that runs before a
    /// runas start.</summary>
    internal static IReadOnlyList<string> FindSetIn(IEnumerable<KeyValuePair<string, string?>> userValues) =>
        userValues
            .Where(pair => !string.IsNullOrEmpty(pair.Value) && IsBlocked(pair.Key))
            .Select(pair => pair.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Reads the user's registry environment (<see cref="UserEnvironmentKeys"/>) without expanding
    /// anything; an unreadable key counts as empty because the helper checks its own block again.</summary>
    internal static IReadOnlyList<string> FindSetInUserRegistry()
    {
        var values = new List<KeyValuePair<string, string?>>();
        foreach (var keyName in UserEnvironmentKeys)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyName);
                if (key is null)
                    continue;

                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
                    values.Add(new(name, value switch
                    {
                        string text => text,
                        string[] lines => string.Join(' ', lines),
                        null => null,
                        _ => "<binary>",
                    }));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Unreadable: nothing to report; the worker's own FindSet is the second line.
            }
        }

        return FindSetIn(values);
    }

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
