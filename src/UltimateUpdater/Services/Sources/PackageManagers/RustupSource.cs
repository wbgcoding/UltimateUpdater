using System.Net.Http;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources.PackageManagers;

/// <summary>
/// Rust toolchains via rustup. `rustup update` refreshes every installed toolchain (and rustup itself)
/// in one call, so the specific row's id is not an argument; updating any Rust row brings all current.
/// No uninstall: rustup only knows "remove a toolchain" as a whole, and this row does not
/// mean one specific toolchain in the way an uninstall click would expect; removing it would take the
/// entire chain the row's channel belongs to, not "this update".
/// </summary>
public sealed class RustupSource(LoggingService logging, HttpClient httpClient) : CliUpdateSource(logging)
{
    public override string Id => "rustup";

    public override UpdateCategory Category => UpdateCategory.DeveloperTools;

    protected override string Executable => "rustup";

    /// <summary>`rustup check` exits 100 whenever ANY toolchain has an update, which is its success case.</summary>
    protected override bool IsAcceptedScanExitCode(int exitCode) => exitCode is 0 or 100;

    /// <summary>
    /// `rustup check` reports no publish date. The row's own name is the toolchain rustup itself uses
    /// (e.g. "stable-x86_64-pc-windows-msvc"); only the three real channels have a public manifest at
    /// this URL; a pinned exact-version toolchain ("1.75.0-x86_64-…") has none and is left alone rather
    /// than guessed at. Size is deliberately NOT read here: the per-target archive URL needs the
    /// installed host triple to pick the right file, and getting that wrong is worse than no size at all
    /// See <see cref="PackageMetadataParsers.ParseRustupChannelDate"/>.
    /// </summary>
    public override async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        var channel = item.Id.Split('-')[0];
        if (channel is not ("stable" or "beta" or "nightly"))
            return null;

        var url = $"https://static.rust-lang.org/dist/channel-rust-{channel}.toml";
        var toml = await RegistryEnrichment.FetchRawAsync(httpClient, url, Logging, Id, item.Id, cancellationToken);
        var date = toml is null ? null : PackageMetadataParsers.ParseRustupChannelDate(toml);

        // Not per-version (rustup ships one combined changelog for every Rust release) but real and
        // free of any lookup, so it is attached even when the date fetch above failed.
        const string changelogUrl = "https://github.com/rust-lang/rust/blob/master/RELEASES.md";
        return new UpdateEnrichment(date, null, ChangelogUrl: changelogUrl);
    }

    protected override async Task<IReadOnlyList<UpdateItem>> ScanCoreAsync(CancellationToken cancellationToken)
    {
        var result = EnsureScanCompleted(await RunAsync(["check"], cancellationToken));

        return PackageManagerParsers.ParseRustupCheck(result.StandardOutput)
            .Select(p => CreateItem(p.Id, p.Name, p.Installed, p.Available))
            .ToList();
    }

    protected override IReadOnlyList<string> InstallArguments(UpdateItem item) => ["update"];
}
