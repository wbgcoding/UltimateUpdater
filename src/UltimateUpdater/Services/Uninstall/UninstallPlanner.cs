using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UltimateUpdater.Services.Processes;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>How an entry is removed.</summary>
public enum UninstallMethod
{
    /// <summary>`winget uninstall --id ... --exact --silent`.</summary>
    Winget,

    /// <summary>`msiexec /x {code} /qn /norestart`.</summary>
    Msi,

    /// <summary>The entry's own QuietUninstallString.</summary>
    QuietString,

    /// <summary>The entry's UninstallString plus the silent switch of the installer type recognised from its files.</summary>
    SilentSwitch,

    /// <summary>The entry's plain UninstallString; the vendor's window shows and a person clicks through it.</summary>
    Interactive,

    /// <summary>`Remove-AppxPackage` for a Store app.</summary>
    StoreApp,

    /// <summary>`pnputil /delete-driver` for a third-party driver package no present device uses.</summary>
    Driver,

    /// <summary>`dism /online /remove-package` for an installed Windows update that Windows lets people remove.</summary>
    WindowsUpdate,

    /// <summary>No safe way: the row shows that it cannot be removed automatically.</summary>
    Refused,
}

/// <summary>
/// What running one removal means: the program, the arguments, whether it runs elevated and whether a window
/// shows. Pure data; the executor re-checks the program just before it launches it.
/// </summary>
public sealed record UninstallPlan
{
    public UninstallMethod Method { get; init; }

    /// <summary>The program: an absolute path, or the bare tool name for winget and PowerShell (resolved by the
    /// runner under its own trust rules).</summary>
    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>True when the removal needs the elevated worker (a machine-scope entry).</summary>
    public bool Elevated { get; init; }

    /// <summary>True when the vendor's own window shows and the run waits for a person.</summary>
    public bool Interactive { get; init; }

    /// <summary>Extra environment for the child (a Store app's package name, kept out of the command text).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public UninstallRefusal Refusal { get; init; }

    public bool IsRunnable => Method != UninstallMethod.Refused && Refusal == UninstallRefusal.None;
}

/// <summary>The file facts the planner needs, behind a seam so tests need no installed programs.</summary>
public interface IUninstallFileProbe
{
    bool FileExists(string path);

    /// <summary>The first bytes of a file (at most <paramref name="count"/>), or null when it cannot be read.</summary>
    byte[]? ReadHead(string path, int count);

    /// <summary>True when the file sits where only administrators can change it (the elevated launch rule).</summary>
    bool IsTrustedForElevation(string path);
}

/// <summary>The probe over the real file system.</summary>
public sealed class FileSystemUninstallProbe : IUninstallFileProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public byte[]? ReadHead(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[count];
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                    break;
                read += n;
            }

            return buffer.AsSpan(0, read).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool IsTrustedForElevation(string path) => ProcessRunner.IsTrustedUninstallerLocation(path);
}

/// <summary>
/// Picks how an entry is removed, first match wins: winget id, MSI product code, the quiet command, the normal
/// command with the silent switch of a recognised installer, else the normal command interactive. Store apps go
/// through PowerShell. Nothing is guessed: an installer that is not recognised is not given a switch.
/// </summary>
public static partial class UninstallPlanner
{
    internal const string AppxPackageVariable = "UU_APPX_PACKAGE";

    private const int NsisProbeBytes = 1024 * 1024;

