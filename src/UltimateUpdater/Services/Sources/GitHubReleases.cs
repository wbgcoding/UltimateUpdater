namespace UltimateUpdater.Services.Sources;

/// <summary>
/// The one place the GitHub releases API is described. Several sources need "what is the newest release of
/// this repository": the tools that update themselves have no other way to know a newer version exists .
/// and they all need the same header to be answered at all.
/// </summary>
public static class GitHubReleases
{
    /// <summary>
    /// api.github.com answers a request with no User-Agent with 403. Passed per request so it never leaks
    /// onto the other sources sharing the same HttpClient.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Headers =
        new Dictionary<string, string> { ["User-Agent"] = "UltimateUpdater" };

    /// <summary>
    /// The latest-release endpoint for "owner/repo". Both halves are escaped even though every caller
    /// passes a constant: the rule is that a URL is built by escaping, not by trusting the caller.
    /// </summary>
    public static string LatestUrl(string ownerAndRepo)
    {
        var parts = ownerAndRepo.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException("A GitHub repository is written as 'owner/repo'.", nameof(ownerAndRepo));

        return $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/latest";
    }

    /// <summary>
    /// The endpoint for one specific tagged release. "tags" (plural), unlike the "tag" segment in the
    /// release page's own web URL; that mismatch is GitHub's own API, not a typo here.
    /// </summary>
    public static string TagUrl(string ownerAndRepo, string tag)
    {
        var parts = ownerAndRepo.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException("A GitHub repository is written as 'owner/repo'.", nameof(ownerAndRepo));

        return $"https://api.github.com/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/releases/tags/{Uri.EscapeDataString(tag)}";
    }
}

/// <summary>The GitLab counterpart to <see cref="GitHubReleases"/>, for a repository hosted on gitlab.com instead.</summary>
public static class GitLabReleases
{
    /// <summary>
    /// Both halves escaped even though every caller passes an already-validated value: a URL is built by
    /// escaping, not by trusting the caller, same rule as <see cref="GitHubReleases.LatestUrl"/>.
    /// </summary>
    public static string ReleaseUrl(string ownerAndRepo, string tag)
    {
        var parts = ownerAndRepo.Split('/');
        if (parts.Length < 2 || parts.Any(string.IsNullOrEmpty))
            throw new ArgumentException("A GitLab project is written as 'group/project', with optional subgroups between.", nameof(ownerAndRepo));

        var projectPath = Uri.EscapeDataString(string.Join('/', parts));
        return $"https://gitlab.com/api/v4/projects/{projectPath}/releases/{Uri.EscapeDataString(tag)}";
    }
}
