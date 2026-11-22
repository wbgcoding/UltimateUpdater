using System.IO;
using UltimateUpdater.Services.Uninstall;

namespace UltimateUpdater.Services.Cleanup;

/// <summary>
/// Finds folders of long-removed programs: direct children of %APPDATA%, %LOCALAPPDATA% (and its Programs
/// folder), ProgramData and both Program Files that no program on this machine explains and that nothing has
/// touched for <see cref="MinimumAge"/>. The decision is the opposite of "looks old": a folder is offered only
/// when EVERY source of evidence (see <see cref="LeftoverEvidence"/>) was read and none of it names the folder,
/// its owner or anything inside or around it. A source that cannot be read, a walk that cannot see the whole
/// folder, a name on the protected list: each means "not offered". The caller still shows every folder with
/// its size and last-used date and never preselects one.
/// </summary>
public static class AppDataLeftoverDiscovery
{
    /// <summary>Untouched for this long, the same bar as the install-folder check this replaces.</summary>
    public static readonly TimeSpan MinimumAge = LeftoverInstallDiscovery.MinimumAge;

    private const int MaxFilesScannedPerCandidate = 2000;
    private const int MaxScanDepth = 3;

    /// <summary>
    /// Folder names that are never offered: Windows' own and shared runtime folders, graphics and game
    /// anti-cheat components, the places other programs keep shared state. Compared on the normalized name, as
    /// a whole or as the start of it ("Microsoft" also covers "MicrosoftEdge"). A name added here can only
    /// ever prevent an offer.
    /// </summary>
    private static readonly string[] ProtectedNames =
    [
        "Microsoft", "Windows", "Packages", "Temp", "Programs", "Package Cache", "Comms", "Publishers",
        "ConnectedDevices", "PlaceholderTileLogoFolder", "D3DSCache", "Low", "VirtualStore", "History",
        "Application Data", "Desktop", "Documents", "Templates", "Start Menu", "Common Files", "Default",
        "Public", "SoftwareDistribution", "USOShared", "USOPrivate", "regid.1991-06.com.microsoft",
        "ModifiableWindowsApps", "WindowsApps", "MSBuild", "dotnet", "NuGet", "Reference Assemblies",
        "WindowsPowerShell", "PowerShell", "PackageManagement", "InstallShield Installation Information",
        "Uninstall Information", "Internet Explorer", "Crypto", "SystemApps", "NVIDIA", "NVIDIA Corporation",
        "Intel", "AMD", "ATI", "Realtek", "Java", "Oracle", "Sun", "Python", "pip", "npm", "node", "NuGet",
        "Visual Studio", "VisualStudio", "Windows Kits", "WinGet", "regid", "cache", "logs", "ProgramData",
        "EasyAntiCheat", "Easy Anti-Cheat", "BattlEye", "Riot Vanguard", "Epic Online Services",
        "VulkanRT", "Vulkan SDK", "DIFX", "Adobe", "Apple", "Apple Computer", "Google", "Mozilla",
    ];

    private static readonly string[] ProtectedPrefixes =
    [
        "microsoft", "windows", "msbuild", "nvidia", "easyanticheat", "battleye", "vanguard", "dotnet",
        "visualstudio", "package", "ultimateupdater", "windowsupdater", "mozilla", "vulkan",
    ];

    /// <summary>One top-level folder and what was decided about it: <see cref="Reason"/> is null for an offer.</summary>
    internal sealed record Verdict(string Path, string? Reason, DateTimeOffset NewestWriteTime);

    /// <summary>The folders to offer. Empty when any evidence could not be read.</summary>
    public static IReadOnlyList<LeftoverCandidate> DiscoverCandidates(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var roots = LeftoverRoots.Current();
        var evidence = LeftoverEvidenceReader.Read(roots, cancellationToken);
        if (evidence is null)
            return [];

        return Offers(Judge(roots, evidence, now, cancellationToken), cancellationToken);
    }

    /// <summary>The offers of a judged list, each with its size.</summary>
    internal static IReadOnlyList<LeftoverCandidate> Offers(IEnumerable<Verdict> verdicts, CancellationToken cancellationToken) =>
        [.. verdicts.Where(v => v.Reason is null).Select(v => new LeftoverCandidate(
            v.Path, Path.GetFileName(v.Path),
            DirectorySizeWalker.Measure(v.Path, MaxScanDepth, MaxFilesScannedPerCandidate, cancellationToken),
            v.NewestWriteTime))];