    public static UninstallPlan Plan(InstalledProgramEntry entry, IUninstallFileProbe probe)
    {
        if (entry.Kind == UninstallEntryKind.StoreApp)
            return PlanStoreApp(entry);

        if (entry.Kind == UninstallEntryKind.Driver)
            return PlanDriver(entry);

        if (entry.Kind == UninstallEntryKind.WindowsUpdate)
            return PlanUpdate(entry);

        if (entry.Kind != UninstallEntryKind.Program)
            return Refuse(UninstallRefusal.NoCommand);

        if (UninstallRiskClassifier.IsOwnEntry(entry.Key))
            return Refuse(UninstallRefusal.OwnEntry);

        var elevated = entry.Scope == UninstallScope.Machine;

        if (!string.IsNullOrWhiteSpace(entry.WingetId))
        {
            return ProcessRunner.IsSafePositionalArgument(entry.WingetId, allowCmdMetacharacters: true)
                ? new UninstallPlan
                {
                    Method = UninstallMethod.Winget,
                    Executable = "winget",
                    Arguments = WingetSource.UninstallArguments(entry.WingetId, elevated ? "machine" : "user"),
                    Elevated = elevated,
                }
                : Refuse(UninstallRefusal.InvalidId);
        }

        if (entry.MsiProductCode is { } code)
            return PlanMsi(code, elevated);

        if (!string.IsNullOrWhiteSpace(entry.QuietUninstallString))
        {
            return PlanCommand(UninstallMethod.QuietString, entry.QuietUninstallString, extraArguments: [], elevated,
                interactive: false, probe);
        }

        if (string.IsNullOrWhiteSpace(entry.UninstallString))
            return Refuse(UninstallRefusal.NoCommand);

        var parsed = UninstallCommandLine.Parse(entry.UninstallString, probe.FileExists, out var refusal);
        if (parsed is null)
            return Refuse(refusal);

        var silent = parsed.Arguments.Count == 0 ? SilentSwitchFor(parsed.Executable, probe) : null;
        return silent is null
            ? PlanCommand(UninstallMethod.Interactive, entry.UninstallString, extraArguments: [], elevated, interactive: true, probe)
            : PlanCommand(UninstallMethod.SilentSwitch, entry.UninstallString, silent, elevated, interactive: false, probe);
    }

    private static UninstallPlan PlanMsi(string productCode, bool elevated) => new()
    {
        Method = UninstallMethod.Msi,
        Executable = Path.Combine(System.Environment.SystemDirectory, "msiexec.exe"),
        Arguments = ["/x", productCode, "/qn", "/norestart"],
        Elevated = elevated,
    };

    private static UninstallPlan PlanCommand(
        UninstallMethod method, string command, IReadOnlyList<string> extraArguments, bool elevated, bool interactive,
        IUninstallFileProbe probe)
    {
        var parsed = UninstallCommandLine.Parse(command, probe.FileExists, out var refusal);
        if (parsed is null)
            return Refuse(refusal);

        if (!probe.FileExists(parsed.Executable))
            return Refuse(UninstallRefusal.MissingFile);

        // A program that runs as administrator must live where only administrators can change it; a
        // user-scope entry runs as the user and crosses no boundary.
        if (elevated && (IsDirectlyInWindowsFolder(parsed.Executable) || !probe.IsTrustedForElevation(parsed.Executable)))
            return Refuse(UninstallRefusal.UntrustedLocation);

        return new UninstallPlan
        {
            Method = method,
            Executable = parsed.Executable,
            Arguments = [.. parsed.Arguments, .. extraArguments],
            Elevated = elevated,
            Interactive = interactive,
        };
    }

