using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

public class TestDataFolderTests
{
    /// <summary>The suite must never read or write the developer's own data folder.</summary>
    [Fact]
    public void The_data_folder_of_the_test_run_is_a_throwaway_temp_folder()
    {
        var expectedParent = Path.GetFullPath(Path.GetTempPath());

        Assert.StartsWith(expectedParent, Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uu-tests-", AppPaths.DataDirectory, StringComparison.Ordinal);
        Assert.StartsWith(AppPaths.DataDirectory, AppPaths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(AppPaths.DataDirectory));
    }
}
