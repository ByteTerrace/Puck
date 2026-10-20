using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Presentation.Tests;

public sealed partial class PortalInputLawTests {
    private static readonly ulong PortalStep = FixedTickConversion.PerRate(ratePerSecond: 30u);

    // The boot row over a real file, so the destination it references resolves beside it.
    private static (WorldInstance Instance, TemporaryDirectory StateDirectory) FileBackedRow(string name, string path, WorldDefinition definition) {
        var machines = new WorldMachineHost(
            engines: [],
            screens: definition.Screens
        );
        var stateDirectory = new TemporaryDirectory(prefix: $"puck-portal-input-{name}-");
        var server = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: name,
            machines: machines,
            narrationSink: new WorldConsoleNarrationSink(),
            population: new WorldPopulation(definition: definition),
            profiles: new WorldOwnedWorlds(
                directory: stateDirectory.RootPath,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        return (new WorldInstance(
            documentOrigin: new WorldFileOrigin(resolvedPath: path),
            federation: new WorldFederationIdentity(
                Authenticator: new InertAuthenticator(),
                Subject: server.AuthorityIdentity
            ),
            link: new LoopbackTransport(server: server),
            name: name,
            origin: () => path,
            ownedMachines: machines,
            server: server
        ), stateDirectory);
    }
}
