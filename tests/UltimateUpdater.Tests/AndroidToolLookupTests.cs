using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;
using Xunit;

namespace UltimateUpdater.Tests;

/// <summary>A tool that was not there on one scan must be looked for again on the next.</summary>
public class AndroidToolLookupTests
{
    [Fact]
    public void A_missing_tool_is_resolved_again_and_a_found_one_is_cached()
    {
        var answers = new Queue<string?>([null, @"C:\sdk\sdkmanager.bat", @"C:\other\sdkmanager.bat"]);
        var cache = new FoundOnceCache<string?>(answers.Dequeue, path => path is not null);

        Assert.Null(cache.Value);
        Assert.Equal(@"C:\sdk\sdkmanager.bat", cache.Value);
        Assert.Equal(@"C:\sdk\sdkmanager.bat", cache.Value);
        Assert.Single(answers);
    }
}
