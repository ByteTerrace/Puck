using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

using Puck.Cli.Automation;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>A verb's deadline runs on the clock its composition root hands it: work that never finishes ends exactly
/// when the deadline expires on a <see cref="VirtualClock"/>, not before.</summary>
public sealed class WorldProbeDeadlineLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A world endpoint that never answers the QUIC handshake fails the probe when
    /// <see cref="WorldProbeCommand.ProbeTimeout"/> expires on the clock.</summary>
    [Fact]
    public async Task WorldProbe_FailsWhenItsTimeoutExpiresOnTheClock() {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicConnection.IsSupported) {
            Assert.Skip(reason: "QUIC is not available on this host.");
        }

        using var silent = new UdpClient(localEP: new IPEndPoint(
            address: IPAddress.Loopback,
            port: 0
        ));
        using var scratch = new TemporaryDirectory(prefix: "puck-probe-");
        var keyFile = scratch.PathOf(name: "probe.spki");

        await File.WriteAllBytesAsync(
            bytes: new byte[91],
            cancellationToken: Token,
            path: keyFile
        );

        var clock = new VirtualClock();
        var probe = WorldProbeCommand.RunAsync(
            clock: clock,
            host: IPAddress.Loopback.ToString(),
            keyFile: keyFile,
            port: ((IPEndPoint)silent.Client.LocalEndPoint!).Port
        );

        await clock.ExpireAsync(
            ct: Token,
            dueTime: WorldProbeCommand.ProbeTimeout,
            pending: probe
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => probe);
    }
}
