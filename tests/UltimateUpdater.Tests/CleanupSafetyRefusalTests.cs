using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The delete gates fail closed. These pin which rule refuses which input (the support log names that rule),
/// so a refusal cannot quietly turn into a different rule or into an allow when the checks are reordered.
/// </summary>
public sealed class CleanupSafetyRefusalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "uu-safety-refusal-" + Guid.NewGuid().ToString("N"));

    public CleanupSafetyRefusalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CleanupSafety.OwnPathsOverrideForTest.Value = null;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string DriveRoot => Path.GetPathRoot(Path.GetTempPath())!;

    // ---- IsSafeToEmpty

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_folder_is_refused_as_an_empty_path(string path)
    {
        Assert.False(CleanupSafety.IsSafeToEmpty(path, out var reason));
        Assert.Equal("empty path", reason);
    }

    [Fact]
    public void A_folder_path_with_an_invalid_character_is_refused_as_unusable()
    {
        Assert.False(CleanupSafety.IsSafeToEmpty("C:\\Temp\\bad\0name", out var reason));
        Assert.Equal("unusable path", reason);
    }

    [Fact]
    public void A_drive_root_is_refused_by_name()
    {
        Assert.False(CleanupSafety.IsSafeToEmpty(DriveRoot, out var reason));
        Assert.Equal("drive root", reason);
    }

    [Theory]
    [InlineData("Downloads")]
    [InlineData("DOCUMENTS")]
    [InlineData("Desktop")]
    [InlineData("OneDrive")]
    [InlineData("Saved Games")]
    public void A_folder_named_like_user_data_is_refused_even_when_it_sits_under_temp(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);

        Assert.False(CleanupSafety.IsSafeToEmpty(path, out var reason));
        Assert.Equal($"named like user data ({name})", reason);
    }

    [Fact]
    public void A_folder_that_does_not_look_like_temp_or_cache_is_refused()
    {
        var path = Path.Combine(DriveRoot, "uu-some-user-folder-" + Guid.NewGuid().ToString("N"), "Stuff");

        Assert.False(CleanupSafety.IsSafeToEmpty(path, out var reason));
        Assert.Equal("not a temp or cache location", reason);
    }

    [Fact]
    public void A_missing_folder_below_temp_is_refused_as_not_existing()
    {
        Assert.False(CleanupSafety.IsSafeToEmpty(Path.Combine(_dir, "missing"), out var reason));
        Assert.Equal("does not exist", reason);
    }

    [Fact]
    public void A_folder_the_program_owns_is_refused_before_anything_else_is_looked_at()
    {
        var own = Path.Combine(_dir, "program");
        Directory.CreateDirectory(own);
        CleanupSafety.OwnPathsOverrideForTest.Value = [own];

        Assert.False(CleanupSafety.IsSafeToEmpty(own, out var reason));
        Assert.Contains("own folder", reason);
    }

    [Fact]
    public void An_ordinary_existing_temp_subfolder_passes_with_no_reason()
    {
        Assert.True(CleanupSafety.IsSafeToEmpty(_dir, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    // ---- IsSafeToDeleteFile

    [Fact]
    public void A_blank_file_path_is_refused_as_an_empty_path()
    {
        Assert.False(CleanupSafety.IsSafeToDeleteFile(" ", out var reason));
        Assert.Equal("empty path", reason);
    }

    [Theory]
    [InlineData("MEMORY.DMP")]                           // relative: ShellExecute-style search order must never apply
    [InlineData("..\\MEMORY.DMP")]
    [InlineData("\\\\server\\share\\MEMORY.DMP")]       // network path
    [InlineData("//server/share/MEMORY.DMP")]
    [InlineData("C:\\Windows\\bad\0name\\MEMORY.DMP")]
    public void A_file_that_is_not_a_fully_qualified_local_path_is_refused(string path)
    {
        Assert.False(CleanupSafety.IsSafeToDeleteFile(path, out var reason));
        Assert.Equal("not a local path or leads through a link", reason);
    }

    [Fact]
    public void A_drive_root_is_refused_as_a_file()
    {
        Assert.False(CleanupSafety.IsSafeToDeleteFile(DriveRoot, out var reason));
        Assert.Equal("drive root", reason);
    }

    [Fact]
    public void Only_the_catalogued_file_kinds_may_be_named()
    {
        var notes = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notes, "x");

        Assert.False(CleanupSafety.IsSafeToDeleteFile(notes, out var reason));
        Assert.Equal("not a kind of file the catalogue may name", reason);
    }

    [Fact]
    public void A_dump_that_is_not_there_is_refused_as_not_existing()
    {
        Assert.False(CleanupSafety.IsSafeToDeleteFile(Path.Combine(_dir, "MEMORY.DMP"), out var reason));
        Assert.Equal("does not exist", reason);
    }

    [Fact]
    public void A_folder_named_like_a_dump_is_not_a_file_and_is_refused()
    {
        var folder = Path.Combine(_dir, "MEMORY.DMP");
        Directory.CreateDirectory(folder);

        Assert.False(CleanupSafety.IsSafeToDeleteFile(folder, out var reason));
        Assert.Equal("does not exist", reason);
    }

    [Fact]
    public void A_dump_inside_the_programs_own_folder_is_refused()
    {
        var own = Path.Combine(_dir, "program");
        Directory.CreateDirectory(own);
        var dump = Path.Combine(own, "MEMORY.DMP");
        File.WriteAllBytes(dump, new byte[4]);
        CleanupSafety.OwnPathsOverrideForTest.Value = [own];

        Assert.False(CleanupSafety.IsSafeToDeleteFile(dump, out var reason));
        Assert.Contains("own folder", reason);
    }

    [Fact]
    public void A_real_dump_file_passes_with_no_reason()
    {
        var dump = Path.Combine(_dir, "MEMORY.DMP");
        File.WriteAllBytes(dump, new byte[4]);

        Assert.True(CleanupSafety.IsSafeToDeleteFile(dump, out var reason));
        Assert.Equal(string.Empty, reason);
    }

    // ---- own-path and local-path helpers

    [Theory]
    [InlineData("C:\\Temp\\bad\0name")]
    [InlineData("")]
    public void An_unusable_path_is_neither_an_own_path_nor_one_that_holds_one(string path)
    {
        Assert.False(CleanupSafety.IsOwnPath(path));
        Assert.False(CleanupSafety.ContainsOwnPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative\\file.txt")]
    [InlineData("\\\\server\\share\\f.txt")]
    [InlineData("//server/share/f.txt")]
    [InlineData("C:\\bad\0name")]
    public void A_path_that_is_not_a_local_fully_qualified_one_is_not_local(string? path) =>
        Assert.False(CleanupSafety.IsLocalPathWithoutLinks(path));

    [Fact]
    public void A_plain_path_on_the_temp_drive_is_local_without_links() =>
        Assert.True(CleanupSafety.IsLocalPathWithoutLinks(Path.Combine(_dir, "anything.txt")));

    [Fact]
    public void A_path_below_a_junction_is_found_to_lead_through_a_link()
    {
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(_dir, "link");
        if (!TryMakeJunction(link, target))
            return; // junction creation unavailable on this machine: nothing to prove here

        try
        {
            Assert.True(CleanupSafety.AncestryLeadsThroughLink(Path.Combine(link, "child.txt")));
            Assert.False(CleanupSafety.IsLocalPathWithoutLinks(Path.Combine(link, "child.txt")));
            Assert.False(CleanupSafety.AncestryLeadsThroughLink(Path.Combine(target, "child.txt")));
        }
        finally
        {
            Directory.Delete(link); // removes the junction itself, never what it points at
        }
    }

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process!.WaitForExit(15_000);
            return Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