    /// <summary>A system tool (net, icacls, takeown and the like) is no uninstaller: the elevated run of a command
    /// from the registry never starts a program that sits directly in the Windows folder, System32 or SysWOW64.
    /// A list of tool names alone would always be one name short.</summary>
    internal static bool IsDirectlyInWindowsFolder(string executable)
    {
        var folder = Path.GetDirectoryName(executable);
        if (string.IsNullOrEmpty(folder))
            return false;

        folder = Path.TrimEndingDirectorySeparator(folder);
        var windows = Path.TrimEndingDirectorySeparator(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows));
        return new[] { windows, Path.Combine(windows, "System32"), Path.Combine(windows, "SysWOW64") }
            .Any(system => string.Equals(folder, system, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A third-party driver package: `pnputil /delete-driver &lt;oemNN.inf&gt;`, no `/uninstall` and no `/force`, so
    /// pnputil itself refuses a package a device still uses. A package a present device uses is refused here already
    /// (listed greyed). The program is pnputil by its absolute System32 path, never resolved from PATH.
    /// </summary>
    private static UninstallPlan PlanDriver(InstalledProgramEntry entry)
    {
        if (DriverUninstallDiscovery.PackageNameOf(entry.Key) is not { } packageName)
            return Refuse(UninstallRefusal.InvalidId);

        if (!string.IsNullOrWhiteSpace(entry.InUseBy))
            return Refuse(UninstallRefusal.DriverInUse);

        return new UninstallPlan
        {
            Method = UninstallMethod.Driver,
            Executable = Path.Combine(System.Environment.SystemDirectory, "pnputil.exe"),
            Arguments = ["/delete-driver", packageName],
            Elevated = true,
        };
    }

    /// <summary>
    /// A Windows update: `dism /online /remove-package /packagename:&lt;identity&gt; /quiet /norestart`, DISM by its
    /// absolute System32 path. Only a package Windows lets be removed is planned; the identity must be a known update
    /// wrapper so nothing else can be put into the argument.
    /// </summary>
    private static UninstallPlan PlanUpdate(InstalledProgramEntry entry)
    {
        if (WindowsUpdateDiscovery.IdentityOf(entry.Key) is not { } identity)
            return Refuse(UninstallRefusal.InvalidId);

        if (!entry.WindowsAllowsRemoval)
            return Refuse(UninstallRefusal.UpdateNotRemovable);

        return new UninstallPlan
        {
            Method = UninstallMethod.WindowsUpdate,
            Executable = Path.Combine(System.Environment.SystemDirectory, "dism.exe"),
            Arguments = ["/online", "/remove-package", "/packagename:" + identity, "/quiet", "/norestart"],
            Elevated = true,
        };
    }

    private static UninstallPlan PlanStoreApp(InstalledProgramEntry entry)
    {
        if (entry.PackageFullName is not { } fullName || !PackageFullNameRegex().IsMatch(fullName))
            return Refuse(UninstallRefusal.InvalidId);

        return new UninstallPlan
        {
            Method = UninstallMethod.StoreApp,
            Executable = "powershell",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command",
                $"Remove-AppxPackage -Package $env:{AppxPackageVariable} -ErrorAction Stop"],
            Environment = new Dictionary<string, string> { [AppxPackageVariable] = fullName },
            Elevated = false,
        };
    }

    /// <summary>The silent switches of the two installer types recognised from the program's own files.</summary>
    private static IReadOnlyList<string>? SilentSwitchFor(string executable, IUninstallFileProbe probe)
    {
        var fileName = Path.GetFileName(executable);
        if (InnoUninstallerNameRegex().IsMatch(fileName) && probe.FileExists(Path.ChangeExtension(executable, ".dat")))
            return ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"];

        var head = probe.ReadHead(executable, NsisProbeBytes);
        if (head is not null && ContainsAscii(head, "Nullsoft"))
            return ["/S"];

        return null;
    }

    private static bool ContainsAscii(byte[] data, string marker) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0;

    private static UninstallPlan Refuse(UninstallRefusal refusal) => new()
    {
        Method = UninstallMethod.Refused,
        Refusal = refusal == UninstallRefusal.None ? UninstallRefusal.Unparseable : refusal,
    };

    [GeneratedRegex(@"^unins\d{3}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InnoUninstallerNameRegex();

    /// <summary>Name_Version_Architecture_ResourceId_PublisherId; the resource id may be empty.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,99}_\d+(\.\d+){1,3}_[A-Za-z0-9]{1,12}_[A-Za-z0-9\-]{0,40}_[a-z0-9]{13}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PackageFullNameRegex();
}
