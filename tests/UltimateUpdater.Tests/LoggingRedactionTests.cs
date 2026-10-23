using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>
/// A failing install is logged together with the tool's own output, and package managers print absolute
/// paths, so the rolling log filled up with the user's profile directory, which carries their account
/// name. The log is the file people hand over when they ask for help, so the path stays readable but the
/// identity does not.
/// </summary>
public class LoggingRedactionTests
{
    private static string Profile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');

    [Fact]
    public void The_user_profile_directory_is_replaced()
    {
        var message = $@"pip: install failed. Requirement already satisfied in {Profile}\AppData\Local\Programs\Python";

        var redacted = LoggingService.Redact(message);

        Assert.DoesNotContain(Profile, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\Programs\Python", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_occurrence_goes_not_just_the_first()
    {
        var message = $"{Profile}\\a and {Profile}\\b";

        Assert.DoesNotContain(Profile, LoggingService.Redact(message), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Case_differences_are_still_caught()
    {
        var message = Profile.ToUpperInvariant() + @"\Downloads\thing.exe";

        Assert.DoesNotContain(Profile, LoggingService.Redact(message), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>npm, pip and node print Windows paths with forward slashes, so that spelling reaches the
    /// log just as often as the native one, and carries the same account name.</summary>
    [Fact]
    public void The_forward_slash_spelling_of_the_profile_is_caught_too()
    {
        var message = $"npm ERR! path {Profile.Replace('\\', '/')}/AppData/Roaming/npm/node_modules";

        var redacted = LoggingService.Redact(message);

        Assert.DoesNotContain(Profile.Replace('\\', '/'), redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%/AppData/Roaming/npm", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_without_a_path_is_untouched()
    {
        const string message = "Scan failed for source 'winget'.";

        Assert.Equal(message, LoggingService.Redact(message));
    }

    /// <summary>
    /// A package manager's own output is copied into the log verbatim, and a private registry, a git
    /// remote or an echoed request header brings a credential with it. The shape stays so the line is
    /// still diagnosable; the value does not.
    /// </summary>
    [Theory]
    [InlineData("npm ERR! https://user:s3cr3t-value@registry.internal/pkg", "s3cr3t-value")]
    [InlineData("GET https://api.example.com/v1/pkg?api_key=AKIA12345EXAMPLE&page=2", "AKIA12345EXAMPLE")]
    [InlineData("request failed, authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc", "eyJhbGciOiJIUzI1NiJ9.abc")]
    [InlineData("could not read _authToken=npm_ZZZZexampletoken from .npmrc", "npm_ZZZZexampletoken")]
    public void Credential_shaped_text_never_reaches_the_log(string message, string secret)
    {
        var redacted = LoggingService.Redact(message);

        Assert.DoesNotContain(secret, redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_url_keeps_its_diagnostic_value()
    {
        const string message = "HTTP -> https://registry.npmjs.org/%40babel%2Fcore";

        Assert.Equal(message, LoggingService.Redact(message));
    }

    /// <summary>The samples are assembled at run time so no file here holds a string a secret scanner flags.</summary>
    [Fact]
    public void A_bare_token_is_redacted_by_its_prefix()
    {
        var github = "gh" + "p_" + new string('A', 36);
        var npm = "np" + "m_" + new string('B', 36);

        var redacted = LoggingService.Redact($"gh: using {github}; npm: {npm}");

        Assert.DoesNotContain(github, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(npm, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bearer_value_without_its_header_name_is_redacted()
    {
        var value = new string('c', 40);

        var redacted = LoggingService.Redact($"request failed, sent Bearer {value}");

        Assert.DoesNotContain(value, redacted, StringComparison.Ordinal);
        Assert.Contains("Bearer ***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_word_after_bearer_is_kept()
    {
        const string message = "the bearer of this message";

        Assert.Equal(message, LoggingService.Redact(message));
    }

    /// <summary>The forms the literal profile replace cannot see: Python's doubled backslashes, an 8.3 short
    /// name, another account's profile.</summary>
    [Theory]
    [InlineData(@"OSError: [WinError 5] Access is denied: 'C:\\Users\\someone\\AppData\\x'", "someone")]
    [InlineData(@"extracting to C:\Users\USER~1\AppData\Local\Temp\a", "USER~1")]
    [InlineData(@"C:/Users/otheruser/.npmrc", "otheruser")]
    public void Other_spellings_of_a_user_profile_are_redacted(string message, string userName) =>
        Assert.DoesNotContain(userName, LoggingService.Redact(message));

    [Theory]
    [InlineData("https://user:p@ss@registry.example.com/", "ss@registry")]
    [InlineData("{ \"_authToken\": \"abc123secret\" }", "abc123secret")]
    public void Credentials_with_awkward_shapes_are_redacted(string message, string secret) =>
        Assert.DoesNotContain(secret, LoggingService.Redact(message));

    [Fact]
    public void Public_and_default_profiles_are_kept() =>
        Assert.Contains(@"C:\Users\Public\Desktop", LoggingService.Redact(@"C:\Users\Public\Desktop\x.lnk"));
}
