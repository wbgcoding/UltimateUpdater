using System.Net;
using System.Net.Http;
using UltimateUpdater.Services.Sources;

namespace UltimateUpdater.Tests;

/// <summary>
/// The SSRF guard in front of <see cref="RegistryEnrichment.HeadContentLengthAsync"/>.
/// Composer/RubyGems/Scoop hand this a download URL straight out of a third-party manifest, a domain
/// allowlist (the NVIDIA pattern) does not fit here, because every app vendor legitimately hosts its own
/// binary wherever it likes. A range check instead: reject the address ranges nothing legitimate would
/// ever need this HEAD request to reach.
/// </summary>
public class RegistryEnrichmentTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("140.82.112.3")] // a real github.com address range
    public void A_real_public_IPv4_address_is_allowed(string ip) =>
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // the cloud metadata service address, the one that matters most
    [InlineData("169.254.0.1")]
    [InlineData("0.0.0.0")]
    public void A_private_loopback_or_link_local_IPv4_address_is_refused(string ip) =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("172.15.255.255")] // just below the 172.16.0.0/12 range
    [InlineData("172.32.0.0")]     // just above it
    public void Addresses_just_outside_the_private_ranges_are_allowed(string ip) =>
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void IPv6_loopback_is_refused() =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.IPv6Loopback));

    [Theory]
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local (ULA)
    [InlineData("fd12:3456:789a::1")] // unique local
    public void IPv6_link_local_and_unique_local_addresses_are_refused(string ip) =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void A_real_public_IPv6_address_is_allowed() =>
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("2606:4700:4700::1111"))); // Cloudflare DNS

    [Fact]
    public void An_IPv4_mapped_IPv6_loopback_is_unwrapped_and_refused() =>
        // ::ffff:127.0.0.1, the classic way to smuggle a loopback address past a naive IPv6-only check.
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("::ffff:127.0.0.1")));

    [Fact]
    public async Task An_unresolvable_hostname_is_never_treated_as_public() =>
        Assert.False(await RegistryEnrichment.IsPublicHostAsync(
            $"this-host-does-not-exist-{Guid.NewGuid():N}.invalid", CancellationToken.None));

    [Fact]
    public async Task Localhost_resolves_to_loopback_and_is_refused() =>
        Assert.False(await RegistryEnrichment.IsPublicHostAsync("localhost", CancellationToken.None));

    // ---- CGNAT and IPv6 special-purpose ranges ----

    [Theory]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.100")]
    [InlineData("100.127.255.255")]
    public void CGNAT_addresses_are_refused(string ip) =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("100.63.255.255")] // just below 100.64.0.0/10
    [InlineData("100.128.0.0")]    // just above it
    public void Addresses_just_outside_the_CGNAT_range_are_allowed(string ip) =>
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("192.0.2.1")]      // TEST-NET-1
    [InlineData("198.51.100.1")]   // TEST-NET-2
    [InlineData("203.0.113.1")]    // TEST-NET-3
    [InlineData("198.18.0.1")]     // benchmarking
    [InlineData("224.0.0.1")]      // multicast
    [InlineData("255.255.255.255")]
    public void IPv4_documentation_benchmarking_and_multicast_ranges_are_refused(string ip) =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("2001:db8::1")]    // documentation
    [InlineData("2001::1")]        // Teredo
    public void IPv6_documentation_and_teredo_ranges_are_refused(string ip) =>
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse(ip)));

    [Fact]
    public void A_6to4_address_embedding_a_private_IPv4_target_is_refused() =>
        // 2002:0a00:0001:: = 6to4 wrapping 10.0.0.1, the tunnel must not smuggle a private target past
        // the outer "looks like Global Unicast" check.
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("2002:0a00:0001::")));

    [Fact]
    public void A_6to4_address_embedding_a_public_IPv4_target_is_allowed() =>
        // 2002:0808:0808:: = 6to4 wrapping 8.8.8.8
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("2002:0808:0808::")));

    [Fact]
    public void A_NAT64_address_embedding_a_private_IPv4_target_is_refused() =>
        // 64:ff9b::10.0.0.1 = NAT64 well-known prefix wrapping a private address.
        Assert.False(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("64:ff9b::10.0.0.1")));

    [Fact]
    public void A_NAT64_address_embedding_a_public_IPv4_target_is_allowed() =>
        Assert.True(RegistryEnrichment.IsPublicAddress(IPAddress.Parse("64:ff9b::8.8.8.8")));

    /// <summary>
    /// The address check at connect time is what closes the gap between validating a host name and dialling
    /// it. A listener on the loopback address stands in for an internal service: the probe client must be
    /// refused before any connection reaches it.
    /// </summary>
    [Fact]
    public async Task The_probe_client_never_dials_a_loopback_address()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RegistryEnrichment.NoRedirectClient.GetAsync(
                $"http://127.0.0.1:{port}/", HttpCompletionOption.ResponseHeadersRead));

        Assert.False(listener.Pending());
    }

    // Redirect-hop host validation is not covered by an automated test here: HeadContentLengthAsync
    // requires HTTPS on the initial URL, and a local test listener would need a bound TLS certificate to
    // exercise the real SendAsync path, disproportionate for what the code is
    // (HttpClientHandler.AllowAutoRedirect = false, a well-understood, well-tested .NET switch).
    // Verified by reading: NoRedirectClient never auto-follows, and every hop's Location host is checked
    // through the same IsPublicHostAsync used above before the next request goes out.
}
