using System.Net;
using System.Net.NetworkInformation;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A QUIC dial to a loopback peer binds its own socket on loopback too, never on every interface: a socket
/// on every interface raises a firewall prompt on a local run that only ever talks to itself. The acceptor sees the
/// dialer's port as its connection's remote port, and the operating system's UDP table names the address bound
/// there.</summary>
public sealed class LoopbackDialLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ADialToALoopbackPeerBindsLoopbackOnly(bool ipv6) {
        var ct = TestContext.Current.CancellationToken;
        var loopback = (ipv6
            ? IPAddress.IPv6Loopback
            : IPAddress.Loopback
        );

        // NewTransport skips the law on a host without QUIC, so the transports are used through the platform-neutral seam.
        await using IPeerTransport acceptor = PeerTestSupport.NewTransport(certificate: PeerIdentity.Create().CreateTransportCertificate());
        await using IPeerTransport dialer = PeerTestSupport.NewTransport(certificate: PeerIdentity.Create().CreateTransportCertificate());
        await using var listener = await acceptor.ListenAsync(
            ct: ct,
            endpoint: new IPEndPoint(
                address: loopback,
                port: 0
            )
        );
        var accepting = listener.AcceptAsync(ct: ct);

        await using var dialed = await dialer.DialAsync(
            ct: ct,
            endpoint: listener.LocalEndpoint
        );
        await using var accepted = (await accepting
            ?? throw new InvalidOperationException(message: "the listener closed before it accepted the dial"));
        var port = ((IPEndPoint)accepted.RemoteEndpoint).Port;
        var bound = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Where(predicate: endpoint => (endpoint.Port == port)).ToArray();

        Assert.NotEmpty(collection: bound);
        Assert.All(
            action: endpoint => Assert.True(
                condition: IPAddress.IsLoopback(address: endpoint.Address),
                userMessage: $"the dialer bound {endpoint}, not loopback"
            ),
            collection: bound
        );
    }
}
