
namespace UltimateUpdater.Tests;

public class MeteredConnectionTests
{
    /// <summary>NLM_CONNECTION_COST flags: Fixed (0x2) and Variable (0x4) are metered; Unrestricted (0x1),
    /// Unknown (0) and the extra state bits alone (roaming 0x40000, over the limit 0x10000) are not.</summary>
    [Theory]
    [InlineData(0x0u, false)]
    [InlineData(0x1u, false)]
    [InlineData(0x2u, true)]
    [InlineData(0x4u, true)]
    [InlineData(0x40001u, false)]
    [InlineData(0x10002u, true)]
    public void Only_a_fixed_or_variable_cost_counts_as_metered(uint cost, bool expected)
    {
        Assert.Equal(expected, MeteredConnection.IsMeteredCost(cost));
    }

    /// <summary>The live probe goes through COM; on any machine it answers without throwing.</summary>
    [Fact]
    public void The_live_probe_answers_without_throwing()
    {
        _ = MeteredConnection.IsMetered();
    }
}