    /// <summary>Every direct child of the scanned roots with its verdict. The seam a test or a read-only
    /// comparison run drives with a hand-built machine.</summary>
    internal static IReadOnlyList<Verdict> Judge(
        LeftoverRoots roots, LeftoverEvidence evidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var verdicts = new List<Verdict>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots.InstallRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
                continue;

            foreach (var folder in SafeEnumerate.Directories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = LeftoverRoots.Full(folder);
                if (full is null || !seen.Add(full))
                    continue;

                verdicts.Add(JudgeFolder(full, roots, evidence, now, cancellationToken));
            }
        }

        return verdicts;
    }

    private static Verdict JudgeFolder(
        string full, LeftoverRoots roots, LeftoverEvidence evidence, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Verdict No(string reason, DateTimeOffset? newest = null) => new(full, reason, newest ?? DateTimeOffset.MaxValue);

        // The scanned roots themselves and the other root's folders ("Programs" below Local, say) are never
        // candidates; a folder that holds one of them is not either.
        if (roots.InstallRoots.Any(root => LeftoverRoots.SamePath(full, root) || LeftoverRoots.IsBelow(root, full)))
            return No("a scanned root");

        var name = Path.GetFileName(full);
        var normalized = LeftoverNames.Normalize(name);
        if (normalized.Length == 0 || name.StartsWith('.') || name.StartsWith('{') || name.StartsWith('$') || name.StartsWith('~'))
            return No("unusual name");

        if (IsProtectedName(normalized))
            return No("protected name");

        if (!LeftoverTargetSafety.IsSafeFolder(full, roots, out var safety))
            return No("unsafe: " + safety);

        if (IsHiddenOrSystem(full))
            return No("hidden or system folder");

        var explanation = evidence.Explain(full, roots);
        if (explanation is not null)
            return No("explained by " + explanation);

        var newest = LeftoverInstallDiscovery.NewestWriteTime(full, cancellationToken);
        if (now - newest < MinimumAge)
            return No("touched within the minimum age", newest);

        switch (UseOf(full, now, cancellationToken))
        {
            case FolderUse.Recent:
                return No("a file in it was created, written or opened recently", newest);
            case FolderUse.Unknown:
                return No("not fully readable", newest);
            case FolderUse.Empty:
                return No("empty", newest);
        }

        return new Verdict(full, null, newest);
    }

    internal static bool IsProtectedName(string normalized) =>
        ProtectedNames.Any(p => LeftoverNames.Normalize(p) == normalized)
        || ProtectedPrefixes.Any(p => normalized.StartsWith(p, StringComparison.Ordinal))
        || LeftoverNames.IsGeneric(normalized);

    private static bool IsHiddenOrSystem(string folder)
    {
        try
        {
            return (File.GetAttributes(folder) & (FileAttributes.System | FileAttributes.Offline | FileAttributes.Device)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// What a full walk of a folder says about its use: Recent when any file in it was created, written or
    /// opened within the minimum age (a program that still reads its data, a copy made lately, a portable
    /// program that is only ever run), Unknown when the walk could not see the whole folder, Empty when it
    /// holds no file at all (nothing to free, nothing that shows a program lived there), otherwise Idle.
    /// Last-access times are only as good as the file system keeps them, and a scanner that reads every file
    /// refreshes them: both can only hide a folder, never offer one.
    /// </summary>
    internal static FolderUse UseOf(string folder, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scanned = 0;
        var incomplete = false;
        var recent = false;

        void Walk(string dir, int depth)
        {
            if (recent || incomplete)
                return;

            if (depth > MaxScanDepth)
            {
                incomplete = true;
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var file in SafeEnumerate.Files(dir, () => incomplete = true))
            {
                if (scanned++ >= MaxFilesScannedPerCandidate)
                {
                    incomplete = true;
                    return;
                }

                try
                {
                    var newest = new[]
                    {
                        File.GetCreationTimeUtc(file), File.GetLastWriteTimeUtc(file), File.GetLastAccessTimeUtc(file),
                    }.Max();
                    if (now - new DateTimeOffset(newest, TimeSpan.Zero) < MinimumAge)
                        recent = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    incomplete = true;
                }
            }

            foreach (var sub in SafeEnumerate.Directories(dir, () => incomplete = true))
                Walk(sub, depth + 1);
        }

        Walk(folder, 0);
        if (recent)
            return FolderUse.Recent;

        if (incomplete)
            return FolderUse.Unknown;

        return scanned == 0 ? FolderUse.Empty : FolderUse.Idle;
    }

    internal enum FolderUse
    {
        Idle,
        Recent,
        Unknown,
        Empty,
    }
}
