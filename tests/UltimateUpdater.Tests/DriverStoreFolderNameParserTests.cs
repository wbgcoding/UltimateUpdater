using UltimateUpdater.Services.Cleanup;

namespace UltimateUpdater.Tests;

public class DriverStoreFolderNameParserTests
{
    [Theory]
    [InlineData("nvhda.inf_amd64_1a2b3c4d5e6f7890", "nvhda.inf")]
    [InlineData("prnms003.inf_x86_deadbeefcafef00d", "prnms003.inf")]
    [InlineData("some.driver.name.inf_arm64_0123456789abcdef", "some.driver.name.inf")]
    public void ExtractOriginalName_strips_the_architecture_and_hash_suffix(string folderName, string expected) =>
        Assert.Equal(expected, DriverStoreFolderNameParser.ExtractOriginalName(folderName));

    [Fact]
    public void An_unrecognised_shape_is_returned_unchanged_rather_than_guessed()
    {
        const string folderName = "not_a_known_shape";

        Assert.Equal(folderName, DriverStoreFolderNameParser.ExtractOriginalName(folderName));
    }
}
