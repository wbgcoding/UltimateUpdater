using System.IO;
using System.Globalization;
using UltimateUpdater.Services.Cleanup;
using UltimateUpdater.Services.Platform;
using UltimateUpdater.Services.Sources.Windows;

namespace UltimateUpdater.Services.Uninstall;

/// <summary>
/// The programs Windows lists as installed, read from the Uninstall keys (HKLM 64 bit and 32 bit, HKCU) plus
/// the Store apps and the winget ids that belong to them. Reading only: nothing here removes or runs anything.
/// Every value is untrusted text; a key that cannot be read is skipped, never fatal.
/// </summary>
public sealed class InstalledProgramInventory
{
    private readonly IRegistryView _registry;
    private readonly IAppxPackageSource _appx;
    private readonly IWingetListSource _winget;

    public InstalledProgramInventory(IRegistryView registry, IAppxPackageSource appx, IWingetListSource winget)
    {
        _registry = registry;
        _appx = appx;
        _winget = winget;
    }

    /// <summary>The registry programs, the Store apps, and winget ids on the programs that match one exactly.
    /// A reader that fails leaves its part out; the others still list.</summary>
    public async Task<IReadOnlyList<InstalledProgramEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        var programs = ReadRegistryPrograms();

        var wingetText = await SafeAsync(() => _winget.ListAsync(cancellationToken), cancellationToken);
        if (!string.IsNullOrWhiteSpace(wingetText))
            programs = WingetListParser.AttachIds(programs, WingetListParser.Parse(wingetText));

        var apps = await SafeAsync(() => _appx.ReadAsync(cancellationToken), cancellationToken);
        return apps is null ? programs : [.. programs, .. apps];
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T?>> read, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                                       or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The Uninstall-key programs of all four registry roots, in root order.</summary>
    public IReadOnlyList<InstalledProgramEntry> ReadRegistryPrograms()
    {
        var result = new List<InstalledProgramEntry>();
        foreach (var root in WindowsRegistryRoots.Uninstall)
        {
            var scope = root.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
                ? UninstallScope.User
                : UninstallScope.Machine;

            foreach (var name in _registry.SubKeyNames(root))
            {
                var entry = ReadEntry($"{root}\\{name}", scope);
                if (entry is not null)
                    result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// One key as an entry, or null when it is not a program a person installed: a system component, an update
    /// (ParentKeyName or ReleaseType), a key with no display name, or one with no uninstall command at all.
    /// </summary>
    internal InstalledProgramEntry? ReadEntry(string keyPath, UninstallScope scope) => ReadEntry(_registry, keyPath, scope);

    /// <summary>The same read over any registry view, for a caller that holds no readers for Store apps or winget
    /// (the elevated worker re-reads one machine entry this way).</summary>
    internal static InstalledProgramEntry? ReadEntry(IRegistryView registry, string keyPath, UninstallScope scope)
    {
        if (registry.DwordValue(keyPath, "SystemComponent") == 1)
            return null;

        if (!string.IsNullOrWhiteSpace(registry.Value(keyPath, "ParentKeyName"))
            || !string.IsNullOrWhiteSpace(registry.Value(keyPath, "ReleaseType")))
            return null;

        var name = Text(registry.Value(keyPath, "DisplayName"));
        if (name.Length == 0)
            return null;

        var quiet = Text(registry.Value(keyPath, "QuietUninstallString"));
        var normal = Text(registry.Value(keyPath, "UninstallString"));
        if (quiet.Length == 0 && normal.Length == 0)
            return null;

        // The MSI code comes from the normal string first (the one Windows itself runs), then the quiet one.
        var msiCode = RegistryCleanupSafety.ExtractMsiProductCode(normal)
                      ?? RegistryCleanupSafety.ExtractMsiProductCode(quiet);

        long? size = registry.DwordValue(keyPath, "EstimatedSize") is { } kilobytes and > 0
            ? (long)(uint)kilobytes * 1024
            : null;

        return new InstalledProgramEntry
        {
            Key = keyPath,
            Kind = UninstallEntryKind.Program,
            Name = name,
            Publisher = Text(registry.Value(keyPath, "Publisher")),
            Version = Text(registry.Value(keyPath, "DisplayVersion")),
            InstallDate = ParseInstallDate(registry.Value(keyPath, "InstallDate")),
            SizeBytes = size,
            InstallLocation = Text(registry.Value(keyPath, "InstallLocation")),
            Scope = scope,
            QuietUninstallString = quiet,
            UninstallString = normal,
            MsiProductCode = msiCode,
            DisplayIcon = Text(registry.Value(keyPath, "DisplayIcon")),
        };
    }

    private static string Text(string? value) => value?.Trim() ?? string.Empty;

    /// <summary>InstallDate is written as yyyyMMdd; anything else is no date.</summary>
    internal static DateOnly? ParseInstallDate(string? value) =>
        value is { Length: 8 }
        && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
}
