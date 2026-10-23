using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UltimateUpdater.Services;
using Xunit;

namespace UltimateUpdater.Tests;

public class NativeLibraryHardeningTests
{
    [Fact]
    public void The_assembly_restricts_P_Invoke_probing_to_System32()
    {
        var attribute = typeof(NativeLibraryHardening).Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DllImportSearchPath.System32, attribute!.Paths);
    }

    [Theory]
    [InlineData("wintrust.dll")]
    [InlineData("kernel32.dll")]
    [InlineData("amsi.dll")]
    [InlineData("shell32")]
    public void A_bare_library_name_resolves_to_the_system_directory(string name)
    {
        var path = NativeLibraryHardening.SystemPathFor(name);

        Assert.NotNull(path);
        Assert.Equal(Path.GetFullPath(Environment.SystemDirectory), Path.GetDirectoryName(path));
        Assert.EndsWith(".dll", path, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Temp\\evil.dll")]
    [InlineData("..\\evil.dll")]
    [InlineData("sub/evil.dll")]
    public void A_name_with_a_path_part_is_not_remapped(string name)
    {
        Assert.Null(NativeLibraryHardening.SystemPathFor(name));
    }

    [Fact]
    public void A_bare_name_missing_from_the_system_directory_fails_instead_of_falling_back_to_probing()
    {
        var name = "wu-no-such-library-" + Guid.NewGuid().ToString("N");

        Assert.Throws<DllNotFoundException>(() =>
            NativeLibraryHardening.ResolveFromSystemDirectory(name, typeof(App).Assembly, null));
    }

    [Fact]
    public void A_name_with_a_path_part_is_left_to_the_runtime_and_a_system_library_loads()
    {
        Assert.Equal(IntPtr.Zero, NativeLibraryHardening.ResolveFromSystemDirectory(@"C:\Temp\evil.dll", typeof(App).Assembly, null));
        Assert.NotEqual(IntPtr.Zero, NativeLibraryHardening.ResolveFromSystemDirectory("kernel32.dll", typeof(App).Assembly, null));
    }

    [Fact]
    public void The_module_initializer_registered_the_resolver_for_the_app_assembly()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(typeof(App).Module.ModuleHandle);

        // A second registration for the same assembly is refused by the runtime, which proves the first one exists.
        Assert.Throws<InvalidOperationException>(() =>
            NativeLibrary.SetDllImportResolver(typeof(App).Assembly, (_, _, _) => IntPtr.Zero));
    }

    [Fact]
    public void The_module_initializer_set_the_default_search_directories()
    {
        // Touching the type is enough: the initializer ran when the assembly loaded.
        Assert.Equal(0, NativeLibraryHardening.DefaultDirectoriesError);
    }

    [Fact]
    public void Every_own_import_names_a_system_library()
    {
        var allowed = new[] { "advapi32", "amsi", "cfgmgr32", "kernel32", "ntdll", "setupapi", "shell32", "user32", "wintrust" };
        var root = FindSourceRoot();
        var pattern = new System.Text.RegularExpressions.Regex("DllImport\\(\"([^\"]+)\"");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(File.ReadAllText(file)))
                Assert.Contains(Path.GetFileNameWithoutExtension(match.Groups[1].Value).ToLowerInvariant(), allowed);
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "UltimateUpdater")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "UltimateUpdater");
    }
}
