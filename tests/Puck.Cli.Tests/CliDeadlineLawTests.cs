using System.Net;
using System.Net.Quic;
using System.Net.Sockets;

using Puck.Cli.Automation;
using Puck.Cli.Bench;
using Puck.Testing;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>A verb's deadline runs on the clock its composition root hands it: work that never finishes ends exactly
/// when the deadline expires on a <see cref="VirtualClock"/>, not before.</summary>
public sealed class CliDeadlineLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A reference tool that never exits is killed when its timeout expires on the clock, and the run names
    /// the timeout.</summary>
    [Fact]
    public async Task ToolRun_IsKilledWhenItsTimeoutExpiresOnTheClock() {
        var clock = new VirtualClock();
        var timeout = TimeSpan.FromSeconds(seconds: 3);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 600" })
            : ("/bin/sh", new[] { "-c", "sleep 600" }));
        var run = ReferenceTools.RunAsync(
            arguments: arguments,
            cancellationToken: Token,
            clock: clock,
            executable: executable,
            standardInput: null,
            timeout: timeout
        );

        await clock.ExpireAsync(
            ct: Token,
            dueTime: timeout,
            pending: run
        );

        var (success, _, error) = await run;

        Assert.False(condition: success);
        Assert.Equal(
            actual: error,
            expected: "timed out after 3 seconds"
        );
    }
    /// <summary>A checked tool run that never exits is killed when its timeout expires on the clock and throws
    /// <see cref="TimeoutException"/> rather than reading as an exit code.</summary>
    [Fact]
    public async Task CheckedRun_ThrowsTimeoutWhenItsTimeoutExpiresOnTheClock() {
        var clock = new VirtualClock();
        var timeout = TimeSpan.FromSeconds(seconds: 3);

        var (executable, arguments) = (OperatingSystem.IsWindows()
            ? ("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 600" })
            : ("/bin/sh", new[] { "-c", "sleep 600" }));
        var run = CliProcess.RunCheckedAsync(
            arguments: arguments,
            cancellationToken: Token,
            capture: true,
            clock: clock,
            fileName: executable,
            workingDirectory: Environment.CurrentDirectory,
            timeout: timeout
        );

        await clock.ExpireAsync(
            ct: Token,
            dueTime: timeout,
            pending: run
        );

        _ = await Assert.ThrowsAsync<TimeoutException>(testCode: () => run);
    }
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
        var keyFile = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-probe-{Guid.NewGuid():N}.spki"
        );

        await File.WriteAllBytesAsync(
            bytes: new byte[91],
            cancellationToken: Token,
            path: keyFile
        );

        try {
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
        } finally {
            File.Delete(path: keyFile);
        }
    }
}
