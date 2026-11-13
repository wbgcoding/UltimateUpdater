using System.Diagnostics;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// Emptying the recycle bin deletes through a locked bin folder and verified handles. The path gate is a
/// snapshot, so a bin folder or an item swapped for a junction right after it must delete nothing outside.
/// A real bin only exists at a drive root, so these tests use a fake one and a gate that accepts it.
/// </summary>
public class RecycledItemDeleteTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";

    private readonly string _root = Directory.CreateTempSubdirectory("wu-recycled-delete-").FullName;
    private readonly CleanupService _service = new(new LoggingService());

    public void Dispose()
    {
        CleanupSafety.BeforeLockOpenForTest.Value = null;
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                     .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList())
        {
            Directory.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string BinFolder => Path.Combine(_root, "bin", Sid);

    private static bool AcceptAll(string path, string? sid) => true;

    private static CleanupEntry EntryFor(string path) =>
        new(path, "item", 1, 1, DateTimeOffset.Now, DateTimeOffset.Now, CleanupEntryType.Other, @"C:\original\item");

    private bool Delete(string path) => _service.DeleteRecycledItem(EntryFor(path), Sid, AcceptAll);

    private static void CreateJunction(string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(mklink.WaitForExit(60_000), "mklink did not finish");
        Assert.Equal(0, mklink.ExitCode);
    }

    private string MakeOutside()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "$R1"), "canary");
        File.WriteAllText(Path.Combine(outside, "$I1"), "canary");
        Directory.CreateDirectory(Path.Combine(outside, "$R2"));
        File.WriteAllText(Path.Combine(outside, "$R2", "inner.txt"), "canary");
        return outside;
    }

    [Fact]
    public void A_recycled_file_and_its_index_are_deleted_even_when_read_only()
    {
        Directory.CreateDirectory(BinFolder);
        var content = Path.Combine(BinFolder, "$R1.txt");
        var index = Path.Combine(BinFolder, "$I1.txt");
        File.WriteAllText(content, "x");
        File.WriteAllText(index, "x");
        File.SetAttributes(content, FileAttributes.ReadOnly);

        Assert.True(Delete(content));

        Assert.False(File.Exists(content));
        Assert.False(File.Exists(index));
    }

    [Fact]
    public void A_recycled_folder_with_read_only_content_is_deleted_with_its_index()
    {
        var content = Path.Combine(BinFolder, "$R2");
        Directory.CreateDirectory(Path.Combine(content, "sub"));
        var locked = Path.Combine(content, "sub", "pack.bin");
        File.WriteAllText(locked, "x");
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(BinFolder, "$I2"), "x");

        Assert.True(Delete(content));

        Assert.False(Directory.Exists(content));
        Assert.False(File.Exists(Path.Combine(BinFolder, "$I2")));
    }

    [Fact]
    public void A_junction_inside_a_recycled_folder_is_removed_and_its_target_survives()
    {
        var outside = MakeOutside();
        var content = Path.Combine(BinFolder, "$R3");
        Directory.CreateDirectory(content);
        CreateJunction(Path.Combine(content, "link"), outside);

        Assert.True(Delete(content));

        Assert.False(Directory.Exists(content));
        Assert.True(File.Exists(Path.Combine(outside, "$R1")));
        Assert.True(File.Exists(Path.Combine(outside, "$R2", "inner.txt")));
    }

    [Fact]
    public void A_bin_folder_swapped_for_a_junction_after_the_gate_deletes_nothing_outside()
    {
        var outside = MakeOutside();
        Directory.CreateDirectory(BinFolder);
        File.WriteAllText(Path.Combine(BinFolder, "$R1"), "inside");
        var content = Path.Combine(BinFolder, "$R1");

        CleanupSafety.BeforeLockOpenForTest.Value = path =>
        {
            if (new DirectoryInfo(path).LinkTarget is not null || !string.Equals(path, BinFolder, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.Delete(path, recursive: true);
            CreateJunction(path, outside);
        };

        Assert.False(Delete(content));

        Assert.True(File.Exists(Path.Combine(outside, "$R1")), "a file outside the bin was deleted through a swapped bin folder");
        Assert.True(File.Exists(Path.Combine(outside, "$I1")));
    }

    [Fact]
    public void A_folder_item_swapped_for_a_junction_after_the_gate_deletes_nothing_outside()
    {
        var outside = MakeOutside();
        var content = Path.Combine(BinFolder, "$R2");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "inner.txt"), "inside");

        CleanupSafety.BeforeLockOpenForTest.Value = path =>
        {
            if (new DirectoryInfo(path).LinkTarget is not null || !string.Equals(path, content, StringComparison.OrdinalIgnoreCase))
                return;

            Directory.Delete(path, recursive: true);
            CreateJunction(path, Path.Combine(outside, "$R2"));
        };

        Assert.False(Delete(content));

        Assert.True(File.Exists(Path.Combine(outside, "$R2", "inner.txt")), "content outside the bin was deleted through a swapped item");
    }

    [Fact]
    public void An_item_whose_bin_folder_is_missing_is_refused()
    {
        Assert.False(Delete(Path.Combine(BinFolder, "$R9")));
    }

    [Fact]
    public void The_gate_still_refuses_a_path_outside_the_own_bin()
    {
        Directory.CreateDirectory(BinFolder);
        var content = Path.Combine(BinFolder, "$R1");
        File.WriteAllText(content, "x");

        Assert.False(_service.DeleteRecycledItem(EntryFor(content), Sid, (_, _) => false));
        Assert.True(File.Exists(content));
    }

    [Fact]
    public void A_recycled_folder_with_a_read_only_root_and_a_desktop_ini_is_deleted()
    {
        var content = Path.Combine(BinFolder, "$R4");
        Directory.CreateDirectory(content);
        var ini = Path.Combine(content, "desktop.ini");
        File.WriteAllText(ini, "[.ShellClassInfo]");
        File.SetAttributes(ini, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(content, FileAttributes.Directory | FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(BinFolder, "$I4"), "x");

        Assert.True(Delete(content));

        Assert.False(Directory.Exists(content));
        Assert.False(File.Exists(Path.Combine(BinFolder, "$I4")));
    }

    [Fact]
    public void A_read_only_recycled_folder_that_still_holds_something_is_kept()
    {
        var content = Path.Combine(BinFolder, "$R5");
        Directory.CreateDirectory(content);
        var held = Path.Combine(content, "held.txt");
        File.WriteAllText(held, "x");
        File.SetAttributes(content, FileAttributes.Directory | FileAttributes.ReadOnly);

        bool deleted;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            deleted = Delete(content);
        }

        Assert.False(deleted);
        Assert.True(File.Exists(held));
        File.SetAttributes(content, FileAttributes.Directory);
    }

    [Fact]
    public void A_partly_deleted_recycled_folder_reports_what_it_freed()
    {
        var content = Path.Combine(BinFolder, "$R7");
        Directory.CreateDirectory(content);
        var gone = Path.Combine(content, "gone.bin");
        var held = Path.Combine(content, "held.txt");
        File.WriteAllBytes(gone, new byte[1000]);
        File.WriteAllText(held, "x");

        bool deleted;
        long partialFreed;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            deleted = _service.DeleteRecycledItem(EntryFor(content), Sid, AcceptAll, out partialFreed);
        }

        Assert.False(deleted);
        // The delete reports the size on disk (a whole cluster), so only "something was freed" is stable.
        Assert.True(partialFreed >= 1000);
        Assert.False(File.Exists(gone));
        Assert.True(File.Exists(held));
    }

    [Fact]
    public void A_junction_as_the_recycled_item_is_only_unlinked_and_its_target_survives()
    {
        var outside = MakeOutside();
        var content = Path.Combine(BinFolder, "$R6");
        Directory.CreateDirectory(BinFolder);
        CreateJunction(content, outside);

        Delete(content);

        Assert.False(Directory.Exists(content));
        Assert.True(File.Exists(Path.Combine(outside, "$R1")));
        Assert.True(File.Exists(Path.Combine(outside, "$R2", "inner.txt")));
    }
}
