using System.IO;
using System.Net.Http;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// uv, the Python package and project manager, updating itself. `uv self update` refuses outright when uv was
/// installed by a package manager: the ownership check keeps us from asking it for winget, Scoop and
/// Chocolatey installs, and <see cref="IsAvailableAsync"/> adds the most common case it does not cover, uv
/// installed with pip or pipx (the pip/pipx sources offer that update themselves).
/// </summary>
public sealed class UvSelfSource(LoggingService logging, HttpClient httpClient)
    : SelfUpdatingToolSource(logging, httpClient)
{
    public override string Id => "uv";

    protected override string Executable => "uv";

    protected override string GitHubRepo => "astral-sh/uv";

    protected override string DisplayName => "uv";

    protected override IReadOnlyList<string> UpgradeArguments => ["self", "update"];

    public override async Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        await base.IsAvailableAsync(cancellationToken) && !IsInstalledByPythonTooling(ProcessRunner.Resolve(Executable));

    /// <summary>
    /// A uv.exe in an interpreter's Scripts folder (pip install uv), or one pipx keeps a venv for. Such a uv
    /// answers `self update` with "only available for uv binaries installed via the standalone installation
    /// scripts", so every scan offered a row whose install always failed.
    /// </summary>
    internal static bool IsInstalledByPythonTooling(string? resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath))
            return false;

        try
        {
            var directory = Path.GetDirectoryName(resolvedPath);
            if (directory is not null
                && string.Equals(Path.GetFileName(directory), "Scripts", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(directory) is { } interpreterRoot
                && (File.Exists(Path.Combine(interpreterRoot, "python.exe"))
                    || File.Exists(Path.Combine(interpreterRoot, "pyvenv.cfg"))))
            {
                return true;
            }

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] pipxVenvs =
            [
                Path.Combine(userProfile, "pipx", "venvs", "uv"),
                Path.Combine(localAppData, "pipx", "pipx", "venvs", "uv"),
                Path.Combine(userProfile, ".local", "pipx", "venvs", "uv"),
            ];
            return pipxVenvs.Any(Directory.Exists);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
