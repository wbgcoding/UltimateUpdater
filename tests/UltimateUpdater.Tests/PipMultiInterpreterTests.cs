using UltimateUpdater.Models;
using UltimateUpdater.Services.Sources.PackageManagers;

namespace UltimateUpdater.Tests;

/// <summary>
/// A single-interpreter machine must keep the plain package name it always had for a row's Id, so a
/// second interpreter appearing later never renames an existing row out from under anything that keyed
/// on it (history, exclusions, snoozes). Once a second interpreter is in the picture, a bare package name
/// can no longer tell two rows apart, and the Id grows the interpreter's own path.
/// <see cref="PythonInterpretersTests"/> covers discovery itself; this covers the id/routing rules built
/// on top of it.
/// </summary>
public class PipMultiInterpreterTests
{
    private static readonly PythonInterpreter Python313 =
        new(@"C:\Users\testuser\AppData\Local\Programs\Python\Python313\python.exe", new Version(3, 13), PerUser: true, Is64Bit: true);

    private static readonly PythonInterpreter Python314 =
        new(@"C:\Users\testuser\AppData\Local\Programs\Python\Python314\python.exe", new Version(3, 14), PerUser: true, Is64Bit: true);

    [Fact]
    public void A_single_interpreter_machine_keeps_the_plain_package_name_as_the_id()
    {
        Assert.Equal("requests", PipSource.BuildId("requests", Python313, multiple: false));
    }

    [Fact]
    public void Two_interpreters_carrying_the_same_package_get_two_distinct_ids()
    {
        var id313 = PipSource.BuildId("requests", Python313, multiple: true);
        var id314 = PipSource.BuildId("requests", Python314, multiple: true);

        Assert.NotEqual(id313, id314);
        Assert.Contains(Python313.PythonExe, id313);
        Assert.Contains(Python314.PythonExe, id314);
    }

    [Fact]
    public void Building_then_parsing_a_composite_id_round_trips_the_package_and_interpreter()
    {
        var id = PipSource.BuildId("requests", Python314, multiple: true);

        var (packageName, interpreterPath) = PipSource.ParseId(id);

        Assert.Equal("requests", packageName);
        Assert.Equal(Python314.PythonExe, interpreterPath);
    }

    [Fact]
    public void Parsing_a_plain_single_interpreter_id_yields_no_interpreter_path()
    {
        var (packageName, interpreterPath) = PipSource.ParseId("requests");

        Assert.Equal("requests", packageName);
        Assert.Null(interpreterPath);
    }

    private static UpdateItem Item(string id, IReadOnlyDictionary<string, string>? meta = null) => new()
    {
        Id = id, SourceId = "pip", Category = UpdateCategory.DeveloperTools, Name = id,
        Meta = meta ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void Package_name_reads_from_meta_when_present_even_for_a_composite_id()
    {
        var item = Item(
            PipSource.BuildId("requests", Python313, multiple: true),
            new Dictionary<string, string> { ["pipPackage"] = "requests", ["pythonPath"] = Python313.PythonExe });

        Assert.Equal("requests", PipSource.PackageName(item));
        Assert.Equal(Python313.PythonExe, PipSource.InterpreterPath(item));
    }

    [Fact]
    public void Package_name_falls_back_to_the_plain_id_when_meta_is_missing()
    {
        var item = Item("requests");

        Assert.Equal("requests", PipSource.PackageName(item));
    }

    [Fact]
    public void Interpreter_path_falls_back_to_own_python_when_meta_is_missing()
    {
        var item = Item("requests");

        Assert.Equal(PipSource.OwnPython(), PipSource.InterpreterPath(item));
    }

    /// <summary>
    /// On a single-interpreter machine the row Id is a plain package
    /// name with no "#", so recovering the interpreter by parsing the Id alone (<see cref="PipSource.ParseId"/>)
    /// always comes back null there, even though the real interpreter path sits right in the item's own
    /// Meta. <see cref="PipSource.GetInstalledVersionAsync"/> used to receive only the bare Id string and
    /// so had no way to reach Meta at all; it now takes the full item and reads
    /// <see cref="PipSource.InterpreterPath"/> directly, which this proves picks the Meta path over
    /// <see cref="PipSource.OwnPython"/> for exactly this plain-id, meta-present case.
    /// </summary>
    [Fact]
    public void Interpreter_path_prefers_meta_over_own_python_for_a_plain_single_interpreter_id()
    {
        var item = Item(
            "requests",
            new Dictionary<string, string> { ["pipPackage"] = "requests", ["pythonPath"] = Python313.PythonExe });

        Assert.Equal(Python313.PythonExe, PipSource.InterpreterPath(item));
        Assert.NotEqual(PipSource.OwnPython(), PipSource.InterpreterPath(item));
    }
}
