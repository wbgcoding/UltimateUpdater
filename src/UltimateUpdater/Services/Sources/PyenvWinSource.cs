using System.Net.Http;
using System.Runtime.CompilerServices;
using UltimateUpdater.Models;

namespace UltimateUpdater.Services.Sources;

/// <summary>
/// pyenv-win, scoped deliberately narrow; only build what can be stated with certainty.
/// pyenv manages SEVERAL Python versions side by side on purpose; the same reasoning
/// rejects nvm-windows/juliaup/GHCup outright for exactly this reason, because "update" there means
/// "install an additional version", not "replace the installed one". pyenv-win gets a source anyway, but
/// only for the one place it DOES have an unambiguous single "current version": the GLOBAL pin
/// (<c>pyenv global</c>). And only a same-major.minor PATCH bump is ever offered; never a minor/major
/// jump, which would be a real judgment call about which Python line to move to, not a safe one-click
/// update.
/// No live pyenv-win install exists on this dev box; commands/output shapes are verified against
/// pyenv-win's own source (`pyenv.vbs`) rather than assumed; see <see cref="PyenvWinParsers"/>.
/// </summary>
public sealed class PyenvWinSource(LoggingService logging) : IUpdateSource
{
    public string Id => "pyenv-win";

    public UpdateCategory Category => UpdateCategory.DeveloperTools;

