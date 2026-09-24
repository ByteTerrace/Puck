using System.Net;
using System.Net.Sockets;
using Puck.Cli.Canary;
using Puck.Launcher;
using Xunit;

namespace Puck.Cli.Tests;

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
    [Fact]
    public void AWorldThatCannotBindItsListenerIsAnInfrastructureFailureRatherThanUnsupported() {
        using var occupant = new Socket(
            addressFamily: AddressFamily.InterNetwork,
            protocolType: ProtocolType.Udp,
            socketType: SocketType.Dgram
        );

        occupant.Bind(localEP: new IPEndPoint(
            address: IPAddress.Loopback,
            port: 0
        ));

        using var leg = ScheduledWorldBoot.Leg();
        var refused = ScheduledWorldBoot.Run(
            legDirectory: leg.PathOf(name: "occupied"),
            options: ["--listen", occupant.LocalEndPoint!.ToString()!],
            script: "quit\n",
            world: "phase-fixture.world.json"
        );
        var transcript = new CanaryTranscript(
            RunDirectory: string.Empty,
            Stderr: refused.Stderr.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n'),
            Stdout: []
        );

        Assert.Equal(
            actual: refused.ExitCode,
            expected: LauncherHostRun.UnsupportedExitCode
        );
        Assert.NotNull(@object: CanaryCommand.ListenerRefusal(stderr: transcript.Stderr));
        Assert.Null(@object: CanaryCommand.UnsupportedReason(
            exitCode: refused.ExitCode,
            transcript: transcript
        ));

        // The same exit code with a device the machine lacks is still UNSUPPORTED, and not a listener refusal.
        var device = new CanaryTranscript(
            RunDirectory: string.Empty,
            Stderr: ["[world.host: unsupported: vulkan device unavailable: no adapter]"],
            Stdout: []
        );

        Assert.NotNull(@object: CanaryCommand.UnsupportedReason(
            exitCode: LauncherHostRun.UnsupportedExitCode,
            transcript: device
        ));
        Assert.Null(@object: CanaryCommand.ListenerRefusal(stderr: device.Stderr));
    }
}
