using System.Net;
using Xunit;

namespace Puck.Networking.Tests;

public sealed class PeerEndpointTests {
    [InlineData("localhost:7825")]
    [InlineData("127.0.0.1:7825")]
    [InlineData("[::1]:7825")]
    [Theory]
    public void FormattingRoundTripsWithoutDnsResolution(string address) {
        Assert.True(condition: PeerEndpoint.TryParse(
            endpoint: out var endpoint,
            value: address
        ));
        Assert.Equal(
            address,
            PeerEndpoint.Format(endpoint: endpoint)
        );
        Assert.True(condition: PeerEndpoint.TryParse(
            PeerEndpoint.Format(endpoint: endpoint),
            out var parsed
        ));
        Assert.Equal(
            actual: parsed,
            expected: endpoint
        );
    }
    [InlineData("play.puck.byteterrace.com:7825")]
    [InlineData("localhost:7825")]
    [Theory]
    public void KeepsDnsUnresolvedUntilDial(string address) {
        Assert.True(condition: PeerEndpoint.TryParse(
            endpoint: out var endpoint,
            value: address
        ));
        Assert.IsType<DnsEndPoint>(@object: endpoint);
    }
    [InlineData("127.0.0.1:7825")]
    [InlineData("[::1]:7825")]
    [Theory]
    public void PreservesNumericRoutes(string address) {
        Assert.True(condition: PeerEndpoint.TryParse(
            endpoint: out var endpoint,
            value: address
        ));
        Assert.IsType<IPEndPoint>(@object: endpoint);
    }
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:65536")]
    [InlineData("user@localhost:7825")]
    [InlineData("localhost:7825/path")]
    [InlineData("localhost:7825?x=1")]
    [Theory]
    public void RefusesIncompleteOrUrlShapedRoutes(string? address) => Assert.False(condition: PeerEndpoint.TryParse(
        endpoint: out _,
        value: address
    ));
}
