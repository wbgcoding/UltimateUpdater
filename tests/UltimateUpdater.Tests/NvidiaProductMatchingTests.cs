using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Matching a detected adapter name against a saved copy of NVIDIA's own product list, so desktop and
/// notebook parts that share a model number resolve to different ids.
/// </summary>
public class NvidiaProductMatchingTests
{
    private static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UltimateUpdater.slnx")))
            directory = directory.Parent;

        Assert.True(directory is not null, "the repository root is not above the test binaries any more");
        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "UltimateUpdater.Tests", "Fixtures", "nvidia-product-lookup.xml"));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", 1006)]
    [InlineData("NVIDIA GeForce RTX 4060 Laptop GPU", 1007)]
    [InlineData("NVIDIA GeForce RTX 3060 Laptop GPU", 940)]
    public void A_laptop_adapter_resolves_to_the_notebook_product(string wmiName, int expected) =>
        Assert.Equal(expected, NvidiaDriverLookup.FindProductId(Fixture(), wmiName));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4070", 1015)]
    [InlineData("NVIDIA GeForce RTX 4060", 1023)]
    [InlineData("NVIDIA GeForce RTX 3060", 942)]
    public void A_desktop_adapter_resolves_to_the_desktop_product(string wmiName, int expected) =>
        Assert.Equal(expected, NvidiaDriverLookup.FindProductId(Fixture(), wmiName));

    [Fact]
    public void A_notebook_part_without_a_notebook_entry_resolves_to_nothing()
    {
        const string desktopOnly = """
            <LookupValueSearch><LookupValues>
            <LookupValue ParentID="127"><Name>NVIDIA GeForce RTX 4070</Name><Value>1015</Value></LookupValue>
            </LookupValues></LookupValueSearch>
            """;

        Assert.Null(NvidiaDriverLookup.FindProductId(desktopOnly, "NVIDIA GeForce RTX 4070 Laptop GPU"));
    }

    [Theory]
    [InlineData("GeForce RTX 4070 Laptop GPU", true)]
    [InlineData("GeForce GTX 1060 with Max-Q Design", true)]
    [InlineData("GeForce GTX 1050 Ti Mobile", true)]
    [InlineData("Full driver CD for Notebook PCs", true)]
    [InlineData("GeForce RTX 4070", false)]
    [InlineData(null, false)]
    public void The_notebook_flag_reads_the_raw_name(string? name, bool expected) =>
        Assert.Equal(expected, NvidiaDriverLookup.IsNotebookName(name));
}
