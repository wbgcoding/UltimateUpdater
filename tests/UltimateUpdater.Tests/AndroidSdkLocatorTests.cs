using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Android Studio records the SDK it is actually using, and that record is the only thing that finds an SDK
/// which lives nowhere near the IDE or the environment variables. A real observed case: ANDROID_HOME
/// points at C:\Android while the real SDK is D:\AndroidStudio\SDK, so following the variable alone pointed
/// sdkmanager at the wrong tree and the row reported that nothing needed updating.
/// The shape below is taken from a real jdk.table.xml.
/// </summary>
public class AndroidSdkLocatorTests
{
    private const string RealShape = """
        <application>
          <component name="ProjectJdkTable">
            <jdk version="2">
              <name value="jbr-21" />
              <type value="JavaSDK" />
              <homePath value="C:/Android/jbr" />
            </jdk>
            <jdk version="2">
              <name value="Android API 34 Platform" />
              <type value="Android SDK" />
              <homePath value="D:/AndroidStudio/SDK" />
              <version value="java version &quot;21.0.0&quot;" />
            </jdk>
            <jdk version="2">
              <name value="Android API 35 Platform" />
              <type value="Android SDK" />
              <homePath value="D:/AndroidStudio/SDK" />
            </jdk>
          </component>
        </application>
        """;

    [Fact]
    public void The_sdk_studio_uses_is_read_out_of_its_own_configuration()
    {
        var paths = AndroidSdkLocator.ParseStudioSdkPaths(RealShape);

        Assert.Equal([@"D:\AndroidStudio\SDK"], paths);
    }

    /// <summary>A Java SDK entry sits in the same file and must not be mistaken for the Android one.</summary>
    [Fact]
    public void A_java_sdk_entry_is_not_an_android_sdk() =>
        Assert.DoesNotContain(@"C:\Android\jbr", AndroidSdkLocator.ParseStudioSdkPaths(RealShape));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<application><component/></application>")]
    public void Anything_unusable_yields_nothing(string? xml) =>
        Assert.Empty(AndroidSdkLocator.ParseStudioSdkPaths(xml));

    /// <summary>A folder that is not an SDK must never be handed to sdkmanager as its root.</summary>
    [Fact]
    public void A_directory_without_any_sdk_marker_is_not_an_sdk_root()
    {
        var empty = Directory.CreateTempSubdirectory("wu-sdk-test-").FullName;

        Assert.False(AndroidSdkLocator.IsSdkRoot(empty));
        Assert.False(AndroidSdkLocator.IsSdkRoot(Path.Combine(empty, "does-not-exist")));
        Assert.False(AndroidSdkLocator.IsSdkRoot(null));

        Directory.CreateDirectory(Path.Combine(empty, "platform-tools"));
        Assert.True(AndroidSdkLocator.IsSdkRoot(empty));
    }

    /// <summary>A minimal SDK holds only the command-line tools, and that still counts.</summary>
    [Fact]
    public void Command_line_tools_alone_are_enough_to_count_as_an_sdk()
    {
        var root = Directory.CreateTempSubdirectory("wu-sdk-test-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "cmdline-tools"));

        Assert.True(AndroidSdkLocator.IsSdkRoot(root));
    }

    private static string CmdlineToolsBin(string root, string version)
    {
        var bin = Path.Combine(root, "cmdline-tools", version, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "sdkmanager.bat"), "rem");
        return bin;
    }

    /// <summary>The newest command-line tools ship `android.exe` beside `sdkmanager.bat` (which only
    /// forwards to it); the CLI is preferred when it is there.</summary>
    [Fact]
    public void The_android_cli_beside_the_chosen_sdkmanager_is_found()
    {
        var root = Directory.CreateTempSubdirectory("wu-sdk-test-").FullName;
        CmdlineToolsBin(root, "9.0");
        var bin = CmdlineToolsBin(root, "23.0");
        File.WriteAllText(Path.Combine(bin, "android.exe"), "x");

        var sdkManager = AndroidSdkLocator.SdkManagerIn(root);

        Assert.Equal(Path.Combine(bin, "sdkmanager.bat"), sdkManager);
        Assert.Equal(Path.Combine(bin, "android.exe"), AndroidSdkLocator.AndroidCliNextTo(sdkManager));
    }

    /// <summary>Older command-line tools have no CLI: sdkmanager stays the executable.</summary>
    [Fact]
    public void Without_an_android_cli_the_sdkmanager_stays_in_charge()
    {
        var root = Directory.CreateTempSubdirectory("wu-sdk-test-").FullName;
        CmdlineToolsBin(root, "9.0");

        var sdkManager = AndroidSdkLocator.SdkManagerIn(root);

        Assert.NotNull(sdkManager);
        Assert.Null(AndroidSdkLocator.AndroidCliNextTo(sdkManager));
        Assert.Null(AndroidSdkLocator.AndroidCliNextTo(null));
    }

    /// <summary>The CLI of an OLDER tools folder must not be picked just because it has one: the CLI beside
    /// the chosen (newest) sdkmanager is the only candidate.</summary>
    [Fact]
    public void The_android_cli_of_an_older_tools_folder_is_ignored()
    {
        var root = Directory.CreateTempSubdirectory("wu-sdk-test-").FullName;
        File.WriteAllText(Path.Combine(CmdlineToolsBin(root, "9.0"), "android.exe"), "x");
        CmdlineToolsBin(root, "23.0");

        Assert.Null(AndroidSdkLocator.AndroidCliNextTo(AndroidSdkLocator.SdkManagerIn(root)));
    }
}
