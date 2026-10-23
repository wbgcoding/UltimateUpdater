using System.Runtime.InteropServices;
using UltimateUpdater.Services;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// Late-bound COM reads surface a callee's COMException wrapped in a TargetInvocationException. One update
/// whose property cannot be read must cost that one row, never the whole provider.
/// </summary>
public class WindowsUpdateMappingTests
{
    [Fact]
    public void An_update_with_an_unreadable_property_is_skipped_without_throwing()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        using var com = new WindowsUpdateSource.ComScope();

        var item = source.TryMapUpdate(new FakeUpdate { ThrowOnTitle = true }, com);

        Assert.Null(item);
    }

    [Fact]
    public void An_update_with_readable_properties_maps_to_an_item()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        using var com = new WindowsUpdateSource.ComScope();

        var item = source.TryMapUpdate(new FakeUpdate(), com);

        Assert.NotNull(item);
        Assert.Equal("Security Update for Windows (KB5001234)", item!.Name);
        Assert.Equal("00000000-0000-0000-0000-000000000001:200", item.Id);
    }

    [Fact]
    public void An_exception_that_is_not_a_COM_error_still_propagates()
    {
        var source = new WindowsUpdateSource(new LoggingService());
        using var com = new WindowsUpdateSource.ComScope();

        Assert.ThrowsAny<Exception>(() => source.TryMapUpdate(new FakeUpdate { ThrowInvalidOnTitle = true }, com));
    }

    /// <summary>Shape of the WUA update object, as far as the mapper reads it.</summary>
    private sealed class FakeUpdate
    {
        public bool ThrowOnTitle { get; init; }

        public bool ThrowInvalidOnTitle { get; init; }

        public string Title =>
            ThrowOnTitle ? throw new COMException("unreadable", unchecked((int)0x80240016))
            : ThrowInvalidOnTitle ? throw new InvalidOperationException("not a COM error")
            : "Security Update for Windows (KB5001234)";

        public object? Categories => null;

        public object Identity { get; } = new FakeIdentity();

        public string? MsrcSeverity => null;

        public bool RebootRequired => false;

        public string? Description => null;

        public object? MoreInfoUrls => null;

        public int Type => 1;

        public decimal MaxDownloadSize => 0;

        public DateTime LastDeploymentChangeTime => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeIdentity
    {
        public string UpdateID => "00000000-0000-0000-0000-000000000001";

        public int RevisionNumber => 200;
    }
}
