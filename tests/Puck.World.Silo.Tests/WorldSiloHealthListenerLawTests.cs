using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using Puck.Abstractions;
using Puck.Launcher;
using Puck.Testing;

using Xunit;

namespace Puck.World.Silo.Tests;

/// <summary>The lifecycle health listener binds the address its silo document names, loopback by default, so a
/// local silo opens no socket on a network interface and raises no firewall prompt. The operating system's TCP
/// table names every address bound on the health port.</summary>
public sealed class WorldSiloHealthListenerLawTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static int FreeLoopbackPort() {
        using var probe = new Socket(
            addressFamily: AddressFamily.InterNetwork,
            protocolType: ProtocolType.Tcp,
            socketType: SocketType.Stream
        );

        probe.Bind(localEP: new IPEndPoint(
            address: IPAddress.Loopback,
            port: 0
        ));

        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }
    private static IPEndPoint[] ListenersOn(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(predicate: endpoint => (endpoint.Port == port)).ToArray();

    [InlineData(null, "127.0.0.1")]
    [InlineData("::1", "::1")]
    [Theory]
    public async Task TheHealthListenerBindsTheDocumentsAddressAndLoopbackByDefault(string? address, string expected) {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var port = FreeLoopbackPort();
        var silo = WorldSiloDeadlineLawTests.Silo(
            clock: new VirtualClock(),
            directory: directory.RootPath,
            lifecycle: ((address is null)
                ? new WorldSiloLifecycle(
                    HealthPort: port,
                    ShutdownSeconds: 5
                )
                : new WorldSiloLifecycle(
                    HealthAddress: address,
                    HealthPort: port,
                    ShutdownSeconds: 5
                )
            ),
            output: output,
            store: new FakeObjectBlobStore()
        );
        using var instances = silo.Instances;
        var service = new WorldSiloLifecycleService(
            extensions: PuckExtensionSet.Compose(extensions: []),
            lifetime: new WorldSiloDeadlineLawTests.Lifetime(),
            silo: silo
        );

        await service.StartAsync(cancellationToken: TestToken);
        try {
            var bound = ListenersOn(port: port);

            // The service binds on its own task; the law waits for the bind, bounded only by the test's token.
            while (bound.Length == 0) {
                if (service.ExecuteTask is { IsCompleted: true } ended) {
                    await ended;
                    Assert.Fail(message: "the health listener stopped before it bound");
                }
                await Task.Delay(
                    cancellationToken: TestToken,
                    millisecondsDelay: 10
                );
                bound = ListenersOn(port: port);
            }
            Assert.Equal(
                actual: bound.Select(selector: static endpoint => endpoint.Address.ToString()).Distinct().ToArray(),
                expected: [expected]
            );
        } finally {
            await service.StopAsync(cancellationToken: TestToken);
        }
    }
}
