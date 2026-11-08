using System.Diagnostics;
using System.IO;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>
/// The shader and developer cache locations of the catalogue, and the one folder (the uv cache) that is
/// emptied by running the tool that owns it instead of deleting its files one by one.
/// </summary>
public class CleanupDeveloperCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wu-devcache-test-" + Guid.NewGuid());
    private readonly string _local;
    private readonly string _profile;
    private readonly string _localLow;

    public CleanupDeveloperCacheTests()
    {
        _local = Path.Combine(_root, "Local");
        _profile = Path.Combine(_root, "profile");
        _localLow = Path.Combine(_root, "LocalLow");
        Directory.CreateDirectory(_local);
        Directory.CreateDirectory(_profile);
        Directory.CreateDirectory(_localLow);
    }

    public void Dispose()
    {
        TempTreeCleanup.Delete(_root);
    }

    private static string Folder(params string[] parts)
    {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string AgedFile(string folder, string name, TimeSpan age, int bytes = 1000)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow - age);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    // ---- shader caches ----

    [Fact]
    public void The_four_new_shader_cache_folders_are_listed_when_they_exist()
    {
        var wanted = new[]
        {
            Folder(_localLow, "NVIDIA", "PerDriverVersion", "DXCache"),
            Folder(_localLow, "NVIDIA", "PerDriverVersion", "GLCache"),
            Folder(_local, "AMD", "VkCache"),
            Folder(_local, "AMD", "GLCache"),
        };

        var listed = CleanupCatalog.ShaderCacheDirectories(_local, _localLow);

        foreach (var path in wanted)
            Assert.Contains(path, listed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Folders_that_do_not_exist_are_not_listed()
    {
        Assert.Empty(CleanupCatalog.ShaderCacheDirectories(_local, _localLow));
    }

    [Fact]
    public void When_the_known_folder_lookup_fails_no_LocalLow_path_is_listed_and_the_Local_ones_stay()
    {
        var localLowFolder = Folder(_localLow, "NVIDIA", "PerDriverVersion", "DXCache");
        var vk = Folder(_local, "AMD", "VkCache");

        var listed = CleanupCatalog.ShaderCacheDirectories(_local, localLow: null);

        Assert.DoesNotContain(localLowFolder, listed, StringComparer.OrdinalIgnoreCase);
        Assert.Equal([vk], listed);
    }

    [Fact]
    public void The_known_folder_lookup_names_the_LocalLow_folder_of_this_user()
    {
        var localLow = KnownFolderPaths.LocalAppDataLow();

        Assert.NotNull(localLow);
        Assert.True(Path.IsPathFullyQualified(localLow));
        Assert.Equal("LocalLow", Path.GetFileName(localLow));
    }

    [Fact]
    public void The_shader_cache_row_passes_the_safety_gate_for_every_existing_folder()
    {
        foreach (var directory in CleanupCatalog.Build().First(t => t.Id == "shader-cache").Directories)
            Assert.True(CleanupSafety.IsSafeToEmpty(directory, out var reason), $"{directory}: {reason}");
    }

    [Theory]
    [InlineData("DXCache")]
    [InlineData("GLCache")]
    public void The_LocalLow_shader_folders_pass_the_safety_gate(string leaf)
    {
        var path = Folder(_localLow, "NVIDIA", "PerDriverVersion", leaf);

        Assert.True(CleanupSafety.IsSafeToEmpty(path, out var reason), reason);
    }

    // ---- developer caches ----

    [Fact]
    public void The_go_cargo_and_pnpm_caches_and_the_uv_cache_are_listed_when_present()
    {
        var goBuild = Folder(_local, "go-build");
        var cargo = Folder(_profile, ".cargo", "registry", "cache");
        var pnpm = Folder(_local, "pnpm-cache");
        var uv = Folder(_local, "uv", "cache");

        var listed = CleanupCatalog.PackageCacheDirectories(_local, _profile, Path.Combine(_root, "tmp"));

        foreach (var path in new[] { goBuild, cargo, pnpm, uv })
            Assert.Contains(path, listed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cargo_sources_the_registry_folder_and_the_pnpm_store_are_never_listed()
    {
        Folder(_profile, ".cargo", "registry", "cache");
        var src = Folder(_profile, ".cargo", "registry", "src");
        var index = Folder(_profile, ".cargo", "registry", "index");
        var store = Folder(_local, "pnpm", "store");
        var packages = Folder(_profile, ".nuget", "packages");
        var yarnBerry = Folder(_local, "Yarn", "Berry", "cache");

        var listed = CleanupCatalog.PackageCacheDirectories(_local, _profile, Path.Combine(_root, "tmp"));

        Assert.DoesNotContain(src, listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(index, listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(Path.Combine(_profile, ".cargo", "registry"), listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(store, listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(packages, listed, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(yarnBerry, listed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_new_developer_cache_names_pass_the_safety_gate()
    {
        foreach (var path in new[]
                 {
                     Folder(_local, "go-build"), Folder(_profile, ".cargo", "registry", "cache"),
                     Folder(_local, "pnpm-cache"), Folder(_local, "uv", "cache"),
                 })
        {
            Assert.True(CleanupSafety.IsSafeToEmpty(path, out var reason), $"{path}: {reason}");
        }
    }

    [Fact]
    public void Environment_variables_do_not_move_the_listed_locations()
    {
        var elsewhere = Folder(_root, "elsewhere", "uv-cache");
        var previous = Environment.GetEnvironmentVariable("UV_CACHE_DIR");
        Environment.SetEnvironmentVariable("UV_CACHE_DIR", elsewhere);
        try
        {
            var listed = CleanupCatalog.PackageCacheDirectories(_local, _profile, Path.Combine(_root, "tmp"));
            var commands = CleanupCatalog.PackageCacheCleanCommands(_local, _profile);

            Assert.DoesNotContain(elsewhere, listed, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(commands);
        }
        finally
        {
            Environment.SetEnvironmentVariable("UV_CACHE_DIR", previous);
        }
    }

    [Fact]
    public void Only_the_uv_folder_carries_a_clean_command_with_the_folder_passed_explicitly()
    {
        var uv = Folder(_local, "uv", "cache");
        Folder(_local, "go-build");

        var commands = CleanupCatalog.PackageCacheCleanCommands(_local, _profile);

        var command = Assert.Single(commands);
        Assert.Equal(Path.GetFullPath(uv), command.Key, ignoreCase: true);
        Assert.Equal("uv", command.Value.ToolName);
        Assert.Equal(TimeSpan.FromSeconds(120), command.Value.Timeout);
        Assert.Equal(["cache", "clean", "--cache-dir", DirectoryCleanCommand.FolderToken, "--no-config", "--no-progress"],
            command.Value.Arguments);
        Assert.Equal(Path.Combine(_profile, ".local", "bin", "uv.exe"), command.Value.FallbackPath);
    }

    [Fact]
    public void The_catalogue_row_has_no_command_when_the_uv_folder_is_absent()
    {
        Assert.Empty(CleanupCatalog.PackageCacheCleanCommands(_local, _profile));
        Assert.False(CleanupCatalog.Build().First(t => t.Id == "package-manager-cache").NeedsElevation);
    }

    // ---- the uv cache is cleaned by uv, never file by file ----

    private sealed class FakeTool
    {
        public readonly List<(string Exe, string[] Args)> Calls = [];
        public Func<string, string[], ProcessResult> Behaviour = (_, _) => new ProcessResult(0, "", "");

        public Task<ProcessResult> Run(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken token)
        {
            Calls.Add((exe, [.. args]));
            return Task.FromResult(Behaviour(exe, [.. args]));
        }
    }

    private CleanupService ServiceWith(FakeTool tool, string? resolved)
    {
        var service = new CleanupService(new LoggingService());
        service.TrustedToolResolver = _ => resolved;
        service.ToolRunner = tool.Run;
        return service;
    }

    private CleanupTarget UvTarget(string uvFolder, string? other = null, bool needsElevation = false)
    {
        var command = new DirectoryCleanCommand(
            "uv", Path.Combine(_profile, ".local", "bin", "uv.exe"),
            ["cache", "clean", "--cache-dir", DirectoryCleanCommand.FolderToken], "uv cache clean", TimeSpan.FromSeconds(120));
        return new CleanupTarget("test-pkg", "x", "y", CleanupKind.Directories, DefaultEnabled: true,
            RiskLevel: CleanupRiskLevel.Safe, ConsequenceKey: "x", NeedsElevation: needsElevation)
        {
            Directories = other is null ? [uvFolder] : [uvFolder, other],
            MinFileAge = CleanupCatalog.CacheMinFileAge,
            CleanCommands = new Dictionary<string, DirectoryCleanCommand>(StringComparer.OrdinalIgnoreCase) { [uvFolder] = command },
        };
    }

    [Fact]
    public async Task The_uv_folder_is_measured_like_the_others()
    {
        var uv = Folder(_local, "uv", "cache");
        AgedFile(uv, "a.bin", TimeSpan.FromDays(3), 500);

        var scan = await ServiceWith(new FakeTool(), null).ScanAsync(UvTarget(uv), CancellationToken.None);

        Assert.Equal(1, scan.Files);
        Assert.Equal(500, scan.Bytes);
    }

    [Fact]
    public async Task The_command_runs_with_the_resolved_exe_and_the_measured_folder_and_the_freed_bytes_are_what_left()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3), 700);
        var tool = new FakeTool
        {
            Behaviour = (_, args) =>
            {
                foreach (var entry in Directory.GetFiles(args[3]))
                    File.Delete(entry);
                return new ProcessResult(0, "", "");
            },
        };
        var service = ServiceWith(tool, @"C:\tools\uv.exe");

        var outcome = await service.CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        var call = Assert.Single(tool.Calls);
        Assert.Equal(@"C:\tools\uv.exe", call.Exe);
        Assert.Equal(["cache", "clean", "--cache-dir", uv], call.Args);
        Assert.Equal(700, outcome.FreedBytes);
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task A_failing_command_leaves_the_folder_untouched_and_counted_as_not_freed()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3), 700);
        var tool = new FakeTool { Behaviour = (_, _) => new ProcessResult(2, "", "locked") };

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Single(tool.Calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(0, outcome.DeletedFiles);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.Equal(700, outcome.SkippedBytes);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_timed_out_command_counts_as_not_freed()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool { Behaviour = (_, _) => new ProcessResult(-1, "", "") { TimedOut = true } };

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_command_that_cannot_start_counts_as_not_freed()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool { Behaviour = (_, _) => throw new InvalidOperationException("no") };

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Without_uv_the_folder_is_skipped_counted_as_not_freed_and_its_files_stay()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool();

        var outcome = await ServiceWith(tool, resolved: null)
            .CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Empty(tool.Calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Equal(1, outcome.SkippedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_fixed_fallback_uv_is_used_when_the_trusted_lookup_finds_none()
    {
        var uv = Folder(_local, "uv", "cache");
        AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var fallback = Path.Combine(_profile, ".local", "bin", "uv.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fallback)!);
        File.WriteAllText(fallback, "stub");
        var tool = new FakeTool();

        await ServiceWith(tool, resolved: null).CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(fallback), Assert.Single(tool.Calls).Exe, ignoreCase: true);
    }

    [Fact]
    public async Task A_fallback_uv_reached_through_a_junction_is_not_run()
    {
        var uv = Folder(_local, "uv", "cache");
        AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var realBin = Folder(_root, "realbin");
        File.WriteAllText(Path.Combine(realBin, "uv.exe"), "stub");
        Folder(_profile, ".local");
        RunCmd($"mklink /J \"{Path.Combine(_profile, ".local", "bin")}\" \"{realBin}\"");
        var tool = new FakeTool();

        await ServiceWith(tool, resolved: null).CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task A_file_younger_than_the_age_rule_keeps_the_command_from_running()
    {
        var uv = Folder(_local, "uv", "cache");
        AgedFile(uv, "old.bin", TimeSpan.FromDays(3));
        var fresh = AgedFile(uv, "fresh.bin", TimeSpan.FromMinutes(5));
        var tool = new FakeTool();

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv), progress: null, CancellationToken.None);

        Assert.Empty(tool.Calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task An_unticked_row_inside_the_folder_keeps_the_command_from_running()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(Path.Combine(uv, "wheels"), "a.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool();

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe").CleanAsync(
            UvTarget(uv), progress: null, CancellationToken.None, excludedEntryPaths: [Path.Combine(uv, "wheels")]);

        Assert.Empty(tool.Calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_row_that_needs_elevation_never_runs_the_command()
    {
        var uv = Folder(_local, "uv", "cache");
        var file = AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool();

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv, needsElevation: true), progress: null, CancellationToken.None);

        Assert.Empty(tool.Calls);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_file_deleter_never_touches_the_uv_folder_but_still_cleans_the_other_folders_of_the_row()
    {
        var uv = Folder(_local, "uv", "cache");
        var uvFile = AgedFile(uv, "a.bin", TimeSpan.FromDays(3));
        var goBuild = Folder(_local, "go-build");
        var goFile = AgedFile(goBuild, "b.bin", TimeSpan.FromDays(3));
        var tool = new FakeTool { Behaviour = (_, _) => new ProcessResult(1, "", "") };

        var outcome = await ServiceWith(tool, @"C:\tools\uv.exe")
            .CleanAsync(UvTarget(uv, goBuild), progress: null, CancellationToken.None);

        Assert.True(File.Exists(uvFile));
        Assert.False(File.Exists(goFile));
        Assert.Equal(1, outcome.DeletedFiles);
        Assert.Equal(1, outcome.SkippedFiles);
    }

    private static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(60_000), "cmd did not finish");
        Assert.Equal(0, process.ExitCode);
    }
}
