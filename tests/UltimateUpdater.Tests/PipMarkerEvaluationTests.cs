using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// The dependency guard decides from the markers of installed packages whether a requirement applies to
/// the scanned interpreter. "Applies" is the safe answer whenever a marker cannot be decided.
/// </summary>
public class PipMarkerEvaluationTests
{
    private static Dictionary<string, string> Windows313() => new()
    {
        ["python_version"] = "3.13",
        ["python_full_version"] = "3.13.1",
        ["sys_platform"] = "win32",
        ["platform_system"] = "Windows",
        ["os_name"] = "nt",
        ["platform_machine"] = "AMD64",
        ["implementation_name"] = "cpython",
        ["extra"] = string.Empty,
    };

    private static Dictionary<string, string> Windows311()
    {
        var environment = Windows313();
        environment["python_version"] = "3.11";
        environment["python_full_version"] = "3.11.9";
        return environment;
    }

    [Fact]
    public void An_extra_clause_does_not_hide_a_python_version_clause_joined_by_or() =>
        Assert.True(PipDependencyGuard.MarkerApplies("extra == \"docs\" or python_version < \"3.12\"", Windows311()));

    [Fact]
    public void Every_clause_of_an_or_is_false_so_the_requirement_does_not_apply() =>
        Assert.False(PipDependencyGuard.MarkerApplies("sys_platform == \"linux\" or python_version == \"2.7\"", Windows313()));

    [Fact]
    public void And_binds_tighter_than_or_and_parentheses_group() =>
        Assert.False(PipDependencyGuard.MarkerApplies(
            "(python_version < \"3.9\" or os_name == \"nt\") and extra == \"x\"", Windows313()));

    [Fact]
    public void Precedence_without_parentheses_is_and_before_or() =>
        // Read with "and" first this is true or (false and false) = true; read left to right it would be
        // (true or false) and false = false.
        Assert.True(PipDependencyGuard.MarkerApplies(
            "os_name == \"nt\" or sys_platform == \"linux\" and python_version < \"3.0\"", Windows313()));

    [Fact]
    public void An_unknown_variable_keeps_the_requirement() =>
        Assert.True(PipDependencyGuard.MarkerApplies("some_future_variable == \"x\"", Windows313()));

    [Fact]
    public void A_false_clause_still_decides_an_and_even_next_to_an_unknown_one() =>
        Assert.False(PipDependencyGuard.MarkerApplies("sys_platform == \"linux\" and some_future_variable == \"x\"", Windows313()));

    [Theory]
    [InlineData("sys_platform ==")]
    [InlineData("== \"win32\"")]
    [InlineData("sys_platform == \"win32")]
    [InlineData("(sys_platform == \"linux\"")]
    [InlineData("sys_platform === \"linux\" nonsense")]
    [InlineData("sys_platform @ \"linux\"")]
    [InlineData("")]
    public void A_marker_that_does_not_parse_keeps_the_requirement(string marker) =>
        Assert.True(PipDependencyGuard.MarkerApplies(marker, Windows313()));

    [Fact]
    public void An_ordering_operator_on_text_that_is_not_a_version_keeps_the_requirement() =>
        Assert.True(PipDependencyGuard.MarkerApplies("sys_platform > \"a\"", Windows313()));

    [Fact]
    public void A_variable_may_stand_on_the_right() =>
        Assert.False(PipDependencyGuard.MarkerApplies("\"3.9\" > python_version", Windows313()));

    [Theory]
    [InlineData("python_version >= \"3.9\"", true)]
    [InlineData("python_version < \"3.9\"", false)]
    [InlineData("python_version == \"3.13\"", true)]
    [InlineData("python_version != \"3.13\"", false)]
    [InlineData("python_full_version >= \"3.13.0\"", true)]
    [InlineData("python_version ~= \"3.9\"", true)]
    [InlineData("python_version < \"3.9\" or python_version >= \"3.10\"", true)]
    [InlineData("sys_platform in \"win32 cygwin\"", true)]
    [InlineData("sys_platform not in \"linux darwin\"", true)]
    [InlineData("platform_machine == \"AMD64\"", true)]
    [InlineData("implementation_name == 'pypy'", false)]
    public void Markers_evaluate_against_the_reported_environment(string marker, bool expected) =>
        Assert.Equal(expected, PipDependencyGuard.MarkerApplies(marker, Windows313()));

    [Fact]
    public void A_name_that_is_a_JSON_number_skips_that_entry_without_throwing()
    {
        const string json = """
        {
          "installed": [
            { "metadata": { "name": 5, "requires_dist": ["bar==1.0"] } },
            { "metadata": { "name": "good", "requires_dist": ["bar==1.0", 7, "baz<2"] } },
            "not an object",
            { "metadata": "not an object" },
            { "metadata": { "name": "also-good", "requires_dist": "not an array" } }
          ],
          "environment": { "python_version": "3.13" }
        }
        """;

        var report = PipDependencyGuard.Parse(json);

        Assert.NotNull(report);
        Assert.Equal(2, report!.Constraints.Count);
        Assert.All(report.Constraints, c => Assert.Equal("good", c.Dependent));
    }

    [Fact]
    public void An_environment_that_is_not_an_object_is_an_empty_environment()
    {
        const string json = """
        {
          "installed": [
            { "metadata": { "name": "a", "requires_dist": ["bar==1.0; sys_platform == \"linux\""] } }
          ],
          "environment": 12
        }
        """;

        var report = PipDependencyGuard.Parse(json);

        // sys_platform is not known, so the marker cannot be decided and the constraint stays.
        Assert.Single(report!.Constraints);
        Assert.Null(report.PythonVersion);
    }

    [Fact]
    public void A_python_version_that_is_not_a_string_is_ignored()
    {
        const string json = """{ "installed": [], "environment": { "python_version": 3.13 } }""";

        Assert.Null(PipDependencyGuard.Parse(json)!.PythonVersion);
    }

    [Fact]
    public void A_root_that_is_not_an_object_is_not_a_report() =>
        Assert.Null(PipDependencyGuard.Parse("[1, 2]"));

    [Fact]
    public void An_extra_gated_requirement_is_dropped_but_an_or_with_a_live_clause_is_kept()
    {
        const string json = """
        {
          "installed": [
            { "metadata": { "name": "a", "requires_dist": [
              "docs-only<2; extra == \"docs\"",
              "live<2; extra == \"docs\" or python_version < \"3.12\"",
              "gone<2; sys_platform == \"linux\" or python_version == \"2.7\""
            ] } }
          ],
          "environment": { "python_version": "3.11", "sys_platform": "win32" }
        }
        """;

        var report = PipDependencyGuard.Parse(json)!;

        Assert.Equal(["live"], report.Constraints.Select(c => c.DependencyName).ToArray());
    }
}
