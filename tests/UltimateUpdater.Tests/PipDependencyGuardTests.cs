using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// The fixture JSON below is trimmed from a real <c>pip inspect --disable-pip-version-check</c> run:
/// pydantic pins pydantic-core==2.46.5, semgrep pins
/// opentelemetry-api~=1.37.0, datasets pins huggingface-hub&lt;2.0,&gt;=0.25.0. Paths and every field not
/// needed for the check are dropped; the three <c>requires_dist</c> entries are the real strings pip
/// printed, kept verbatim including their mixed quote styles.
/// </summary>
public class PipDependencyGuardTests
{
    private const string RealFixtureJson = """
    {
      "version": "1",
      "pip_version": "26.2.1",
      "installed": [
        {
          "metadata": {
            "name": "pydantic",
            "version": "2.13.5",
            "requires_dist": [
              "annotated-types>=0.6.0",
              "pydantic-core==2.46.5",
              "typing-extensions>=4.14.1",
              "typing-inspection>=0.4.2",
              "email-validator>=2.0.0; extra == 'email'",
              "tzdata; (python_version >= '3.9' and platform_system == 'Windows') and extra == 'timezone'"
            ]
          }
        },
        {
          "metadata": {
            "name": "semgrep",
            "version": "1.177.0",
            "requires_dist": [
              "opentelemetry-api~=1.37.0",
              "requests~=2.22",
              "pywin32==311; sys_platform == \"win32\""
            ]
          }
        },
        {
          "metadata": {
            "name": "datasets",
            "version": "5.0.1",
            "requires_dist": [
              "huggingface-hub<2.0,>=0.25.0",
              "numpy>=1.17",
              "torch>=2.8.0; extra == \"audio\""
            ]
          }
        },
        {
          "metadata": {
            "name": "unconstrained-tool",
            "version": "1.0",
            "requires_dist": [
              "some-lib"
            ]
          }
        }
      ],
      "environment": {
        "os_name": "nt",
        "platform_system": "Windows",
        "python_version": "3.13",
        "sys_platform": "win32"
      }
    }
    """;

    private static PipDependencyGuard.Report ParseFixture()
    {
        var report = PipDependencyGuard.Parse(RealFixtureJson);
        Assert.NotNull(report);
        return report!;
    }

    [Fact]
    public void Parse_reads_the_python_version_from_the_environment()
    {
        Assert.Equal("3.13", ParseFixture().PythonVersion);
    }

    [Fact]
    public void Parse_skips_a_requirement_gated_by_an_extra()
    {
        var report = ParseFixture();

        Assert.DoesNotContain(report.Constraints, c => c.DependencyName == "email-validator");
        Assert.DoesNotContain(report.Constraints, c => c.DependencyName == "tzdata");
        Assert.DoesNotContain(report.Constraints, c => c.DependencyName == "torch");
    }

    [Fact]
    public void Parse_keeps_a_requirement_gated_by_sys_platform_win32()
    {
        var report = ParseFixture();

        Assert.Contains(report.Constraints, c => c.DependencyName == "pywin32" && c.Dependent == "semgrep");
    }

    [Fact]
    public void Parse_drops_a_dependency_with_no_version_specifier_at_all()
    {
        var report = ParseFixture();

        Assert.DoesNotContain(report.Constraints, c => c.DependencyName == "some-lib");
    }

    /// <summary>Three real dependency pairs, plus one unconstrained package that must still
    /// show as an update.</summary>
    [Fact]
    public void FindHeldBack_drops_exactly_the_versions_a_dependent_forbids()
    {
        var report = ParseFixture();

        var candidates = new[]
        {
            new PipDependencyGuard.Candidate("pydantic-core", "2.47.0"),   // pydantic pins ==2.46.5
            new PipDependencyGuard.Candidate("opentelemetry-api", "1.38.0"), // semgrep pins ~=1.37.0
            new PipDependencyGuard.Candidate("huggingface-hub", "2.1.0"),   // datasets pins <2.0
            new PipDependencyGuard.Candidate("requests", "2.32.0"),         // unconstrained: still shows
        };

        var heldBack = PipDependencyGuard.FindHeldBack(candidates, report);
        var heldBackIds = heldBack.Select(h => h.Id).ToHashSet();

        Assert.Contains("pydantic-core", heldBackIds);
        Assert.Contains("opentelemetry-api", heldBackIds);
        Assert.Contains("huggingface-hub", heldBackIds);
        Assert.DoesNotContain("requests", heldBackIds);
    }

    [Fact]
    public void FindHeldBack_lets_a_version_the_dependent_still_allows_through()
    {
        var report = ParseFixture();

        var candidates = new[]
        {
            // still satisfies pydantic's own ==2.46.5 pin
            new PipDependencyGuard.Candidate("pydantic-core", "2.46.5"),
            // still within datasets' <2.0,>=0.25.0 window
            new PipDependencyGuard.Candidate("huggingface-hub", "1.9.0"),
        };

        Assert.Empty(PipDependencyGuard.FindHeldBack(candidates, report));
    }

    [Fact]
    public void FindHeldBack_matches_names_after_PEP503_normalisation()
    {
        var report = ParseFixture();

        // pip itself would report this candidate normalized/lower-cased even if some tool spelled it
        // with underscores; the match must not depend on which spelling either side used.
        var candidates = new[] { new PipDependencyGuard.Candidate("Pydantic_Core", "2.47.0") };

        Assert.Single(PipDependencyGuard.FindHeldBack(candidates, report));
    }

    [Fact]
    public void HeldBack_log_line_names_the_package_the_version_and_the_dependent()
    {
        var report = ParseFixture();
        var candidates = new[] { new PipDependencyGuard.Candidate("pydantic-core", "2.47.0") };

        var line = PipDependencyGuard.FindHeldBack(candidates, report).Single().LogLine;

        Assert.Contains("pydantic-core", line);
        Assert.Contains("2.47.0", line);
        Assert.Contains("pydantic", line);
    }

    [Fact]
    public void Parse_unwraps_a_parenthesised_legacy_specifier_and_ignores_a_direct_reference()
    {
        var report = PipDependencyGuard.Parse("""
        {
          "installed": [
            { "metadata": { "name": "python-dateutil", "version": "2.8.2",
                            "requires_dist": [ "six (>=1.5)", "foo @ https://example.invalid/foo-1.0-py3-none-any.whl" ] } }
          ],
          "environment": { "python_version": "3.13" }
        }
        """);

        Assert.NotNull(report);
        var six = Assert.Single(report!.Constraints, c => c.DependencyName == "six");
        Assert.Equal(">=1.5", six.SpecifierSet);
        Assert.DoesNotContain(report.Constraints, c => c.DependencyName == "foo");
    }

    [Fact]
    public void Parse_returns_null_for_output_that_is_not_the_expected_json()
    {
        Assert.Null(PipDependencyGuard.Parse("ERROR: unknown command 'inspect'"));
    }

    [Fact]
    public void Parse_returns_null_for_json_missing_the_installed_array()
    {
        Assert.Null(PipDependencyGuard.Parse("""{ "version": "1" }"""));
    }
}
