using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Sources.Windows;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Reads <see cref="LeftoverEvidence"/> from the real machine. Reading only. A source that cannot be read at
/// all (the task list, for one) makes the whole read fail, because a missing source could hide the very
/// program that explains a folder: the caller then offers nothing.
/// </summary>
internal static partial class LeftoverEvidenceReader
{
    private const int MaxFolderListings = 4000;
    private const int MaxExecutablesPerFolder = 60;

    private static readonly string[] RunKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
    ];

    private const string StorePackages =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>The evidence of this machine, or null when a source could not be read in full.</summary>
    internal static LeftoverEvidence? Read(LeftoverRoots roots, CancellationToken cancellationToken)
    {
        var evidence = new LeftoverEvidence();
        try
        {
            ReadInstalledPrograms(evidence);
            cancellationToken.ThrowIfCancellationRequested();
            ReadStorePackages(evidence);
            ReadServices(evidence);
            ReadStartup(evidence);
            ReadAppPaths(evidence);
            ReadProcesses(evidence);
            ReadEnvironmentPath(evidence);
            cancellationToken.ThrowIfCancellationRequested();
            ReadShortcuts(evidence, roots, cancellationToken);
            if (!ReadScheduledTasks(evidence, cancellationToken))
                return null;

            ReadExecutableNames(evidence, cancellationToken);
            return evidence;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static void ReadInstalledPrograms(LeftoverEvidence evidence)
    {
        var registry = WindowsRegistryView.Instance;
        evidence.AddClaims(LeftoverClaims.FromRegistry(registry));

        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            foreach (var id in registry.SubKeyNames(root))
            {
                var key = $@"{root}\{id}";
                evidence.AddName(id);
                evidence.AddName(registry.Value(key, "DisplayName"));
                evidence.AddName(registry.Value(key, "Publisher"));
                evidence.AddPath(registry.Value(key, "DisplayIcon"));
                evidence.AddPath(registry.Value(key, "UninstallString"));
                evidence.AddPath(registry.Value(key, "InstallLocation"));
                evidence.AddPath(registry.Value(key, "InstallSource"));
            }
        }
    }

    private static void ReadStorePackages(LeftoverEvidence evidence)
    {
        using var packages = Registry.CurrentUser.OpenSubKey(StorePackages);
        if (packages is null)
            return;

        foreach (var fullName in packages.GetSubKeyNames())
        {
            // "Contoso.Notes_1.2.3.0_x64__8wekyb3d8bbwe": the name part, as a whole and by its dotted parts.
            var name = fullName.Split('_')[0];
            evidence.AddName(name);
            using var package = packages.OpenSubKey(fullName);
            evidence.AddName(package?.GetValue("DisplayName") as string);
            evidence.AddPath(package?.GetValue("PackageRootFolder") as string);
        }
    }

    private static void ReadServices(LeftoverEvidence evidence)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
            return;

        foreach (var service in services.GetSubKeyNames())
        {
            evidence.AddName(service);
            using var key = services.OpenSubKey(service);
            evidence.AddName(key?.GetValue("DisplayName") as string);
            evidence.AddPath(key?.GetValue("ImagePath") as string);
        }
    }

    private static void ReadStartup(LeftoverEvidence evidence)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var path in RunKeys)
            {
                using var key = hive.OpenSubKey(path);
                if (key is null)
                    continue;

                foreach (var valueName in key.GetValueNames())
                {
                    evidence.AddName(valueName);
                    evidence.AddPath(key.GetValue(valueName) as string);
                }
            }
        }
    }

    private static void ReadAppPaths(LeftoverEvidence evidence)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var appPaths = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
            if (appPaths is null)
                continue;

            foreach (var exe in appPaths.GetSubKeyNames())
            {
                evidence.AddName(Path.GetFileNameWithoutExtension(exe));
                using var key = appPaths.OpenSubKey(exe);
                evidence.AddPath(key?.GetValue(null) as string);
                evidence.AddPath(key?.GetValue("Path") as string);
            }
        }
    }

    private static void ReadProcesses(LeftoverEvidence evidence)
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                evidence.AddName(process.ProcessName);
                try
                {
                    evidence.AddPath(process.MainModule?.FileName);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A process this account may not inspect still counts by its name.
                }
            }
        }
    }

    private static void ReadEnvironmentPath(LeftoverEvidence evidence)
    {
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH", target) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
                evidence.AddPath(Environment.ExpandEnvironmentVariables(entry));
        }
    }

    private static void ReadShortcuts(LeftoverEvidence evidence, LeftoverRoots roots, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        foreach (var root in roots.UserShortcutFolders.Concat(roots.MachineShortcutFolders))
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in EnumerateShortcuts(root, cancellationToken))
                files.Add(file);
        }

        foreach (var file in files)
        {
            evidence.AddName(Path.GetFileNameWithoutExtension(file));
            if (Path.GetDirectoryName(file) is { } folder)
                evidence.AddName(Path.GetFileName(folder));
        }

        foreach (var (_, target) in LeftoverScanner.ReadShortcutTargets(files))
            evidence.AddPath(target);
    }

    private static IEnumerable<string> EnumerateShortcuts(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((root, 0));
        var count = 0;
        while (pending.Count > 0 && count < 20000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (folder, depth) = pending.Pop();

            foreach (var file in SafeEnumerate.Files(folder))
            {
                if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    yield return file;
                }
            }

            if (depth < 5)
            {
                foreach (var sub in SafeEnumerate.Directories(folder))
                    pending.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>Task names and what each task runs, from <c>schtasks /query /xml</c> (every task this account
    /// may see). False when the list could not be read.</summary>
    private static bool ReadScheduledTasks(LeftoverEvidence evidence, CancellationToken cancellationToken)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (string.IsNullOrWhiteSpace(system))
            return false;

        ProcessResult result;
        try
        {
            result = ProcessRunner.RunAsync(
                Path.Combine(system, "schtasks.exe"), ["/query", "/xml"], TimeSpan.FromSeconds(60), cancellationToken,
                outputEncoding: System.Text.Encoding.Latin1)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            return false;
        }

        if (result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return false;

        // schtasks answers in the OEM code page; Latin1 kept the raw bytes, so folder names with accents survive.
        var xml = OemTextRecovery.NeedsRecovery(result.StandardOutput)
            ? OemTextRecovery.Recover(result.StandardOutput)
            : result.StandardOutput;
        // A listing that does not look like task XML (another encoding, an error text) is no evidence at all.
        if (!xml.Contains("<Task", StringComparison.Ordinal))
            return false;

        foreach (Match match in TaskTextPattern().Matches(xml))
        {
            var text = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
            if (match.Groups[1].Value is "URI")
            {
                foreach (var part in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                    evidence.AddName(part);
            }
            else
            {
                evidence.AddPath(Environment.ExpandEnvironmentVariables(text));
            }
        }

        return true;
    }

    [GeneratedRegex(@"<(URI|Command|Arguments|WorkingDirectory)>\s*([^<]*?)\s*</\1>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaskTextPattern();

    /// <summary>The program files' own names: the executables at the top of every folder a program or
    /// service lives in ("ts3client_win64.exe" explains an "ts3client" folder).</summary>
    private static void ReadExecutableNames(LeftoverEvidence evidence, CancellationToken cancellationToken)
    {
        var listed = 0;
        foreach (var folder in evidence.Paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++listed > MaxFolderListings || !Directory.Exists(folder))
                continue;

            var taken = 0;
            foreach (var file in SafeEnumerate.Files(folder))
            {
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && taken++ < MaxExecutablesPerFolder)
                    evidence.AddName(Path.GetFileNameWithoutExtension(file));
            }
        }
    }
}
