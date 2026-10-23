using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Tier C: the app can DETECT that a vendor client is installed, but the vendor exposes no way to
/// read per-title update state. These rows therefore never claim a version and
/// never install; they only offer "open in <vendor>", targeting the vendor's update/downloads view
/// where such a URI exists, otherwise the client executable (which self-patches on launch).
/// No uninstall for the same reason there is no install: this row is a launcher handoff, not a real
/// package this app placed. Removal would need the Registry-`UninstallString` fallback that is out of
/// scope for this app on principle.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class LauncherSourceBase : IUpdateSource
{
    protected LauncherSourceBase(LoggingService logging)
    {
        Logging = logging;
    }

    protected LoggingService Logging { get; }

    public abstract string Id { get; }

    public abstract UpdateCategory Category { get; }

    public SourceTier Tier => SourceTier.C;

    /// <summary>Human-facing row title.</summary>
    protected abstract string DisplayName { get; }

    /// <summary>URI or executable the row's button opens; null when the client is not installed.</summary>
    protected abstract string? ResolveOpenTarget();

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ResolveOpenTarget() is not null);

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = await Task.Run(ResolveOpenTarget, cancellationToken);
        if (target is null)
            yield break;

        // No AVAILABLE version, ever: the vendor exposes no per-title update state, so there is nothing
        // to compare the installed client against (class remarks). But the client's OWN installed
        // version is a real, readable fact when the target is its actual exe: the file's own version
        // resource and last-write time, never fabricated, without it, launcher rows show no version/date at all.
        var (installedVersion, lastWritten) = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? await Task.Run(() => ReadExeVersionInfo(target), cancellationToken)
            : (null, null);

        yield return new UpdateItem
        {
            Id = Id,
            SourceId = Id,
            Category = Category,
            Name = DisplayName,
            // Left in AvailableVersion, not InstalledVersion: the row template only draws the "old ->
            // new" arrow when InstalledVersion is set, and there is no "new" here to point at, just
            // the one real version fact this row has. Putting it in InstalledVersion too would render a
            // misleading "X -> X" as if a comparison had actually been made.
            InstalledVersion = null,
            AvailableVersion = installedVersion,
            // a launcher client is not software this app can make a security claim about, and the row
            // carries no version to judge one by (SecurityRelevance)
            IsCritical = false,
            Tier = Tier,
            OpenTarget = target,
            PublishDate = lastWritten,
        };
    }

    /// <summary>Fails closed: a version resource a vendor did not fill in, or a file this process cannot
    /// stat, yields null rather than a guess.</summary>
    private static (string? Version, DateTimeOffset? LastWritten) ReadExeVersionInfo(string exePath)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var version = info.FileVersion is { Length: > 0 } fileVersion ? fileVersion : info.ProductVersion;
            var written = File.GetLastWriteTimeUtc(exePath);

            return (string.IsNullOrWhiteSpace(version) ? null : version, new DateTimeOffset(written, TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return (null, null);
        }
    }

    public Task InstallAsync(UpdateItem item, IProgress<Models.InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} cannot install; it can only be opened.");

    /// <summary>
    /// Starting a launcher client is the same target its "Open" button uses, already resolved at scan
    /// time, so this is free. It gives the row a right-click "Start" as well, which is what the user
    /// reaches for on a game launcher.
    /// </summary>
    public string? LaunchTarget(UpdateItem item) => item.OpenTarget;

    protected static string? FirstExistingPath(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);

    protected static string? ReadRegistryString(RegistryKey root, string subKey, string valueName)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
