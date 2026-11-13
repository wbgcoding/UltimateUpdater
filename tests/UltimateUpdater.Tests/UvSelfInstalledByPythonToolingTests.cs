using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// A uv.exe that pip or pipx installed answers "uv self update" with an error, so the source must stay
/// silent for it rather than offer a row whose install always fails.
/// </summary>
public sealed class UvSelfInstalledByPythonToolingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uu-uvself-" + Guid.NewGuid().ToString("N"));

    public UvSelfInstalledByPythonToolingTests() => Directory.CreateDirectory(_root);

    /// <summary>Creates "{interpreterFolder}/{scriptsName}/uv.exe" plus the given marker files in the interpreter folder.</summary>
    private string UvUnder(string interpreterFolder, string scriptsName, params string[] markers)
    {
        var interpreter = Path.Combine(_root, interpreterFolder);
        var scripts = Path.Combine(interpreter, scriptsName);
        Directory.CreateDirectory(scripts);
        foreach (var marker in markers)
            File.WriteAllText(Path.Combine(interpreter, marker), "x");
        var uv = Path.Combine(scripts, "uv.exe");
        File.WriteAllText(uv, "x");
        return uv;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_path_is_not_python_tooling(string? path) =>
        Assert.False(UvSelfSource.IsInstalledByPythonTooling(path));

    [Fact]
    public void A_uv_in_an_interpreters_scripts_folder_next_to_python_exe_counts() =>
        Assert.True(UvSelfSource.IsInstalledByPythonTooling(UvUnder("py312", "Scripts", "python.exe")));

    [Fact]
    public void A_uv_in_a_virtualenv_scripts_folder_counts() =>
        Assert.True(UvSelfSource.IsInstalledByPythonTooling(UvUnder("venv", "Scripts", "pyvenv.cfg")));

    [Fact]
    public void The_scripts_name_is_matched_ignoring_case() =>
        Assert.True(UvSelfSource.IsInstalledByPythonTooling(UvUnder("py311", "SCRIPTS", "python.exe")));

    [Fact]
    public void A_scripts_folder_without_an_interpreter_beside_it_gets_no_special_treatment()
    {
        var scriptsOnly = UvUnder("lonely", "Scripts");
        var plainFolder = UvUnder("standalone", "bin", "python.exe");

        // neither is python tooling by the folder rule, so both fall to the machine's pipx check, which
        // gives the same answer for any path
        Assert.Equal(UvSelfSource.IsInstalledByPythonTooling(plainFolder),
            UvSelfSource.IsInstalledByPythonTooling(scriptsOnly));
    }

    [Fact]
    public void A_path_with_invalid_characters_never_throws()
    {
        var weird = UvSelfSource.IsInstalledByPythonTooling("C:\\bad\0name\\Scripts\\uv.exe");

        Assert.Equal(UvSelfSource.IsInstalledByPythonTooling(Path.Combine(_root, "x", "uv.exe")), weird);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
