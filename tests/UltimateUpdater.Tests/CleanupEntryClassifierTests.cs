using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

/// <summary>The "by type" grouping is derived, never asked of the user, these lock down
/// the folder-name judgment and the extension fallback it uses when a name says nothing.</summary>
public class CleanupEntryClassifierTests
{
    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\SomeApp\Logs", CleanupEntryType.Logs)]
    [InlineData(@"C:\Users\x\AppData\Local\SomeApp\Cache", CleanupEntryType.Cache)]
    [InlineData(@"C:\Users\x\AppData\Local\D3DSCache", CleanupEntryType.Cache)]
    [InlineData(@"C:\Windows\Minidump", CleanupEntryType.CrashReports)]
    [InlineData(@"C:\Users\x\AppData\Local\CrashDumps", CleanupEntryType.CrashReports)]
    [InlineData(@"C:\Users\x\AppData\Local\Microsoft\Windows\Explorer\thumbcache_256.db", CleanupEntryType.Thumbnails)]
    [InlineData(@"C:\Users\x\AppData\Local\SomeInstaller\Uninstall", CleanupEntryType.InstallLeftovers)]
    public void A_folder_name_that_says_what_it_is_wins_over_any_extension_guess(string path, CleanupEntryType expected) =>
        Assert.Equal(expected, CleanupEntryClassifier.Classify(path, [".bin"]));

    [Fact]
    public void An_unindicative_name_falls_back_to_the_dominant_extension_being_all_logs() =>
        Assert.Equal(CleanupEntryType.Logs, CleanupEntryClassifier.Classify(@"C:\ProgramData\Acme\SomeApp", [".log", ".txt"]));

    [Fact]
    public void An_unindicative_name_falls_back_to_any_crash_dump_extension() =>
        Assert.Equal(CleanupEntryType.CrashReports, CleanupEntryClassifier.Classify(@"C:\ProgramData\Acme\SomeApp", [".dmp", ".ini"]));

    [Fact]
    public void An_unindicative_name_with_mixed_extensions_and_no_dump_is_Other() =>
        Assert.Equal(CleanupEntryType.Other, CleanupEntryClassifier.Classify(@"C:\ProgramData\Acme\SomeApp", [".bin", ".dat"]));

    [Fact]
    public void No_name_hint_and_no_sample_extensions_is_Other() =>
        Assert.Equal(CleanupEntryType.Other, CleanupEntryClassifier.Classify(@"C:\ProgramData\Acme\SomeApp"));
}