    public SourceTier Tier => SourceTier.A;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ProcessRunner.Exists("pyenv"));

    /// <summary>
    /// python.org's own download layout is a fixed, predictable URL per version; no metadata API needed,
    /// only a HEAD for the installer's Content-Length. No date: python.org exposes no reliable per-build
    /// publish date at this path (see the class doc; the same reason the scan itself never sets one).
    /// </summary>
    public async Task<UpdateEnrichment?> EnrichAsync(UpdateItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion))
            return null;

        var url = $"https://www.python.org/ftp/python/{item.AvailableVersion}/python-{item.AvailableVersion}-amd64.exe";
        var size = await RegistryEnrichment.HeadContentLengthAsync(url, logging, Id, item.Id, cancellationToken);
        return size is null ? null : new UpdateEnrichment(null, size);
    }

    /// <summary>
    /// The global pin. A timeout fails the scan; any other outcome without one version (a non-zero exit,
    /// nothing set, several set) is null: "no global version set" is a valid state and the exit code of
    /// that case has never been measured, so it is not read as a failure.
    /// </summary>
    internal static string? ReadGlobalVersion(ProcessResult result)
    {
        if (result.TimedOut)
            throw new TimeoutException("pyenv-win: reading the global version timed out.");

        return result.ExitCode != 0 ? null : PyenvWinParsers.ParseGlobalVersion(result.StandardOutput);
    }

    /// <summary>`pyenv install -l` has no valid reason to fail, so a timeout or non-zero exit is a failed scan.</summary>
    internal static IReadOnlyList<string> ReadInstallableVersions(ProcessResult result)
    {
        if (result.TimedOut)
            throw new TimeoutException("pyenv-win: listing installable versions timed out.");

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"pyenv-win: 'pyenv install -l' exited with code {result.ExitCode}.");

        return PyenvWinParsers.ParseInstallableVersions(result.StandardOutput);
    }

    public async IAsyncEnumerable<UpdateItem> ScanAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var globalResult = await ProcessRunner.RunAsync("pyenv", ["global"], ExternalCallTimeouts.Default, cancellationToken);
        var installed = ReadGlobalVersion(globalResult);

        // No readable global version: never guess at "the" installed one. Logged at Info with the raw output;
        // a live VM run once showed `pyenv-win` cleanly detected but no update ever offered, and there
        // was no way afterwards to tell whether this branch (or the one below) was the reason.
        // Next time this is unclear, the rolling log has the actual answer.
        if (installed is null)
        {
            logging.Info(
                $"{Id}: no single, unambiguous global version; skipped. " +
                $"exitCode={globalResult.ExitCode} timedOut={globalResult.TimedOut} " +
                $"stdout='{globalResult.StandardOutput}' stderr='{globalResult.StandardError}'");
            yield break;
        }

        // pyenv-win fetches its version index on first use, which can genuinely exceed a short timeout;
        // that failure was silent (logged, then an empty result) rather than wrong, but 15s was too eager.
        var listResult = await ProcessRunner.RunAsync("pyenv", ["install", "-l"], ExternalCallTimeouts.Long, cancellationToken);
        var installable = ReadInstallableVersions(listResult);
        var update = PyenvWinParsers.FindSameLinePatchUpdate(installed, installable);
        if (update is null)
        {
            logging.Info(
                $"{Id}: no same-line patch update for '{installed}' among {installable.Count} installable versions.");
            yield break;
        }

        yield return new UpdateItem
        {
            Id = "pyenv-win-global",
            SourceId = Id,
            Category = Category,
            Name = "Python (pyenv global)",
            InstalledVersion = installed,
            AvailableVersion = update,
            IsCritical = SecurityRelevance.IsSecurityRelevant(Id, "Python"),
        };
    }

    public async Task InstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AvailableVersion))
            throw new InvalidOperationException($"{Id}: missing install target for '{item.Name}'.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var install = await ProcessRunner.RunStreamingAsync(
            "pyenv",
            ["install", "-q", item.AvailableVersion],
            TimeSpan.FromMinutes(10),
            segment => progress.Report(new InstallProgress { PercentComplete = 50, StatusText = $"{item.Name}: {segment.Trim()}" }),
            cancellationToken);

        if (install.TimedOut)
            throw new InvalidOperationException($"{Id}: installing Python {item.AvailableVersion} timed out.");

        if (install.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(install.StandardOutput, install.StandardError, install.ExitCode);
            throw new InvalidOperationException($"{Id}: installing Python {item.AvailableVersion} failed. {detail}");
        }

        progress.Report(new InstallProgress { PercentComplete = 90, StatusText = item.Name });

        var global = await ProcessRunner.RunAsync("pyenv", ["global", item.AvailableVersion], ExternalCallTimeouts.Default, cancellationToken);
        if (global.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(global.StandardOutput, global.StandardError, global.ExitCode);
            throw new InvalidOperationException(
                $"{Id}: Python {item.AvailableVersion} installed, but setting it as the global version failed. {detail}");
        }

        // pyenv-win's own help text calls this out explicitly ("run this after installing executables");
        // neither `install` nor `global` does it implicitly. A failure here is not fatal: the version IS
        // installed and set as global, just not yet reachable through pyenv's shims until the next rehash.
        var rehash = await ProcessRunner.RunAsync("pyenv", ["rehash"], ExternalCallTimeouts.Default, cancellationToken);
        if (rehash.ExitCode != 0)
            logging.Warn($"{Id}: 'pyenv rehash' failed after installing {item.AvailableVersion}; shims may be stale until it is re-run.");

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }

    /// <summary>The row's id is the synthetic "pyenv-win-global" pin, not a real version; removal targets
    /// <see cref="UpdateItem.InstalledVersion"/>, the actual Python version currently pinned.</summary>
    public bool CanUninstall(UpdateItem item) => !string.IsNullOrWhiteSpace(item.InstalledVersion);

    public async Task UninstallAsync(UpdateItem item, IProgress<InstallProgress> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.InstalledVersion) || !ProcessRunner.IsSafePositionalArgument(item.InstalledVersion))
            throw new InvalidOperationException($"{Id}: refusing to remove; no plain installed version for '{item.Name}'.");

        progress.Report(new InstallProgress { PercentComplete = 0, StatusText = item.Name });

        var result = await ProcessRunner.RunAsync("pyenv", ["uninstall", "-f", item.InstalledVersion], ExternalCallTimeouts.Long, cancellationToken);

        if (result.TimedOut)
            throw new InvalidOperationException($"{Id}: removing Python {item.InstalledVersion} timed out.");

        if (result.ExitCode != 0)
        {
            var detail = ToolOutput.Summarize(result.StandardOutput, result.StandardError, result.ExitCode);
            throw new InvalidOperationException($"{Id}: removing Python {item.InstalledVersion} failed. {detail}");
        }

        progress.Report(new InstallProgress { PercentComplete = 100, StatusText = item.Name });
    }
}
