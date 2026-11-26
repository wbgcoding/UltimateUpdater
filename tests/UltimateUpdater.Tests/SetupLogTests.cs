using System.IO;
using UltimateUpdater.Services.SelfUpdate;

namespace UltimateUpdater.Tests;

public class SetupLogTests
{
    private const string Logs = @"C:\Users\Someone\AppData\Local\UltimateUpdater\logs";

    [Fact]
    public void The_setup_the_app_starts_writes_its_log_to_the_given_path()
    {
        var path = SetupLog.PathFor(Logs, new DateTime(2026, 3, 4, 5, 6, 7));
        var info = SelfUpdateService.BuildSetupStartInfo(@"C:\Temp\setup.exe", machineScope: false, path);

        Assert.Equal(@"C:\Users\Someone\AppData\Local\UltimateUpdater\logs\setup\setup-20260304-050607.log", path);
        Assert.EndsWith($" /LOG=\"{path}\"", info.Arguments);
        Assert.Contains("/SILENT", info.Arguments);
    }

    [Fact]
    public void Without_a_log_path_the_setup_arguments_carry_no_log_switch()
    {
        var info = SelfUpdateService.BuildSetupStartInfo(@"C:\Temp\setup.exe", machineScope: false);

        Assert.DoesNotContain("/LOG", info.Arguments);
    }

    [Theory]
    [InlineData("setup.log")]
    [InlineData(@"C:\logs\a""b.log")]
    [InlineData(@"C:\logs\a"" /SILENT=""x.log")]
    [InlineData("C:\\logs\\a\nb.log")]
    [InlineData(@"C:\logs\a*.log")]
    [InlineData(@"C:\logs\a|b.log")]
    [InlineData(@"C:\logs\")]
    [InlineData("")]
    [InlineData(null)]
    public void A_path_that_cannot_be_quoted_safely_gets_no_log_switch(string? path)
    {
        Assert.Null(SetupLog.Argument(path));
        Assert.DoesNotContain("/LOG", SelfUpdateService.BuildSetupStartInfo(@"C:\Temp\setup.exe", false, path).Arguments);
    }

    [Fact]
    public void A_path_with_spaces_stays_one_quoted_argument()
    {
        Assert.Equal(@"/LOG=""C:\Users\A B\logs\setup\setup-1.log""", SetupLog.Argument(@"C:\Users\A B\logs\setup\setup-1.log"));
    }

    [Fact]
    public void Pruning_keeps_only_the_newest_install_logs_and_leaves_other_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "uu-setuplog-" + Guid.NewGuid().ToString("N"));
        var folder = SetupLog.FolderFor(root);
        Directory.CreateDirectory(folder);
        try
        {
            for (var day = 1; day <= 8; day++)
                File.WriteAllText(Path.Combine(folder, $"setup-202601{day:00}-120000.log"), "x");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "keep");

            SetupLog.Prune(root, keep: 3);

            var left = Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Order().ToArray();
            Assert.Equal(
                ["notes.txt", "setup-20260106-120000.log", "setup-20260107-120000.log", "setup-20260108-120000.log"], left);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pruning_a_missing_folder_does_nothing()
    {
        SetupLog.Prune(Path.Combine(Path.GetTempPath(), "uu-setuplog-missing-" + Guid.NewGuid().ToString("N")));
    }
}
