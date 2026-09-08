using System.Net;
using Xunit;

namespace Puck.Networking.Tests;

public sealed class PeerEndpointTests {
    [InlineData("localhost:33333")]
    [InlineData("127.0.0.1:33333")]
    [InlineData("[::1]:33333")]
    [Theory]
    public void FormattingRoundTripsWithoutDnsResolution(string address) {
        Assert.True(PeerEndpoint.TryParse(address, out var endpoint));
        Assert.Equal(address, PeerEndpoint.Format(endpoint));
        Assert.True(PeerEndpoint.TryParse(PeerEndpoint.Format(endpoint), out var parsed));
        Assert.Equal(endpoint, parsed);
    }
    [InlineData("puck.puck.byteterrace.com:33333")]
    [InlineData("localhost:33333")]
    [Theory]
    public void KeepsDnsUnresolvedUntilDial(string address) {
        Assert.True(PeerEndpoint.TryParse(address, out var endpoint));
        Assert.IsType<DnsEndPoint>(endpoint);
    }
    [InlineData("127.0.0.1:33333")]
    [InlineData("[::1]:33333")]
    [Theory]
    public void PreservesNumericRoutes(string address) {
        Assert.True(PeerEndpoint.TryParse(address, out var endpoint));
        Assert.IsType<IPEndPoint>(endpoint);
    }
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("user@localhost:33333")]
    [InlineData("localhost:33333/path")]
    [InlineData("localhost:33333?x=1")]
    [Theory]
    public void RefusesIncompleteOrUrlShapedRoutes(string? address) => Assert.False(PeerEndpoint.TryParse(address, out _));
}
