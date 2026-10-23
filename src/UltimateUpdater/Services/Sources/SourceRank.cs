using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// Which source should carry out an update when more than one offers the same one, and what counts as
/// "the same one" in the first place (a tool installed twice, once through pip
/// and once through uv's tools; the requirement is one row with both sources named under it and the
/// better one doing the work).
/// The table is EXPLICIT and hand-ordered, like every other decision table in this project: an unknown
/// source gets a middling default rather than being guessed at by name. Lower is better.
/// The ordering rests on three things, in this order:
/// 1. Who owns the package. A tool installed through `uv tool` is uv's; asking pip to update it would at
/// best do nothing.
/// 2. How verifiable the update is: winget checks a manifest hash, the OS's own updater is the OS.
/// 3. How silent and scriptable it is. Anything needing a window is last by definition.
/// </summary>
public static class SourceRank
{
    private const int Unknown = 500;

    /// <summary>Never wins: a Tier C row cannot install anything, it only opens something.</summary>
    private const int ManualOnly = 9000;

    private static readonly Dictionary<string, int> Ranks = new(StringComparer.OrdinalIgnoreCase)
    {
        // The system updating itself. Nothing else may take these.
        ["windowsupdate"] = 10,

        // General-purpose Windows package managers, best-verified first: winget resolves through a signed
        // manifest with a payload hash, chocolatey and scoop through their own community feeds.
        ["winget"] = 100,
        ["chocolatey"] = 110,
        ["scoop"] = 120,

        // Language and platform ecosystems. These come after the Windows managers because a package a
        // Windows manager knows about is one it installed, and it should be the one to replace it; but
        // ahead of anything self-updating, because they manage OTHER packages rather than themselves.
        ["uv-tool"] = 200,      // isolated, pinned tool environments; uv owns what it installed
        ["pipx"] = 210,         // same idea, older
        ["pip"] = 220,          // the shared site-packages, which is the least isolated of the three
        ["npm"] = 230,
        ["dotnet-tool"] = 240,
        ["gem"] = 260,
        ["composer"] = 270,
        ["psgallery"] = 280,
        ["gh-extension"] = 290,
        ["vscode-extension"] = 300,
        ["android-sdk"] = 310,
        ["gcloud"] = 320,
        ["ollama"] = 330,
        ["wsl"] = 340,

        // Toolchain managers that refresh everything they own in one go.
        ["rustup"] = 400,

        // Tools that only ever update THEMSELVES. They already stand aside when a package manager owns
        // them (PackageManagerOwnership), so reaching here at all is unusual; and if it happens, the
        // package manager is still the better answer.
        ["deno"] = 600,
        ["bun"] = 610,
        ["uv"] = 620,

        // Vendor installers: one UAC prompt and a vendor's own setup program.
        ["nvidia-driver"] = 700,
    };

    /// <summary>Where this source sits. Lower wins.</summary>
    public static int Of(string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? Unknown : Ranks.GetValueOrDefault(sourceId, Unknown);

    /// <summary>Where this row's source sits, with anything that can only be opened pushed to the back.</summary>
    public static int Of(UpdateItem item) =>
        item.Tier == SourceTier.C ? ManualOnly : Of(item.SourceId);

    /// <summary>
    /// What makes two rows the same update. Deliberately strict: the same package name AND the same
    /// installed version AND the same version on offer. Two sources agreeing on all three are offering one
    /// and the same step, which is the only case where collapsing them into one row cannot hide anything.
    /// Anything weaker would merge rows that are genuinely different; chocolatey and scoop both ship `git`,
    /// and two separate installations at two different versions are two separate pieces of work. Returns
    /// null when the row cannot be compared at all, and a null key never merges with anything.
    /// </summary>
    public static string? DuplicateKey(UpdateItem item)
    {
        if (item.Tier == SourceTier.C)
            return null;    // nothing to install, so nothing to choose between

        if (string.IsNullOrWhiteSpace(item.InstalledVersion) || string.IsNullOrWhiteSpace(item.AvailableVersion))
            return null;

        var id = Normalise(item.Id);
        if (id.Length == 0)
            return null;

        return $"{id}\0{item.InstalledVersion.Trim()}\0{item.AvailableVersion.Trim()}";
    }

    /// <summary>
    /// Package names differ only in case and in the '.'/'_'/'-' a registry happens to prefer; PyPI itself
    /// treats those as one name. Nothing cleverer than that: a fuzzy match here would merge two different
    /// packages, which is worse than showing one twice.
    /// </summary>
    private static string Normalise(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? string.Empty
            : id.Trim().ToLowerInvariant().Replace('_', '-').Replace('.', '-');
}
