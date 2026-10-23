using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// mise, the polyglot dev-tool version manager, verified only against documentation, no mise install on
/// this dev box to test against. `mise outdated --json`'s shape, confirmed against mise's
/// own official docs (mise.jdx.dev/cli/outdated.html), is an object keyed by tool name with
/// `current`/`latest` string fields, identical to <see cref="PackageManagerParsers.ParseNpmJson"/>'s
/// existing shape, so that parser is reused rather than duplicated (this is NOT an npm-registry package,
/// only the JSON envelope happens to match).
/// `mise upgrade` (bare, no flags) upgrades every outdated tool non-interactively already, `-i`/
/// `--interactive` is what OPTS IN to a confirmation menu, confirmed against mise's own upgrade-command
/// docs. `-y`/`--yes` is NOT a real flag on this command at all (it only exists on the separate
/// `mise self-update`, which updates mise itself and is out of scope here), always confirm against the
/// real flag list rather than assuming one command's flags apply to another, the same class of mistake
/// `cargo install-update` already taught this project.
/// </summary>
public sealed class MiseSource(LoggingService logging) : CliUpdateSource(logging)
{
    public override string Id => "mise";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "mise";

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["outdated", "--json"], cancellationToken));

        return PackageManagerParsers.ParseNpmJson(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["upgrade", item.Id];

    protected override IReadOnlyList<string>? UninstallArguments(UpdateItem item) => ["uninstall", item.Id];
}
