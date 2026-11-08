namespace UltimateUpdater.Models;

/// <summary>The two install scopes a package manager knows. Anything else is "not known".</summary>
public static class InstallScopes
{
    public const string User = "user";

    public const string Machine = "machine";

    /// <summary>The value when it is exactly one of the two scopes, else null. Exact and lower case: the
    /// value ends up on a winget command line, so nothing else may pass.</summary>
    public static string? Normalize(string? scope) =>
        scope is User or Machine ? scope : null;
}
