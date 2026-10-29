using System.Text.RegularExpressions;

namespace UltimateUpdater.Services.Sources.PackageManagers;

internal enum PipFailureKind
{
    None,

    /// <summary>A dependency of the offered version has no build for this Python.</summary>
    MissingDependency,

    /// <summary>The offered version cannot live next to the other installed packages.</summary>
    Conflict,
}

/// <summary>
/// Why a pinned pip upgrade cannot work on this interpreter. Pure parsing of pip's own output, plus the
/// one message wording that carries the result from the install to <see cref="InstallErrorExplainer"/>
/// (the row and the history only keep that text).
/// </summary>
internal readonly record struct PipInstallFailure(PipFailureKind Kind, string? Dependency)
{
    public static readonly PipInstallFailure None = new(PipFailureKind.None, null);

    private static readonly Regex NoMatchingDistribution = new(
        @"No matching distribution found for ([A-Za-z0-9][A-Za-z0-9._-]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Conflicting = new(
        @"ResolutionImpossible|conflicting dependencies",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Offline, behind a broken proxy or with a bad certificate pip prints the same "no matching
    // distribution" line, and that says nothing about the package: only a clean answer from the index counts.
    private static readonly string[] NetworkSignals =
    [
        "Retrying (", "NewConnectionError", "ConnectionError", "ProxyError", "SSLError", "Max retries exceeded",
        "Could not fetch URL", "timed out", "Temporary failure in name resolution", "Failed to establish",
        "getaddrinfo failed", "ReadTimeoutError", "ConnectTimeoutError",
    ];

    /// <param name="output">Everything pip printed.</param>
    /// <param name="package">The package being upgraded: a "no matching distribution" for the package itself
    /// is not a dependency problem.</param>
    public static PipInstallFailure Classify(string? output, string package)
    {
        if (string.IsNullOrWhiteSpace(output)
            || NetworkSignals.Any(signal => output.Contains(signal, StringComparison.OrdinalIgnoreCase)))
            return None;

        if (NoMatchingDistribution.Match(output) is { Success: true } missing)
        {
            var dependency = missing.Groups[1].Value;
            return PipBlockedStore.NormalizeName(dependency) == PipBlockedStore.NormalizeName(package)
                ? None
                : new PipInstallFailure(PipFailureKind.MissingDependency, dependency);
        }

        return Conflicting.IsMatch(output) ? new PipInstallFailure(PipFailureKind.Conflict, null) : None;
    }

    /// <summary>The first words of the exception message of a blocked install; <see cref="TryParseMessage"/>
    /// reads exactly this shape back.</summary>
    public static string BuildMessage(string package, string? version, PipInstallFailure failure) =>
        failure.Kind == PipFailureKind.MissingDependency
            ? $"pip: cannot install '{package}' version '{version}': missing dependency '{failure.Dependency}'."
            : $"pip: cannot install '{package}' version '{version}': conflicting dependencies.";

    private static readonly Regex MessageShape = new(
        @"cannot install '(?<package>[^']+)' version '(?<version>[^']*)': (?:missing dependency '(?<dependency>[^']+)'|(?<conflict>conflicting dependencies))\.",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseMessage(string? text, out string package, out string version, out PipInstallFailure failure)
    {
        package = version = string.Empty;
        failure = None;
        if (string.IsNullOrEmpty(text) || MessageShape.Match(text) is not { Success: true } match)
            return false;

        package = match.Groups["package"].Value;
        version = match.Groups["version"].Value;
        failure = match.Groups["conflict"].Success
            ? new PipInstallFailure(PipFailureKind.Conflict, null)
            : new PipInstallFailure(PipFailureKind.MissingDependency, match.Groups["dependency"].Value);
        return true;
    }
}
