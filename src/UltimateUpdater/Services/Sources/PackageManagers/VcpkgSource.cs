using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// C/C++ libraries via vcpkg, classic (non-manifest) mode only. Manifest mode (a
/// project's own `vcpkg.json`) has no global "outdated" concept at all, irrelevant here anyway, since
/// this app always runs `vcpkg` from its own working directory, never a project's, so it can only ever
/// see the classic/global install. No admin: the default `C:\vcpkg` (or wherever `%VCPKG_ROOT%` points)
/// is user-writable.
/// </summary>
public sealed class VcpkgSource(LoggingService logging) : CliUpdateSource(logging)
{
    public override string Id => "vcpkg";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "vcpkg";

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["update"], cancellationToken));

        return PackageManagerParsers.ParseVcpkgUpdate(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    /// <summary>
    /// `upgrade` rebuilds every outdated port in one pass, there is no documented single-port upgrade
    /// verb, so (same shape as rustup) the row's own id is not an argument.
    /// </summary>
    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["upgrade", "--no-dry-run"];

    /// <summary>Unlike `upgrade`, `remove` takes a specific port, there is nothing else it could mean.</summary>
    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["remove", item.Id];
}
