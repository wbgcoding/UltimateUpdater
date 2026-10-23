using System.Text.Json;
using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>Shared by <c>UpdateManifest</c> and <c>WingetUpgradeParser</c>, which had byte-identical copies
/// of this before this consolidation.</summary>
public class JsonElementHelpersTests
{
    [Fact]
    public void A_present_string_property_is_returned()
    {
        var element = JsonDocument.Parse("""{"name":"value"}""").RootElement;

        Assert.Equal("value", JsonElementHelpers.GetString(element, "name"));
    }

    [Fact]
    public void A_missing_property_returns_empty()
    {
        var element = JsonDocument.Parse("""{}""").RootElement;

        Assert.Equal(string.Empty, JsonElementHelpers.GetString(element, "name"));
    }

    [Fact]
    public void A_non_string_property_returns_empty()
    {
        var element = JsonDocument.Parse("""{"name":42}""").RootElement;

        Assert.Equal(string.Empty, JsonElementHelpers.GetString(element, "name"));
    }
}
