using System.Net;
using System.Net.Sockets;
using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>CONTRACT UNDER TEST: the canary runner picks the endpoints its federated legs listen on as UDP ports, since
/// the World listens over QUIC, and a World that cannot bind the endpoint the runner picked fails its leg as
/// infrastructure, never as an UNSUPPORTED environment.</summary>
public sealed class CanaryListenerLawTests {
    [Fact]
    public void TheProbeHandsOutDistinctPortsAUdpSocketCanBind() {
        var ports = Enumerable.Range(
            count: 16,
            start: 0
        ).Select(selector: static _ => CanaryCommand.GetFreeLoopbackPort()).ToArray();

        Assert.Equal(
            actual: ports.Distinct().Count(),
            expected: ports.Length
        );

        foreach (var port in ports) {
            using var socket = new Socket(
                addressFamily: AddressFamily.InterNetwork,
                protocolType: ProtocolType.Udp,
                socketType: SocketType.Dgram
            );

            socket.Bind(localEP: new IPEndPoint(
                address: IPAddress.Loopback,
                port: port
            ));
        }
    }
}
