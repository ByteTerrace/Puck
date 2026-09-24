using System.Net;
using System.Net.Sockets;
using Puck.Launcher;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldListenRefusalLawTests {
    private const string World = "phase-fixture.world.json";

    // Law: a World booted onto a listen endpoint another socket already holds ends the way every environment that
    // cannot run the boot does — one "[world.host: unsupported: quic listener <endpoint> unavailable: …]" line and the
    // unsupported exit code, never an unhandled exception and never a bound line. The same boot onto a free endpoint
    // binds, says so, and quits 0 on its script.
    [Fact]
    public void AWorldBootedOntoAnOccupiedEndpointRefusesByNameWithTheUnsupportedExitCode() {
        using var occupant = new Socket(
            addressFamily: AddressFamily.InterNetwork,
            protocolType: ProtocolType.Udp,
            socketType: SocketType.Dgram
        );

        occupant.Bind(localEP: new IPEndPoint(
            address: IPAddress.Loopback,
            port: 0
        ));
        var endpoint = occupant.LocalEndPoint!.ToString()!;
        using var occupiedLeg = ScheduledWorldBoot.Leg();
        var refused = ScheduledWorldBoot.Run(
            legDirectory: occupiedLeg.PathOf(name: "occupied"),
            options: ["--listen", endpoint],
            script: "quit\n",
            world: World
        );

        Assert.True(
            condition: (refused.ExitCode == LauncherHostRun.UnsupportedExitCode),
            userMessage: $"the occupied boot exited {refused.ExitCode}:{Environment.NewLine}{refused.Stderr}"
        );
        Assert.Contains(
            actualString: refused.Stderr,
            expectedSubstring: $"{LauncherHostRun.UnsupportedLinePrefix(label: "world")}quic listener {endpoint} unavailable: "
        );
        Assert.DoesNotContain(
            actualString: refused.Stderr,
            expectedSubstring: "[world.listen: bound "
        );
        Assert.DoesNotContain(
            actualString: refused.Stderr,
            expectedSubstring: "Unhandled exception"
        );

        using var freeLeg = ScheduledWorldBoot.Leg();
        var bound = ScheduledWorldBoot.Run(
            legDirectory: freeLeg.PathOf(name: "free"),
            options: ["--listen", "127.0.0.1:0"],
            script: "quit\n",
            world: World
        );

        Assert.True(
            condition: (bound.ExitCode == 0),
            userMessage: $"the free boot exited {bound.ExitCode}:{Environment.NewLine}{bound.Stderr}"
        );
        Assert.Contains(
            actualString: bound.Stderr,
            expectedSubstring: "[world.listen: bound 127.0.0.1:"
        );
    }
}
