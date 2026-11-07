using UltimateUpdater.Services;

namespace UltimateUpdater.Tests;

/// <summary>A file hash can be listed by several catalogs; a superseded first one must not decide.</summary>
public class TrustedBinaryCatalogLoopTests
{
    private static (bool Result, List<IntPtr> Released, List<IntPtr> Verified) Run(bool[] verdicts)
    {
        var handles = verdicts.Select((_, index) => (IntPtr)(index + 1)).ToArray();
        var released = new List<IntPtr>();
        var verified = new List<IntPtr>();

        var result = TrustedBinary.AnyCatalogVerifies(
            previous =>
            {
                var next = previous == IntPtr.Zero ? 0 : (int)previous;
                return next < handles.Length ? handles[next] : IntPtr.Zero;
            },
            handle =>
            {
                verified.Add(handle);
                return verdicts[(int)handle - 1];
            },
            released.Add);

        return (result, released, verified);
    }

    [Fact]
    public void A_failing_first_catalog_is_followed_by_the_next_one()
    {
        var (result, released, verified) = Run([false, true]);

        Assert.True(result);
        Assert.Equal([(IntPtr)1, (IntPtr)2], verified);
        // The first context is handed back by the enumeration itself; only the accepted one is released here.
        Assert.Equal([(IntPtr)2], released);
    }

    [Fact]
    public void Every_catalog_failing_is_a_refusal()
    {
        var (result, released, verified) = Run([false, false, false]);

        Assert.False(result);
        Assert.Equal(3, verified.Count);
        Assert.Empty(released);
    }

    [Fact]
    public void No_catalog_at_all_is_a_refusal_without_verifying_anything()
    {
        var (result, _, verified) = Run([]);

        Assert.False(result);
        Assert.Empty(verified);
    }

    [Fact]
    public void A_verifier_that_throws_releases_its_context_and_propagates()
    {
        var released = new List<IntPtr>();

        Assert.Throws<InvalidOperationException>(() => TrustedBinary.AnyCatalogVerifies(
            previous => previous == IntPtr.Zero ? (IntPtr)7 : IntPtr.Zero,
            _ => throw new InvalidOperationException(),
            released.Add));

        Assert.Equal([(IntPtr)7], released);
    }
}
