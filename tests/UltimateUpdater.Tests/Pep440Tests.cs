using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

public class Pep440Tests
{
    [Theory]
    [InlineData("Foo-Bar", "foo-bar")]
    [InlineData("foo_bar", "foo-bar")]
    [InlineData("foo.bar", "foo-bar")]
    [InlineData("FOO--Bar__Baz", "foo-bar-baz")]
    public void NormalizeName_folds_case_and_separators(string name, string expected) =>
        Assert.Equal(expected, Pep440.NormalizeName(name));

    [Theory]
    [InlineData("2.46.5", "==2.46.5", true)]
    [InlineData("2.46.4", "==2.46.5", false)]
    [InlineData("2.46.5", "!=2.46.5", false)]
    [InlineData("1.9", "<2.0", true)]
    [InlineData("2.0", "<2.0", false)]
    [InlineData("2.0", "<=2.0", true)]
    [InlineData("2.1", ">2.0", true)]
    [InlineData("2.0", ">=2.0", true)]
    [InlineData("1.9", ">=2.0", false)]
    public void Satisfies_evaluates_a_single_clause(string version, string specifier, bool expected) =>
        Assert.Equal(expected, Pep440.Satisfies(version, specifier));

    /// <summary>The real datasets -> huggingface-hub pair: a comma joins two clauses, both must hold.</summary>
    [Theory]
    [InlineData("1.9", true)]
    [InlineData("0.30.0", true)]
    [InlineData("2.0", false)]
    [InlineData("0.24.0", false)]
    public void Satisfies_ANDs_comma_separated_clauses(string version, bool expected) =>
        Assert.Equal(expected, Pep440.Satisfies(version, "<2.0,>=0.25.0"));

    /// <summary>The real semgrep -> opentelemetry-api pair: "~=1.37.0" means at least 1.37.0 but never
    /// 1.38 or 2.0.</summary>
    [Theory]
    [InlineData("1.37.0", true)]
    [InlineData("1.37.9", true)]
    [InlineData("1.36.9", false)]
    [InlineData("1.38.0", false)]
    [InlineData("2.0.0", false)]
    public void Satisfies_evaluates_compatible_release(string version, bool expected) =>
        Assert.Equal(expected, Pep440.Satisfies(version, "~=1.37.0"));

    [Theory]
    [InlineData("2.0", "==2.*", true)]
    [InlineData("2.5.1", "==2.*", true)]
    [InlineData("3.0", "==2.*", false)]
    [InlineData("3.0", "!=2.*", true)]
    [InlineData("2", "==2.0.*", true)]
    [InlineData("2", "==2.1.*", false)]
    public void Satisfies_evaluates_wildcard(string version, string specifier, bool expected) =>
        Assert.Equal(expected, Pep440.Satisfies(version, specifier));

    /// <summary>
    /// A raw string prefix check used to run alongside the segment-aware
    /// one, and "2.1" is a literal string prefix of "2.10.0" even though its second release segment (10)
    /// is not 1 - so "foo==2.1.*" wrongly accepted an offered "foo 2.10.0", letting an upgrade through that
    /// violates the very constraint meant to block it. Segment comparison alone must refuse this.
    /// </summary>
    [Theory]
    [InlineData("2.10.0", "==2.1.*", false)]
    [InlineData("2.1.0", "==2.1.*", true)]
    [InlineData("2.1.9", "==2.1.*", true)]
    [InlineData("2.10.0", "!=2.1.*", true)]
    public void Satisfies_wildcard_never_matches_by_raw_string_prefix(string version, string specifier, bool expected) =>
        Assert.Equal(expected, Pep440.Satisfies(version, specifier));

    [Fact]
    public void A_prerelease_sorts_before_the_final_release()
    {
        Assert.False(Pep440.Satisfies("1.0a1", ">=1.0"));
        Assert.True(Pep440.Satisfies("1.0", ">=1.0a1"));
    }

    [Fact]
    public void A_dev_release_sorts_before_a_prerelease()
    {
        Assert.False(Pep440.Satisfies("1.0.dev1", ">=1.0a1"));
    }

    [Fact]
    public void A_post_release_sorts_after_the_plain_release()
    {
        Assert.False(Pep440.Satisfies("1.0", ">=1.0.post1"));
        Assert.True(Pep440.Satisfies("1.0.post1", ">=1.0"));
    }

    [Fact]
    public void Unparseable_version_fails_closed()
    {
        Assert.False(Pep440.Satisfies("not-a-version", ">=1.0"));
    }

    [Fact]
    public void An_epoch_outranks_the_release_and_must_match_for_equality()
    {
        Assert.True(Pep440.Satisfies("1!2.0", "<1!3.0"));
        Assert.False(Pep440.Satisfies("1!2.0", "==2.0"));
        Assert.True(Pep440.Satisfies("1!1.0", ">2.0"));
    }

    [Theory]
    [InlineData("2.0garbage")]
    [InlineData("2.0 beta tester")]
    [InlineData("1.0.0.")]
    [InlineData("")]
    public void Trailing_garbage_is_not_silently_ignored(string version) =>
        Assert.False(Pep440.Satisfies(version, ">=0.1"));

    [Fact]
    public void Numbers_past_the_integer_range_still_compare()
    {
        Assert.True(Pep440.Satisfies("1.99999999999999", ">1.2"));
        Assert.False(Pep440.Satisfies("1.2", ">1.99999999999999"));
        Assert.True(Pep440.Satisfies("1.00002", "==1.2"));
    }

    [Fact]
    public void A_local_version_is_accepted_and_ignored_for_ordering()
    {
        Assert.True(Pep440.Satisfies("2.1.0+cu118", "==2.1.0"));
        Assert.True(Pep440.Satisfies("2.1.0", "==2.1.0+cpu"));
    }

    [Theory]
    [InlineData("1.0-rc1", "==1.0rc1")]
    [InlineData("1.0alpha1", "==1.0a1")]
    [InlineData("1.0.beta.2", "==1.0b2")]
    [InlineData("1.0preview3", "==1.0rc3")]
    [InlineData("1.0-1", "==1.0.post1")]
    [InlineData("1.0rev2", "==1.0.post2")]
    [InlineData("1.0rc", "==1.0rc0")]
    [InlineData("v1.0.DEV", "==1.0.dev0")]
    public void Alternate_spellings_normalise_to_the_same_version(string version, string specifier) =>
        Assert.True(Pep440.Satisfies(version, specifier));

    [Theory]
    [InlineData("24.1a1", "23.12.1", false)]
    [InlineData("1.0rc1", "1.0", true)]
    [InlineData("1.0", "1.0rc1", false)]
    [InlineData("1.0.post1", "1.0", false)]
    [InlineData("1.0", "1.0.post1", true)]
    public void IsNewer_orders_python_versions_by_PEP440(string installed, string available, bool expected) =>
        Assert.Equal(expected, Pep440.IsNewer(installed, available));
}
