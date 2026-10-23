using System.IO;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The manual stand-in for <see cref="AndroidSdkSource"/>: Android Studio is installed but its command-line
/// tools are not, so nothing here can update the SDK on its own.
/// Android Studio ships the SDK Manager inside the IDE and exposes no argument for opening it; there is no
/// documented switch, and inventing one would silently open the wrong window, so this row opens Studio and
/// says where to go. The description also names the one component (`cmdline-tools`) that turns this row into
/// the fully automatic one, which is the useful thing to tell someone here.
/// </summary>
public sealed class AndroidStudioSource(LoggingService logging) : IUpdateSource
{
    private static readonly Lazy<string?> ResolvedStudio = new(FindStudio);

    public string Id => "android-studio";

    public UpdateCategory Category => UpdateCategory.DeveloperTools;

    public SourceTier Tier => SourceTier.C;

    /// <summary>
    /// Only when the automatic path is unavailable. With `sdkmanager` present the SDK source does the real
    /// work and a second row saying "do it by hand" would be noise.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ResolvedStudio.Value is not null && AndroidSdkSource.ToolMissing);

    public Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken) =>
        Task.FromResult<UpdateEnrichment?>(
            new UpdateEnrichment(null, null, ChangelogUrl: "https://developer.android.com/studio/releases"));

    public async IAsyncEnumerable<UpdateItem> ScanAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var studio = await Task.Run(() => ResolvedStudio.Value, cancellationToken);
        if (studio is null || !AndroidSdkSource.ToolMissing)
            yield break;

        logging.Info("android-studio: sdkmanager is missing, offering the SDK Manager by hand.");

        yield return new UpdateItem
        {
            Id = Id,
            SourceId = Id,
            Category = Category,
            Name = LocalizationService.Instance["AndroidStudioSdkRow"],
            IsCritical = false,
            Tier = SourceTier.C,
            OpenTarget = studio,
        };
    }

    public Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Id} can only be opened.");

    private static string? FindStudio()
    {
        foreach (var directory in AndroidSdkLocator.StudioInstallDirectories().Where(c => c.Length > 0))
        {
            foreach (var exe in new[] { "studio64.exe", "studio.exe" })
            {
                var full = Path.Combine(directory, "bin", exe);
                if (File.Exists(full))
                    return full;
            }
        }

        return null;
    }
}
